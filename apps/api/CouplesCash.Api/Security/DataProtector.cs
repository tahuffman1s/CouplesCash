using System.Security.Cryptography;
using System.Text;

namespace CouplesCash.Api.Security;

/// <summary><c>Security__DataKey</c>: base64 of 32 random bytes. Blank (compose passes "" when unset) = not configured.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public string? DataKey { get; set; }

    public static SecurityOptions Read(IConfiguration configuration)
    {
        var value = configuration.GetSection(SectionName)["DataKey"];
        return new SecurityOptions { DataKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim() };
    }
}

/// <summary>
/// AES-256-GCM for secrets at rest (the SimpleFIN access URL). Blob layout: <c>nonce(12) || tag(16) || ciphertext</c>.
/// Without a key the protector is "not configured": claiming a bank link answers 503 and existing links
/// cannot be decrypted (the sync records "encryption key missing"). Rotation = re-link.
/// </summary>
public sealed class DataProtector
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;
    public const string KeyMissingMessage = "encryption key missing";

    private readonly byte[]? _key;

    public DataProtector(SecurityOptions options)
        : this(options.DataKey)
    {
    }

    public DataProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Security__DataKey must be base64 (32 bytes); generate one with `openssl rand -base64 32`.", ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException($"Security__DataKey must decode to exactly {KeySize} bytes, got {key.Length}.");
        }

        _key = key;
    }

    public bool IsConfigured => _key is not null;

    /// <summary>A fresh base64 key for `make secrets` / docs.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize));

    public byte[] Protect(string plaintext)
    {
        var key = _key ?? throw new InvalidOperationException(KeyMissingMessage);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return blob;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the blob was tampered with or encrypted under another key.</summary>
    public string Unprotect(ReadOnlySpan<byte> blob)
    {
        var key = _key ?? throw new InvalidOperationException(KeyMissingMessage);
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext too short.");
        }

        var nonce = blob[..NonceSize];
        var tag = blob.Slice(NonceSize, TagSize);
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
