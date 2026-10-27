namespace CouplesCash.Api.Plaid;

/// <summary>
/// <c>Plaid__*</c>: the keys from dashboard.plaid.com (Developers → Keys). Empty client id or secret → every Plaid endpoint answers
/// 503 <c>plaid_not_configured</c> and only SimpleFIN is offered. <c>Plaid__Environment</c> is <c>production</c> (the free Trial
/// plan's Items are production Items) or <c>sandbox</c>; <c>Plaid__BaseUrl</c> overrides it (fake server in tests and the smoke test).
/// <c>Plaid__RedirectUri</c> is the OAuth return page (must be listed under Allowed redirect URIs in the Plaid dashboard); empty →
/// <c>{WebAuthn__Origin}/settings/banks/plaid-oauth</c> when the origin is https, else none.
/// </summary>
public sealed class PlaidOptions
{
    public const string SectionName = "Plaid";

    public const string OAuthPath = "/settings/banks/plaid-oauth";

    public string? ClientId { get; init; }

    public string? Secret { get; init; }

    public string Environment { get; init; } = "production";

    public string? BaseUrl { get; init; }

    public string? RedirectUri { get; init; }

    /// <summary>How often the scheduler syncs a Plaid link (Plaid itself checks most banks several times a day).</summary>
    public double IntervalMinutes { get; init; } = 60;

    /// <summary>After linking, reconnecting or "Sync now" the link is checked this often until its <c>refresh_until</c>.</summary>
    public double RefreshPollMinutes { get; init; } = 3;

    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Secret);

    public string ResolvedBaseUrl => BaseUrl ?? (Environment == "sandbox" ? "https://sandbox.plaid.com" : "https://production.plaid.com");

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(0.05, IntervalMinutes));

    public TimeSpan RefreshPoll => TimeSpan.FromMinutes(Math.Max(0.05, RefreshPollMinutes));

    private static double Minutes(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes > 0 ? minutes : fallback;

    public static PlaidOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var environment = Clean(section["Environment"])?.ToLowerInvariant() is "sandbox" ? "sandbox" : "production";
        var redirect = Clean(section["RedirectUri"]);
        if (redirect is null && Clean(configuration["WebAuthn:Origin"]) is { } origin
            && Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == "https")
        {
            redirect = o.GetLeftPart(UriPartial.Authority) + OAuthPath;
        }

        return new PlaidOptions
        {
            ClientId = Clean(section["ClientId"]),
            Secret = Clean(section["Secret"]),
            Environment = environment,
            BaseUrl = Clean(section["BaseUrl"])?.TrimEnd('/'),
            RedirectUri = redirect,
            IntervalMinutes = Minutes(section["IntervalMinutes"], 60),
            RefreshPollMinutes = Minutes(section["RefreshPollMinutes"], 3),
        };
    }
}
