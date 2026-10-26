using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Matching;

/// <summary>The Python matcher service (docs/MATCHER-CONTRACT.md). Every failure surfaces as a <see cref="MatcherException"/>.</summary>
public interface IMatcherClient
{
    bool Enabled { get; }

    Task<MatcherHealth> HealthAsync(CancellationToken ct);

    Task<MatcherMatchResponse> MatchAsync(MatcherMatchRequest request, CancellationToken ct);

    Task<MatcherDetectResponse> DetectRecurringAsync(MatcherDetectRequest request, CancellationToken ct);
}

/// <summary>
/// Typed <see cref="HttpClient"/> for the matcher: base address <c>Matcher__BaseUrl</c>, <c>X-Matcher-Key: Matcher__Key</c> on every
/// call except <c>/health</c>, a 30 s timeout, and <c>/v1/match</c> split into requests of ≤ 1000 transactions / ≤ 2000 candidates.
/// 401 / 413 / 422 / 5xx / timeouts / connection errors / unparseable bodies become <see cref="MatcherException"/>; nothing from the
/// request (descriptions, names) is ever put in an exception message.
/// </summary>
public sealed class MatcherClient(HttpClient http, MatcherOptions options) : IMatcherClient
{
    public const string KeyHeader = "X-Matcher-Key";

    /// <summary>The service rejects bodies over 1 MB; strings are cut well below its 1024-character field limit.</summary>
    public const int MaxTextLength = 256;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => options.Enabled;

    public async Task<MatcherHealth> HealthAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri("health"));
        return await SendAsync<MatcherHealth>(request, authenticate: false, ct);
    }

    public async Task<MatcherMatchResponse> MatchAsync(MatcherMatchRequest request, CancellationToken ct)
    {
        if (request.Transactions.Count == 0)
        {
            return new MatcherMatchResponse(MatchDetail.Fuzzy, "", [], []);
        }

        var clean = request with { Transactions = request.Transactions.Select(Clip).ToList() };
        var parts = MatcherChunking.Split(clean, options.ChunkSize, options.MaxCandidates, MatchingRules.CandidateWindowDays);
        var responses = new List<MatcherMatchResponse>(parts.Count);
        foreach (var part in parts)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Uri("v1/match")) { Content = JsonContent.Create(part, options: Json) };
            var response = await SendAsync<MatcherMatchResponse>(message, authenticate: true, ct);
            responses.Add(response with { Matches = response.Matches ?? [], Categories = response.Categories ?? [] });
        }

        return MatcherChunking.Merge(responses);
    }

    public async Task<MatcherDetectResponse> DetectRecurringAsync(MatcherDetectRequest request, CancellationToken ct)
    {
        var clean = request with
        {
            Transactions = request.Transactions.Select(t => t with { Description = Cut(t.Description) ?? "", Payee = Cut(t.Payee) }).ToList(),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, Uri("v1/detect-recurring")) { Content = JsonContent.Create(clean, options: Json) };
        var response = await SendAsync<MatcherDetectResponse>(message, authenticate: true, ct);
        return response with { Suggestions = response.Suggestions ?? [] };
    }

    private Uri Uri(string path) =>
        options.BaseUri is { } baseUri ? new Uri(baseUri, path) : throw new MatcherException(MatcherFailure.Disabled, "Matcher is not configured (Matcher__BaseUrl / Matcher__Key).");

    private async Task<T> SendAsync<T>(HttpRequestMessage request, bool authenticate, CancellationToken ct)
        where T : class
    {
        if (!options.Enabled)
        {
            throw new MatcherException(MatcherFailure.Disabled, "Matcher is not configured (Matcher__BaseUrl / Matcher__Key).");
        }

        if (authenticate)
        {
            request.Headers.TryAddWithoutValidation(KeyHeader, options.Key!.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MatcherException(MatcherFailure.Timeout, "Matcher did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MatcherException(MatcherFailure.Unreachable, $"Matcher unreachable ({ex.HttpRequestError}).", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MatcherException(Classify(response.StatusCode), $"Matcher answered {(int)response.StatusCode}.");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                    ?? throw new MatcherException(MatcherFailure.BadResponse, "Matcher returned an empty body.");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new MatcherException(MatcherFailure.Timeout, "Matcher did not answer in time.", ex);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                throw new MatcherException(MatcherFailure.BadResponse, "Matcher returned a body that is not the contract's JSON.", ex);
            }
        }
    }

    public static MatcherFailure Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => MatcherFailure.Unauthorized,
        413 => MatcherFailure.PayloadTooLarge,
        400 or 422 => MatcherFailure.Invalid,
        >= 500 => MatcherFailure.ServerError,
        _ => MatcherFailure.BadResponse,
    };

    private static MatcherTransaction Clip(MatcherTransaction t) =>
        t with { Description = Cut(t.Description) ?? "", Payee = Cut(t.Payee), Memo = Cut(t.Memo) };

    private static string? Cut(string? value) => value is null ? null : value.Length <= MaxTextLength ? value : value[..MaxTextLength];
}
