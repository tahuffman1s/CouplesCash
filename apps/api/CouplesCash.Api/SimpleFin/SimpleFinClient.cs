using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace CouplesCash.Api.SimpleFin;

/// <summary>The two Bridge calls the app makes. Faked in tests; the real one is <see cref="SimpleFinClient"/>.</summary>
public interface ISimpleFinClient
{
    /// <summary>Decodes the setup token (base64 claim URL), POSTs to it with an empty body and returns the access URL (a credential).</summary>
    Task<string> ClaimAsync(string setupToken, CancellationToken ct);

    /// <summary><c>GET {accessUrl}/accounts?start-date&amp;end-date&amp;pending=1</c>; <paramref name="endExclusive"/> follows the protocol (transactions before, not on, that day).</summary>
    Task<SimpleFinAccountSet> GetAccountsAsync(string accessUrl, DateOnly startDate, DateOnly endExclusive, bool includePending, CancellationToken ct);
}

/// <summary>
/// SimpleFIN protocol client (https://www.simplefin.org/protocol.html). The access URL carries HTTP Basic credentials in
/// its userinfo; it is never logged (see <see cref="Mask"/>) and never leaves this class except as the encrypted blob
/// the caller stores. <c>SimpleFin__BaseUrl</c> rewrites both URLs to a local fake server.
/// </summary>
public sealed class SimpleFinClient(IHttpClientFactory httpClientFactory, SimpleFinOptions options, TimeProvider time, ILogger<SimpleFinClient> logger) : ISimpleFinClient
{
    public const string HttpClientName = "simplefin";

    public async Task<string> ClaimAsync(string setupToken, CancellationToken ct)
    {
        var claimUrl = Rewrite(DecodeSetupToken(setupToken), options.BaseUrl);
        logger.LogInformation("SimpleFIN claim at {ClaimUrl}", Mask(claimUrl));

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, claimUrl) { Content = new ByteArrayContent([]) };
            request.Content.Headers.ContentLength = 0;
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SimpleFinException(SimpleFinError.Unreachable, "Could not reach the SimpleFIN Bridge to claim the token.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new SimpleFinException(SimpleFinError.InvalidToken, "This setup token was already claimed or has expired. Create a new one at the Bridge.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SimpleFinException(SimpleFinError.Unreachable, $"The SimpleFIN Bridge answered {(int)response.StatusCode} while claiming the token.");
            }

            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (!Uri.TryCreate(body, UriKind.Absolute, out var accessUrl) || accessUrl.Scheme is not ("https" or "http") || string.IsNullOrEmpty(accessUrl.UserInfo))
            {
                throw new SimpleFinException(SimpleFinError.BadResponse, "The Bridge did not return an access URL.");
            }

            if (accessUrl.Scheme == "http" && options.BaseUrl is null && !accessUrl.IsLoopback)
            {
                throw new SimpleFinException(SimpleFinError.BadResponse, "The Bridge returned a plain-http access URL; refusing to store credentials for it.");
            }

            return accessUrl.ToString();
        }
    }

    public async Task<SimpleFinAccountSet> GetAccountsAsync(string accessUrl, DateOnly startDate, DateOnly endExclusive, bool includePending, CancellationToken ct)
    {
        var (endpoint, basicAuth) = AccountsEndpoint(accessUrl, startDate, endExclusive, includePending, options.BaseUrl);
        logger.LogDebug("SimpleFIN GET {Url}", Mask(endpoint));

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SimpleFinException(SimpleFinError.Unreachable, "Could not reach the SimpleFIN Bridge.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                    throw new SimpleFinException(SimpleFinError.AccessDenied, "The Bridge rejected the access URL (revoked?). Unlink and link again.");
                case HttpStatusCode.PaymentRequired:
                    throw new SimpleFinException(SimpleFinError.PaymentRequired, "The SimpleFIN Bridge subscription needs renewing.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SimpleFinException(SimpleFinError.Unreachable, $"The SimpleFIN Bridge answered {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return SimpleFinParser.Parse(json, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Pure helpers (unit-tested)
    // ---------------------------------------------------------------------------------------------

    /// <summary>The setup token is base64 (standard or url-safe, padding optional) of an absolute http(s) claim URL.</summary>
    public static Uri DecodeSetupToken(string setupToken)
    {
        var compact = new string((setupToken ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
        if (compact.Length == 0)
        {
            throw new SimpleFinException(SimpleFinError.InvalidToken, "Setup token is empty.");
        }

        var padded = compact.PadRight(compact.Length + (4 - compact.Length % 4) % 4, '=');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException ex)
        {
            throw new SimpleFinException(SimpleFinError.InvalidToken, "Setup token is not valid base64.", ex);
        }

        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new SimpleFinException(SimpleFinError.InvalidToken, "Setup token does not decode to a claim URL.");
        }

        return url;
    }

    /// <summary>With a base URL override, keep path/query/userinfo but point at the override's scheme, host and port.</summary>
    public static Uri Rewrite(Uri url, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var target))
        {
            return url;
        }

        var builder = new UriBuilder(url) { Scheme = target.Scheme, Host = target.Host, Port = target.Port };
        return builder.Uri;
    }

    /// <summary>Splits the access URL into the <c>/accounts</c> endpoint (no userinfo) and the Basic auth parameter.</summary>
    public static (Uri Endpoint, string BasicAuth) AccountsEndpoint(string accessUrl, DateOnly startDate, DateOnly endExclusive, bool includePending, string? baseUrl)
    {
        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.UserInfo))
        {
            throw new SimpleFinException(SimpleFinError.AccessDenied, "Stored access URL is malformed.");
        }

        var userInfo = Uri.UnescapeDataString(url.UserInfo);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo));

        var rewritten = Rewrite(url, baseUrl);
        var builder = new UriBuilder(rewritten) { UserName = "", Password = "" };
        builder.Path = builder.Path.TrimEnd('/') + "/accounts";
        var query = new List<string>
        {
            $"start-date={ToUnix(startDate)}",
            $"end-date={ToUnix(endExclusive)}",
        };
        if (includePending)
        {
            query.Add("pending=1");
        }

        builder.Query = string.Join('&', query);
        return (builder.Uri, basic);
    }

    /// <summary>"https://user:pass@bridge/simplefin" → "https://***@bridge/simplefin" for logs and error messages.</summary>
    public static string Mask(Uri url) => string.IsNullOrEmpty(url.UserInfo)
        ? url.ToString()
        : new UriBuilder(url) { UserName = "***", Password = "" }.Uri.ToString().Replace("***:@", "***@", StringComparison.Ordinal);

    public static string Mask(string url) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? Mask(parsed) : "<invalid url>";

    private static string ToUnix(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
