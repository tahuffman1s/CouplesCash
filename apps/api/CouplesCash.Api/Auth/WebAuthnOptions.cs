namespace CouplesCash.Api.Auth;

/// <summary>
/// Passkey (WebAuthn) relying-party configuration. Resolved from <c>WebAuthn__Origin</c> (falling back to
/// <c>Api__PublicUrl</c>), <c>WebAuthn__RelyingPartyId</c> (default: the origin's host) and
/// <c>WebAuthn__RelyingPartyName</c>. Passkeys are a secure-context feature, so the origin must be https;
/// the only exception is <c>http://localhost[:port]</c> for native development. Blank values (compose
/// passes <c>""</c> when unset) mean "not configured".
/// </summary>
public sealed class WebAuthnOptions
{
    public const string SectionName = "WebAuthn";
    public const string DefaultRelyingPartyName = "CouplesCash";

    private WebAuthnOptions()
    {
    }

    /// <summary>Normalised origin (<c>scheme://host[:port]</c>, no path) or null when passkeys are disabled.</summary>
    public string? Origin { get; private init; }

    public string? RelyingPartyId { get; private init; }

    public string RelyingPartyName { get; private init; } = DefaultRelyingPartyName;

    /// <summary>Why passkeys are off (for the startup log); null when enabled.</summary>
    public string? DisabledReason { get; private init; }

    public bool Enabled => Origin is not null && RelyingPartyId is not null;

    public static WebAuthnOptions Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return Resolve(
            origin: section["Origin"],
            publicUrl: configuration["Api:PublicUrl"],
            relyingPartyId: section["RelyingPartyId"],
            relyingPartyName: section["RelyingPartyName"]);
    }

    public static WebAuthnOptions Resolve(string? origin, string? publicUrl, string? relyingPartyId, string? relyingPartyName)
    {
        var name = string.IsNullOrWhiteSpace(relyingPartyName) ? DefaultRelyingPartyName : relyingPartyName.Trim();
        var raw = FirstNonBlank(origin, publicUrl);
        if (raw is null)
        {
            return new WebAuthnOptions { RelyingPartyName = name, DisabledReason = "WebAuthn__Origin / Api__PublicUrl not set" };
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return new WebAuthnOptions { RelyingPartyName = name, DisabledReason = $"origin '{raw}' is not an absolute URL" };
        }

        var isLocalhost = uri.Scheme == Uri.UriSchemeHttp && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !isLocalhost)
        {
            return new WebAuthnOptions { RelyingPartyName = name, DisabledReason = $"origin '{raw}' is not https (only http://localhost is allowed without TLS)" };
        }

        var rpId = string.IsNullOrWhiteSpace(relyingPartyId) ? uri.Host : relyingPartyId.Trim().ToLowerInvariant();
        return new WebAuthnOptions
        {
            Origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant(),
            RelyingPartyId = rpId,
            RelyingPartyName = name,
        };
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
