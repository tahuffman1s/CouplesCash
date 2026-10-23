using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CouplesCash.Api.Auth;

public interface IPasswordHasher
{
    /// <summary>Hashes a password into a PHC-formatted string (<c>$argon2id$v=19$m=65536,t=3,p=1$salt$hash</c>).</summary>
    string Hash(string password);

    /// <summary>Verifies a password against a PHC string in constant time. Malformed strings never verify.</summary>
    bool Verify(string password, string encoded);

    /// <summary>A valid hash of a random password. Verified against when the user does not exist so timing is uniform.</summary>
    string DummyHash { get; }
}

/// <summary>Argon2id, 64 MB / 3 iterations / parallelism 1, 16-byte salt, 32-byte hash.</summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    public const int SaltSize = 16;
    public const int HashSize = 32;
    public const int MemoryKb = 64 * 1024;
    public const int Iterations = 3;
    public const int Parallelism = 1;

    private const string Prefix = "$argon2id$";

    public Argon2idPasswordHasher()
    {
        DummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    public string DummyHash { get; }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, MemoryKb, Iterations, Parallelism, HashSize);
        return $"{Prefix}v=19$m={MemoryKb},t={Iterations},p={Parallelism}${PhcBase64(salt)}${PhcBase64(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        ArgumentNullException.ThrowIfNull(password);

        // Argon2 cannot hash zero bytes and no account can have an empty password; validation rejects
        // these long before we get here, so there is no timing signal worth hiding.
        if (password.Length == 0 || !TryParse(encoded, out var parsed))
        {
            return false;
        }

        var computed = Derive(password, parsed.Salt, parsed.MemoryKb, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(computed, parsed.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static bool TryParse(string? encoded, out ParsedHash parsed)
    {
        parsed = default;
        if (encoded is null || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // $argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>
        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[1] != "v=19")
        {
            return false;
        }

        int? m = null, t = null, p = null;
        foreach (var kv in parts[2].Split(','))
        {
            var eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !int.TryParse(kv.AsSpan(eq + 1), out var value) || value <= 0)
            {
                return false;
            }

            switch (kv[..eq])
            {
                case "m":
                    m = value;
                    break;
                case "t":
                    t = value;
                    break;
                case "p":
                    p = value;
                    break;
                default:
                    return false;
            }
        }

        if (m is null || t is null || p is null)
        {
            return false;
        }

        if (!TryPhcBase64Decode(parts[3], out var salt) || !TryPhcBase64Decode(parts[4], out var hash) || salt.Length < 8 || hash.Length < 16)
        {
            return false;
        }

        parsed = new ParsedHash(m.Value, t.Value, p.Value, salt, hash);
        return true;
    }

    private static string PhcBase64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryPhcBase64Decode(string text, out byte[] bytes)
    {
        var padded = text.Length % 4 == 0 ? text : text + new string('=', 4 - (text.Length % 4));
        var buffer = new byte[(padded.Length / 4) * 3];
        if (Convert.TryFromBase64String(padded, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }

    private readonly record struct ParsedHash(int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
