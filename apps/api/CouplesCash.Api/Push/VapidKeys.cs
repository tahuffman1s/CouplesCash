using System.Buffers.Text;
using System.Security.Cryptography;

namespace CouplesCash.Api.Push;

/// <summary>A freshly generated VAPID key pair, base64url-encoded as the push libraries and <c>--vapid</c> expect.</summary>
public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>P-256 key generation for <c>dotnet CouplesCash.Api.dll --vapid</c>.</summary>
public static class VapidKeys
{
    public const int PublicKeyBytes = 65;
    public const int PrivateKeyBytes = 32;

    public static VapidKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        return new VapidKeyPair(EncodePublicKey(p.Q), Base64Url.EncodeToString(LeftPad(p.D!, PrivateKeyBytes)));
    }

    /// <summary>0x04 || X || Y (65 bytes), base64url without padding.</summary>
    public static string EncodePublicKey(ECPoint q)
    {
        var bytes = new byte[PublicKeyBytes];
        bytes[0] = 0x04;
        LeftPad(q.X!, 32).CopyTo(bytes, 1);
        LeftPad(q.Y!, 32).CopyTo(bytes, 33);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The two lines printed by <c>--vapid</c>, parsed by compose/installer by prefix.</summary>
    public static IEnumerable<string> EnvLines(VapidKeyPair pair)
    {
        yield return $"Push__VapidPublicKey={pair.PublicKey}";
        yield return $"Push__VapidPrivateKey={pair.PrivateKey}";
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        if (value.Length > length)
        {
            return value[^length..];
        }

        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }
}
