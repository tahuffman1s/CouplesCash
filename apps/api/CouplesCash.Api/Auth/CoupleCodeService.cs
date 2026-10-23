using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CouplesCash.Api.Auth;

/// <summary>A freshly generated couple code: what to show the user and what to store.</summary>
public sealed record GeneratedCoupleCode(string Code, string Payload, byte[] PayloadHash, byte[] CodeHash);

/// <summary>
/// Couple codes: 20 random bytes. <c>payload = "cc1." + base64url(bytes)</c> goes in the QR code.
/// The typed code is the first 40 bits of SHA-256(bytes) as 8 Crockford base32 chars (<c>XXXX-XXXX</c>).
/// Both <c>sha256(bytes)</c> and <c>sha256(code)</c> are stored so either form can be redeemed.
/// </summary>
public sealed class CoupleCodeService
{
    public const string PayloadPrefix = "cc1.";
    public const int PayloadBytes = 20;
    public const int CodeLength = 8;
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public GeneratedCoupleCode Generate() => FromBytes(RandomNumberGenerator.GetBytes(PayloadBytes));

    /// <summary>Deterministic derivation from the random payload bytes (exposed for tests).</summary>
    public static GeneratedCoupleCode FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != PayloadBytes)
        {
            throw new ArgumentException($"Payload must be {PayloadBytes} bytes", nameof(bytes));
        }

        var payloadHash = SHA256.HashData(bytes);
        var code = EncodeCode(payloadHash.AsSpan(0, 5));
        return new GeneratedCoupleCode(
            Code: FormatCode(code),
            Payload: PayloadPrefix + Base64Url.EncodeToString(bytes),
            PayloadHash: payloadHash,
            CodeHash: HashCode(code));
    }

    /// <summary>
    /// Computes the hash to look up in storage for whatever the user supplied: a full <c>cc1.</c> payload
    /// (→ sha256(bytes)) or a typed 8-char code in any case, with or without the dash (→ sha256(code)).
    /// </summary>
    public static bool TryComputeLookupHash(string? input, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith(PayloadPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var encoded = trimmed.AsSpan(PayloadPrefix.Length);
            if (!Base64Url.IsValid(encoded, out var decodedLength) || decodedLength != PayloadBytes)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[PayloadBytes];
            if (!Base64Url.TryDecodeFromChars(encoded, buffer, out var written) || written != PayloadBytes)
            {
                return false;
            }

            hash = SHA256.HashData(buffer);
            return true;
        }

        if (!TryNormalizeCode(trimmed, out var code))
        {
            return false;
        }

        hash = HashCode(code);
        return true;
    }

    /// <summary>Upper-cases, strips dashes/spaces and maps the Crockford aliases (O→0, I/L→1). Result is 8 chars, no dash.</summary>
    public static bool TryNormalizeCode(string? input, out string code)
    {
        code = "";
        if (input is null)
        {
            return false;
        }

        var sb = new StringBuilder(CodeLength);
        foreach (var raw in input)
        {
            var c = char.ToUpperInvariant(raw);
            switch (c)
            {
                case '-' or ' ':
                    continue;
                case 'O':
                    c = '0';
                    break;
                case 'I' or 'L':
                    c = '1';
                    break;
            }

            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }

            sb.Append(c);
        }

        if (sb.Length != CodeLength)
        {
            return false;
        }

        code = sb.ToString();
        return true;
    }

    /// <summary>SHA-256 over the ASCII bytes of the normalized 8-char code (no dash).</summary>
    public static byte[] HashCode(string normalizedCode) => SHA256.HashData(Encoding.ASCII.GetBytes(normalizedCode));

    public static string FormatCode(string code) => $"{code[..4]}-{code[4..]}";

    /// <summary>40 bits → 8 Crockford base32 characters.</summary>
    public static string EncodeCode(ReadOnlySpan<byte> fiveBytes)
    {
        if (fiveBytes.Length != 5)
        {
            throw new ArgumentException("Exactly 5 bytes (40 bits) are required", nameof(fiveBytes));
        }

        ulong bits = 0;
        foreach (var b in fiveBytes)
        {
            bits = (bits << 8) | b;
        }

        return string.Create(CodeLength, bits, static (span, v) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var shift = 35 - (5 * i);
                span[i] = Alphabet[(int)((v >> shift) & 0x1F)];
            }
        });
    }
}
