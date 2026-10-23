using System.Security.Cryptography;
using CouplesCash.Api.Security;

namespace CouplesCash.Api.Tests;

public class DataProtectorTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void Round_trips_text()
    {
        var protector = new DataProtector(Key);
        const string secret = "https://user:p%40ss@beta-bridge.simplefin.org/simplefin";

        var blob = protector.Protect(secret);

        Assert.True(protector.IsConfigured);
        Assert.Equal(DataProtector.NonceSize + DataProtector.TagSize + secret.Length, blob.Length);
        Assert.Equal(secret, protector.Unprotect(blob));
    }

    [Fact]
    public void Uses_a_fresh_nonce_each_time()
    {
        var protector = new DataProtector(Key);

        var a = protector.Protect("same");
        var b = protector.Protect("same");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a[..DataProtector.NonceSize], b[..DataProtector.NonceSize]);
    }

    [Theory]
    [InlineData(0)]   // nonce
    [InlineData(12)]  // tag
    [InlineData(28)]  // ciphertext
    public void Detects_tampering(int index)
    {
        var protector = new DataProtector(Key);
        var blob = protector.Protect("hello world");
        blob[index] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void Rejects_blobs_from_another_key()
    {
        var blob = new DataProtector(Key).Protect("hello");
        var other = new DataProtector(Convert.ToBase64String(new byte[32]));

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(blob));
    }

    [Fact]
    public void Rejects_truncated_blobs()
    {
        var protector = new DataProtector(Key);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(new byte[10]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_key_means_not_configured(string? key)
    {
        var protector = new DataProtector(key);

        Assert.False(protector.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => protector.Protect("x"));
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(new byte[40]));
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("c2hvcnQ=")] // "short"
    public void Malformed_key_fails_fast(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new DataProtector(key));
    }

    [Fact]
    public void GenerateKey_is_32_bytes()
    {
        var key = DataProtector.GenerateKey();

        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.True(new DataProtector(key).IsConfigured);
    }
}
