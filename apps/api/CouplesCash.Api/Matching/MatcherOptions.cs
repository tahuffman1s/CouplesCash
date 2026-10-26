namespace CouplesCash.Api.Matching;

/// <summary>
/// <c>Matcher__*</c>: where the Python matcher lives and the shared key it expects in <c>X-Matcher-Key</c>. An empty base URL or key
/// disables it (the API then always uses its built-in C# matcher and found bills are paused).
/// </summary>
public sealed class MatcherOptions
{
    public const string SectionName = "Matcher";

    /// <summary><c>http://matcher:8090</c> in compose, <c>http://localhost:8090</c> in native dev.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Same value as the matcher's <c>MATCHER_KEY</c>. Never logged.</summary>
    public string? Key { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Scheduler sweep over every To-review row, per household (minutes; ≤ 0 = every tick).</summary>
    public double RematchMinutes { get; set; } = 30;

    /// <summary>At most this many transactions per <c>/v1/match</c> request (the service accepts 5000; 1000 keeps bodies small).</summary>
    public int ChunkSize { get; set; } = 1000;

    /// <summary>The service accepts at most 2000 candidates per request.</summary>
    public int MaxCandidates { get; set; } = 2000;

    /// <summary>Debounce of the immediate rematch after an item changes (seconds).</summary>
    public int DebounceSeconds { get; set; } = 10;

    public bool Enabled => Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(Key);

    public Uri? BaseUri => Enabled ? new Uri(BaseUrl!.Trim().TrimEnd('/') + "/") : null;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300));

    public TimeSpan RematchInterval => TimeSpan.FromMinutes(Math.Max(0, RematchMinutes));

    public TimeSpan Debounce => TimeSpan.FromSeconds(Math.Clamp(DebounceSeconds, 0, 600));

    public static MatcherOptions Read(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<MatcherOptions>() ?? new MatcherOptions();
}
