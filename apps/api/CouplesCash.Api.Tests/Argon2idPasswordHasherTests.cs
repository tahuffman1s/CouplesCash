using System.Text.RegularExpressions;
using CouplesCash.Api.Auth;

namespace CouplesCash.Api.Tests;

public partial class Argon2idPasswordHasherTests
{
    private static readonly Argon2idPasswordHasher Hasher = new();

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=65536,t=3,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$")]
    private static partial Regex PhcFormat();

    [Fact]
    public void Hash_is_phc_formatted_with_contract_parameters()
    {
        var encoded = Hasher.Hash("correct horse battery staple");

        Assert.Matches(PhcFormat(), encoded);
    }

    [Fact]
    public void Verify_accepts_the_right_password_and_rejects_others()
    {
        var encoded = Hasher.Hash("correct horse battery staple");

        Assert.True(Hasher.Verify("correct horse battery staple", encoded));
        Assert.False(Hasher.Verify("correct horse battery stapl", encoded));
        Assert.False(Hasher.Verify("Correct horse battery staple", encoded));
        Assert.False(Hasher.Verify("", encoded));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
    {
        var a = Hasher.Hash("same-password-123");
        var b = Hasher.Hash("same-password-123");

        Assert.NotEqual(a, b);
        Assert.True(Hasher.Verify("same-password-123", a));
        Assert.True(Hasher.Verify("same-password-123", b));
    }

    [Fact]
    public void Verify_handles_unicode_passwords()
    {
        var encoded = Hasher.Hash("pässwörd 🔐 ünïcödé");

        Assert.True(Hasher.Verify("pässwörd 🔐 ünïcödé", encoded));
        Assert.False(Hasher.Verify("passwörd 🔐 ünïcödé", encoded));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$not*base64$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(Hasher.Verify("anything", encoded));
    }

    [Fact]
    public void Verify_honours_parameters_embedded_in_the_hash()
    {
        // A hash produced with lighter parameters still verifies, which is what lets us raise the cost later
        // without invalidating existing passwords.
        var encoded = Hasher.Hash("rotate-me-later");
        var lighter = encoded.Replace("m=65536,t=3,p=1", "m=65536,t=3,p=1", StringComparison.Ordinal);

        Assert.True(Hasher.Verify("rotate-me-later", lighter));
        Assert.False(Hasher.Verify("rotate-me-later", encoded.Replace("t=3", "t=2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dummy_hash_is_a_valid_hash_of_an_unknown_password()
    {
        Assert.Matches(PhcFormat(), Hasher.DummyHash);
        Assert.False(Hasher.Verify("", Hasher.DummyHash));
        Assert.False(Hasher.Verify("password", Hasher.DummyHash));
    }
}
