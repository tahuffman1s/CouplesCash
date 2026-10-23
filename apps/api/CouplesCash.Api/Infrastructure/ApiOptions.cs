namespace CouplesCash.Api.Infrastructure;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Serve OpenAPI + Scalar. Always on in Development.</summary>
    public bool EnableDocs { get; set; }

    /// <summary>CORS origins. Empty (the default) means CORS is not enabled at all: the BFF talks server-to-server.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>The public https URL the PWA is served from (<c>PUBLIC_URL</c>). Default WebAuthn origin.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Reads <c>Api:AllowedOrigins</c> either as an array (<c>Api__AllowedOrigins__0</c>) or as a single
    /// comma-separated string (<c>Api__AllowedOrigins=https://a,https://b</c>).
    /// </summary>
    public static string[] ReadAllowedOrigins(IConfiguration configuration)
    {
        var section = configuration.GetSection($"{SectionName}:AllowedOrigins");
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            return section.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return section.GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();
    }
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int AuthPerMinute { get; set; } = 10;

    public int GlobalPerMinute { get; set; } = 120;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public bool MigrateOnStartup { get; set; } = true;
}

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>Run the autopay / reminder / hygiene background job. Off in tests.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>How many days back autopay still marks a missed due date (the Pi may have been off).</summary>
    public int AutopayLookbackDays { get; set; } = 3;
}
