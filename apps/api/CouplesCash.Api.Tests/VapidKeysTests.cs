using System.Buffers.Text;
using System.Security.Cryptography;
using CouplesCash.Api.Push;

namespace CouplesCash.Api.Tests;

public class VapidKeysTests
{
    [Fact]
    public void Generated_public_key_is_65_byte_uncompressed_point_and_private_key_32_bytes()
    {
        var pair = VapidKeys.Generate();

        var publicKey = Base64Url.DecodeFromChars(pair.PublicKey);
        var privateKey = Base64Url.DecodeFromChars(pair.PrivateKey);

        Assert.Equal(VapidKeys.PublicKeyBytes, publicKey.Length);
        Assert.Equal(0x04, publicKey[0]);
        Assert.Equal(VapidKeys.PrivateKeyBytes, privateKey.Length);
        Assert.DoesNotContain('=', pair.PublicKey);
        Assert.DoesNotContain('+', pair.PublicKey);
        Assert.DoesNotContain('/', pair.PublicKey);
    }

    [Fact]
    public void Key_pair_round_trips_through_ecdsa_and_signs()
    {
        var pair = VapidKeys.Generate();
        var publicKey = Base64Url.DecodeFromChars(pair.PublicKey);
        var privateKey = Base64Url.DecodeFromChars(pair.PrivateKey);

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });

        var data = "vapid"u8.ToArray();
        var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);
        Assert.True(ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Two_generations_differ()
    {
        Assert.NotEqual(VapidKeys.Generate().PrivateKey, VapidKeys.Generate().PrivateKey);
    }

    [Fact]
    public void Env_lines_have_the_exact_prefixes_compose_parses()
    {
        var lines = VapidKeys.EnvLines(new VapidKeyPair("PUB", "PRIV")).ToList();

        Assert.Equal(["Push__VapidPublicKey=PUB", "Push__VapidPrivateKey=PRIV"], lines);
    }

    [Fact]
    public void Push_options_treat_blank_values_as_not_configured()
    {
        var blank = new PushOptions { VapidPublicKey = "", VapidPrivateKey = "  ", Subject = "" };
        var set = new PushOptions { VapidPublicKey = "a", VapidPrivateKey = "b" };

        Assert.False(blank.Enabled);
        Assert.True(set.Enabled);
        Assert.Equal(PushOptions.DefaultSubject, set.EffectiveSubject);
    }

    [Fact]
    public void Push_payload_serializes_camel_case_with_icon()
    {
        var json = new PushPayload("T", "B", "/calendar?m=2026-01&d=2026-01-05", "tag").ToJson();

        Assert.Contains("\"title\":\"T\"", json);
        Assert.Contains("\"url\":\"/calendar?m=2026-01\\u0026d=2026-01-05\"", json);
        Assert.Contains("\"icon\":\"/icons/icon-192.png\"", json);
    }
}
