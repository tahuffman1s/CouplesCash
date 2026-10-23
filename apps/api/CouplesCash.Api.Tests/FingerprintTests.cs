using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class FingerprintTests
{
    private static readonly Guid Account = Guid.Parse("00000000-0000-7000-8000-000000000010");

    [Theory]
    [InlineData("KROGER #123", "kroger 123")]
    [InlineData("  Kroger   #123 ", "kroger 123")]
    [InlineData("POS DEBIT - NETFLIX.COM 10/02", "pos debit netflix com 10 02")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("---", "")]
    public void Normalise_lowercases_and_strips_punctuation(string? input, string expected)
    {
        Assert.Equal(expected, Fingerprint.Normalise(input));
    }

    [Fact]
    public void ForRow_is_stable_across_cosmetic_differences()
    {
        var a = Fingerprint.ForRow(Account, new DateOnly(2026, 10, 2), -1249, "KROGER #123");
        var b = Fingerprint.ForRow(Account, new DateOnly(2026, 10, 2), -1249, "kroger  #123");

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForRow_changes_with_amount_date_or_account()
    {
        var baseline = Fingerprint.ForRow(Account, new DateOnly(2026, 10, 2), -1249, "KROGER #123");

        Assert.NotEqual(baseline, Fingerprint.ForRow(Account, new DateOnly(2026, 10, 2), -1250, "KROGER #123"));
        Assert.NotEqual(baseline, Fingerprint.ForRow(Account, new DateOnly(2026, 10, 3), -1249, "KROGER #123"));
        Assert.NotEqual(baseline, Fingerprint.ForRow(Guid.NewGuid(), new DateOnly(2026, 10, 2), -1249, "KROGER #123"));
    }

    [Fact]
    public void ForExternal_is_scoped_by_account_because_simplefin_ids_are_only_unique_per_account()
    {
        var a = Fingerprint.ForExternal("simplefin", "acct-1", "tx-1");
        var b = Fingerprint.ForExternal("simplefin", "acct-2", "tx-1");
        var c = Fingerprint.ForExternal("csv", "acct-1", "tx-1");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, Fingerprint.ForExternal("simplefin", "acct-1", "tx-1"));
    }
}
