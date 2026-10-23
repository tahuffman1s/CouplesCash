using System.Text.Json;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Data;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace CouplesCash.Api.Services;

/// <summary>Outcome of a passkey ceremony: a row on success, otherwise a short reason for the problem response.</summary>
public sealed record PasskeyOutcome<T>(T? Value, string? Error) where T : class
{
    public static PasskeyOutcome<T> Ok(T value) => new(value, null);

    public static PasskeyOutcome<T> Fail(string error) => new(null, error);
}

/// <summary>
/// WebAuthn ceremonies with Fido2NetLib. Options are stored server-side in <c>webauthn_challenges</c>
/// (5 minute TTL, single use); credentials are discoverable (resident key + user verification required)
/// so sign-in needs no email. The user handle is the user id's 16 bytes.
/// </summary>
public sealed class PasskeyService(WebAuthnOptions options, WebAuthnRepository store, TimeProvider time)
{
    public const string RegisterKind = "register";
    public const string LoginKind = "login";
    public static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);

    private readonly IFido2? _fido = options.Enabled
        ? new Fido2(new Fido2Configuration
        {
            RPID = options.RelyingPartyId,
            RPName = options.RelyingPartyName,
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.Origin! },
            Timeout = 60_000,
            ChallengeSize = 32,
        })
        : null;

    public bool Enabled => _fido is not null;

    public async Task<(Guid ChallengeId, JsonElement Options)> BeginRegistrationAsync(UserRow user, CancellationToken ct)
    {
        var fido = Required();
        var existing = await store.ListForUserAsync(user.Id, ct);
        var createOptions = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(user.Id), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(c => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, c.CredentialId, ParseTransports(c.Transports))).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var json = createOptions.ToJson();
        var now = time.GetUtcNow().UtcDateTime;
        var id = await store.InsertChallengeAsync(createOptions.Challenge, user.Id, RegisterKind, json, now, now.Add(ChallengeTtl), ct);
        return (id, Parse(json));
    }

    public async Task<PasskeyOutcome<WebAuthnCredentialRow>> CompleteRegistrationAsync(Guid userId, Guid challengeId, string name, JsonElement credential, CancellationToken ct)
    {
        var fido = Required();
        var now = time.GetUtcNow().UtcDateTime;
        var challenge = await store.ConsumeChallengeAsync(challengeId, RegisterKind, userId, now, ct);
        if (challenge is null)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail("Unknown or expired challenge.");
        }

        AuthenticatorAttestationRawResponse? raw;
        try
        {
            raw = credential.Deserialize<AuthenticatorAttestationRawResponse>();
        }
        catch (JsonException)
        {
            raw = null;
        }

        if (raw?.Response is null || raw.RawId is null)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail("Malformed credential.");
        }

        RegisteredPublicKeyCredential registered;
        try
        {
            registered = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = raw,
                OriginalOptions = CredentialCreateOptions.FromJson(challenge.Options),
                IsCredentialIdUniqueToUserCallback = async (args, token) => !await store.CredentialIdExistsAsync(args.CredentialId, token),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail(ex.Message);
        }

        var row = await store.InsertAsync(new WebAuthnCredentialRow
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CredentialId = registered.Id,
            PublicKey = registered.PublicKey,
            SignCount = registered.SignCount,
            Aaguid = registered.AaGuid == Guid.Empty ? null : registered.AaGuid,
            Transports = (registered.Transports ?? raw.Response.Transports ?? []).Select(WireName).ToArray(),
            Name = name,
            BackedUp = registered.IsBackedUp,
            CreatedAt = now,
        }, ct);
        return PasskeyOutcome<WebAuthnCredentialRow>.Ok(row);
    }

    public async Task<(Guid ChallengeId, JsonElement Options)> BeginLoginAsync(CancellationToken ct)
    {
        var fido = Required();
        var assertionOptions = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        var json = assertionOptions.ToJson();
        var now = time.GetUtcNow().UtcDateTime;
        var id = await store.InsertChallengeAsync(assertionOptions.Challenge, null, LoginKind, json, now, now.Add(ChallengeTtl), ct);
        return (id, Parse(json));
    }

    /// <summary>Verifies the assertion and returns the credential (with its user) on success.</summary>
    public async Task<PasskeyOutcome<WebAuthnCredentialRow>> CompleteLoginAsync(Guid challengeId, JsonElement credential, CancellationToken ct)
    {
        var fido = Required();
        var now = time.GetUtcNow().UtcDateTime;
        var challenge = await store.ConsumeChallengeAsync(challengeId, LoginKind, null, now, ct);
        if (challenge is null)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail("Unknown or expired challenge.");
        }

        AuthenticatorAssertionRawResponse? raw;
        try
        {
            raw = credential.Deserialize<AuthenticatorAssertionRawResponse>();
        }
        catch (JsonException)
        {
            raw = null;
        }

        if (raw?.Response is null || raw.RawId is null)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail("Malformed credential.");
        }

        var stored = await store.FindByCredentialIdAsync(raw.RawId, ct);
        if (stored is null)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail("Unknown passkey.");
        }

        var expectedHandle = UserHandle(stored.UserId);
        VerifyAssertionResult result;
        try
        {
            result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = raw,
                OriginalOptions = AssertionOptions.FromJson(challenge.Options),
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = (uint)Math.Clamp(stored.SignCount, 0, uint.MaxValue),
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.CredentialId.AsSpan().SequenceEqual(stored.CredentialId) && args.UserHandle.AsSpan().SequenceEqual(expectedHandle)),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            return PasskeyOutcome<WebAuthnCredentialRow>.Fail(ex.Message);
        }

        await store.UpdateAfterLoginAsync(stored.Id, result.SignCount, result.IsBackedUp, now, ct);
        return PasskeyOutcome<WebAuthnCredentialRow>.Ok(stored);
    }

    public static byte[] UserHandle(Guid userId) => userId.ToByteArray();

    /// <summary>WebAuthn wire names ("usb", "smart-card", …) for storage.</summary>
    public static string WireName(AuthenticatorTransport transport) => transport switch
    {
        AuthenticatorTransport.Usb => "usb",
        AuthenticatorTransport.Nfc => "nfc",
        AuthenticatorTransport.Ble => "ble",
        AuthenticatorTransport.SmartCard => "smart-card",
        AuthenticatorTransport.Hybrid => "hybrid",
        AuthenticatorTransport.Internal => "internal",
        _ => transport.ToString().ToLowerInvariant(),
    };

    public static AuthenticatorTransport[]? ParseTransports(string[] names)
    {
        var parsed = names.Select(n => n switch
        {
            "usb" => AuthenticatorTransport.Usb,
            "nfc" => AuthenticatorTransport.Nfc,
            "ble" => AuthenticatorTransport.Ble,
            "smart-card" => AuthenticatorTransport.SmartCard,
            "hybrid" => AuthenticatorTransport.Hybrid,
            "internal" => AuthenticatorTransport.Internal,
            _ => (AuthenticatorTransport?)null,
        }).Where(t => t is not null).Select(t => t!.Value).ToArray();
        return parsed.Length == 0 ? null : parsed;
    }

    private IFido2 Required() => _fido ?? throw new InvalidOperationException("Passkeys are not configured");

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
