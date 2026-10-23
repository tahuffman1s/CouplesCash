namespace CouplesCash.Api.Push;

/// <summary>
/// Web Push (VAPID) configuration. Compose passes empty strings when unset, so blank values count as
/// "not configured" and every <c>/v1/push/*</c> route answers 503 <c>push_not_configured</c>.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";
    public const string DefaultSubject = "https://couplescash.app";

    /// <summary>base64url of the 65-byte uncompressed P-256 public point (from <c>--vapid</c>).</summary>
    public string? VapidPublicKey { get; set; }

    /// <summary>base64url of the 32-byte P-256 private scalar (from <c>--vapid</c>).</summary>
    public string? VapidPrivateKey { get; set; }

    /// <summary><c>mailto:</c> or https URL the push service may contact about abuse.</summary>
    public string? Subject { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);

    public string EffectiveSubject => string.IsNullOrWhiteSpace(Subject) ? DefaultSubject : Subject.Trim();

    public static PushOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new PushOptions
        {
            VapidPublicKey = Blank(section["VapidPublicKey"]),
            VapidPrivateKey = Blank(section["VapidPrivateKey"]),
            Subject = Blank(section["Subject"]),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
