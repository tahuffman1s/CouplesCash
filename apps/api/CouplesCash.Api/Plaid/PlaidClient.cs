using System.Net;
using System.Text;
using System.Text.Json;
using CouplesCash.Api.SimpleFin;

namespace CouplesCash.Api.Plaid;

/// <summary>A Link token for Plaid Link in the browser (short-lived; safe to hand to the page).</summary>
public sealed record PlaidLinkToken(string LinkToken, DateTime Expiration);

/// <summary>The Plaid calls the app makes. Faked in tests; the real one is <see cref="PlaidClient"/>.</summary>
public interface IPlaidClient
{
    bool Configured { get; }

    /// <summary><c>/link/token/create</c>; with <paramref name="accessToken"/> it opens Link in update mode (sign in again).</summary>
    Task<PlaidLinkToken> CreateLinkTokenAsync(string clientUserId, string? accessToken, CancellationToken ct);

    /// <summary><c>/item/public_token/exchange</c> → the access token (a credential) and the Item id.</summary>
    Task<(string AccessToken, string ItemId)> ExchangeAsync(string publicToken, CancellationToken ct);

    /// <summary>
    /// The Item's accounts and the transactions dated [<paramref name="start"/>, <paramref name="end"/>] (<c>/transactions/get</c>, all
    /// pages; balances are Plaid's cached ones, dated by <c>/item/get</c>'s last successful update). While Plaid is still loading a new
    /// Item (<c>PRODUCT_NOT_READY</c>) the accounts come from <c>/accounts/get</c> with no transactions and a "still loading" error.
    /// </summary>
    Task<SimpleFinAccountSet> GetFeedAsync(string accessToken, string orgName, DateOnly start, DateOnly end, DateOnly today, CancellationToken ct);

    /// <summary><c>/transactions/refresh</c>: asks Plaid to fetch from the bank now (results arrive a few minutes later).</summary>
    Task RefreshAsync(string accessToken, CancellationToken ct);

    /// <summary><c>/item/remove</c>: revokes the access token at Plaid.</summary>
    Task RemoveAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// Plaid API client (https://plaid.com/docs/api/). Keys go in the <c>PLAID-CLIENT-ID</c> / <c>PLAID-SECRET</c> headers; request and
/// response bodies (access tokens, public tokens) are never logged — only endpoint names and Plaid error codes.
/// </summary>
public sealed class PlaidClient(IHttpClientFactory httpClientFactory, PlaidOptions options, ILogger<PlaidClient> logger) : IPlaidClient
{
    public const string HttpClientName = "plaid";
    public const string ApiVersion = "2020-09-14";

    /// <summary>Plaid's page size limit for <c>/transactions/get</c>.</summary>
    public const int PageSize = 500;

    /// <summary>A first sync asks for at most this many pages (10 000 transactions).</summary>
    public const int MaxPages = 20;

    public const string StillLoading = "Plaid is still loading transactions from the bank; they will appear within a few minutes.";

    public bool Configured => options.Configured;

    public async Task<PlaidLinkToken> CreateLinkTokenAsync(string clientUserId, string? accessToken, CancellationToken ct)
    {
        Dictionary<string, object?> Body(bool withRedirect)
        {
            var body = new Dictionary<string, object?>
            {
                ["client_name"] = "CouplesCash",
                ["language"] = "en",
                ["country_codes"] = new[] { "US" },
                ["user"] = new Dictionary<string, object?> { ["client_user_id"] = clientUserId },
            };
            if (accessToken is null)
            {
                body["products"] = new[] { "transactions" };
                body["transactions"] = new Dictionary<string, object?> { ["days_requested"] = 90 };
            }
            else
            {
                body["access_token"] = accessToken;
            }

            if (withRedirect && options.RedirectUri is { } redirect)
            {
                body["redirect_uri"] = redirect;
            }

            return body;
        }

        JsonElement root;
        try
        {
            root = await PostAsync("/link/token/create", Body(withRedirect: true), ct);
        }
        catch (PlaidApiException ex) when (options.RedirectUri is not null && ex.Body.Code == "INVALID_FIELD" && ex.Body.Message.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase))
        {
            // Not listed under Allowed redirect URIs in the dashboard: Link still works without it (OAuth banks open in a pop-up).
            logger.LogWarning("Plaid rejected the OAuth redirect URI {RedirectUri}; add it under Allowed redirect URIs in the Plaid dashboard. Continuing without it.", options.RedirectUri);
            root = await PostAsync("/link/token/create", Body(withRedirect: false), ct);
        }

        var token = root.TryGetProperty("link_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new SimpleFinException(SimpleFinError.BadResponse, "Plaid did not return a link token.");
        }

        var expiration = root.TryGetProperty("expiration", out var e) && e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), out var at)
            ? at.ToUniversalTime()
            : DateTime.UtcNow.AddHours(4);
        return new PlaidLinkToken(token, expiration);
    }

    public async Task<(string AccessToken, string ItemId)> ExchangeAsync(string publicToken, CancellationToken ct)
    {
        var root = await PostAsync("/item/public_token/exchange", new Dictionary<string, object?> { ["public_token"] = publicToken }, ct);
        var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        var item = root.TryGetProperty("item_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(item))
        {
            throw new SimpleFinException(SimpleFinError.BadResponse, "Plaid did not return an access token.");
        }

        return (access, item);
    }

    public async Task<SimpleFinAccountSet> GetFeedAsync(string accessToken, string orgName, DateOnly start, DateOnly end, DateOnly today, CancellationToken ct)
    {
        var item = await PostAsync("/item/get", new Dictionary<string, object?> { ["access_token"] = accessToken }, ct);
        var balanceAt = PlaidParser.LastSuccessfulUpdate(item) ?? DateTime.UtcNow;
        var name = PlaidParser.InstitutionName(item) ?? orgName;

        var accounts = new List<JsonElement>();
        var transactions = new List<JsonElement>();
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var body = new Dictionary<string, object?>
                {
                    ["access_token"] = accessToken,
                    ["start_date"] = start.ToString("yyyy-MM-dd"),
                    ["end_date"] = end.ToString("yyyy-MM-dd"),
                    ["options"] = new Dictionary<string, object?>
                    {
                        ["count"] = PageSize,
                        ["offset"] = transactions.Count,
                        ["include_original_description"] = true,
                    },
                };
                var parsed = PlaidParser.ParseTransactionsPage(await PostAsync("/transactions/get", body, ct));
                if (page == 0)
                {
                    accounts.AddRange(parsed.Accounts);
                }

                transactions.AddRange(parsed.Transactions);
                if (parsed.Transactions.Count == 0 || transactions.Count >= parsed.Total)
                {
                    break;
                }
            }
        }
        catch (PlaidApiException ex) when (ex.Body.Code == "PRODUCT_NOT_READY")
        {
            var accountsOnly = await PostAsync("/accounts/get", new Dictionary<string, object?> { ["access_token"] = accessToken }, ct);
            return PlaidParser.ToAccountSet(PlaidParser.ParseTransactionsPage(accountsOnly).Accounts, [], name, balanceAt, today, [StillLoading]);
        }

        return PlaidParser.ToAccountSet(accounts, transactions, name, balanceAt, today, []);
    }

    public async Task RefreshAsync(string accessToken, CancellationToken ct) =>
        await PostAsync("/transactions/refresh", new Dictionary<string, object?> { ["access_token"] = accessToken }, ct);

    public async Task RemoveAsync(string accessToken, CancellationToken ct) =>
        await PostAsync("/item/remove", new Dictionary<string, object?> { ["access_token"] = accessToken }, ct);

    private async Task<JsonElement> PostAsync(string path, Dictionary<string, object?> body, CancellationToken ct)
    {
        if (!options.Configured)
        {
            throw new SimpleFinException(SimpleFinError.Unreachable, "Plaid is not configured on this server (Plaid__ClientId / Plaid__Secret).");
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.ResolvedBaseUrl + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("PLAID-CLIENT-ID", options.ClientId);
            request.Headers.Add("PLAID-SECRET", options.Secret);
            request.Headers.Add("Plaid-Version", ApiVersion);
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SimpleFinException(SimpleFinError.Unreachable, "Could not reach Plaid.", ex);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new SimpleFinException(SimpleFinError.BadResponse, "Plaid answered with something that is not JSON.", ex);
                }
            }

            var error = PlaidParser.ParseError(json);
            logger.LogWarning("Plaid {Path} answered {Status} {Code}", path, (int)response.StatusCode, error?.Code ?? "-");
            if (error is null)
            {
                throw new SimpleFinException(SimpleFinError.Unreachable, $"Plaid answered {(int)response.StatusCode}.");
            }

            throw new PlaidApiException(error, Classify(error, response.StatusCode), Message(error));
        }
    }

    /// <summary>Plaid error code → what the app does about it (reconnect, re-link, retry later).</summary>
    public static SimpleFinError Classify(PlaidErrorBody error, HttpStatusCode status) => error.Code switch
    {
        "ITEM_LOGIN_REQUIRED" or "ITEM_LOCKED" or "INVALID_CREDENTIALS" or "INVALID_MFA" or "USER_SETUP_REQUIRED"
            or "MFA_NOT_SUPPORTED" or "PENDING_EXPIRATION" or "ACCESS_NOT_GRANTED" or "INSUFFICIENT_CREDENTIALS" => SimpleFinError.ReauthRequired,
        "INVALID_ACCESS_TOKEN" or "ITEM_NOT_FOUND" or "INVALID_API_KEYS" => SimpleFinError.AccessDenied,
        "INVALID_PUBLIC_TOKEN" or "INVALID_LINK_TOKEN" => SimpleFinError.InvalidToken,
        _ when error.Type is "INSTITUTION_ERROR" or "RATE_LIMIT_EXCEEDED" or "API_ERROR"
            || status is HttpStatusCode.TooManyRequests || (int)status >= 500 => SimpleFinError.Unreachable,
        _ => SimpleFinError.BadResponse,
    };

    public static string Message(PlaidErrorBody error) => error.Code switch
    {
        "ITEM_LOGIN_REQUIRED" or "ITEM_LOCKED" or "INVALID_CREDENTIALS" or "INVALID_MFA" or "PENDING_EXPIRATION"
            or "ACCESS_NOT_GRANTED" or "INSUFFICIENT_CREDENTIALS" => "The bank wants you to sign in again. Tap Reconnect.",
        "USER_SETUP_REQUIRED" => "The bank needs something done on its own website first (a new password or terms to accept), then tap Reconnect.",
        "INVALID_ACCESS_TOKEN" or "ITEM_NOT_FOUND" => "Plaid no longer knows this connection. Unlink it and connect the bank again.",
        "INVALID_API_KEYS" => "Plaid rejected this server's keys (check PLAID_CLIENT_ID / PLAID_SECRET and PLAID_ENV).",
        _ => string.IsNullOrWhiteSpace(error.DisplayMessage) ? $"Plaid: {error.Message} ({error.Code})" : error.DisplayMessage!,
    };
}

/// <summary>A Plaid error answer (keeps the parsed body so callers can tell PRODUCT_NOT_READY / INVALID_FIELD apart).</summary>
public sealed class PlaidApiException(PlaidErrorBody body, SimpleFinError error, string message) : SimpleFinException(error, message)
{
    public PlaidErrorBody Body { get; } = body;
}
