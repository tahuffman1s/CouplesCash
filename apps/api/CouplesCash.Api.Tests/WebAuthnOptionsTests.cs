using CouplesCash.Api.Auth;
using CouplesCash.Api.Validation;

namespace CouplesCash.Api.Tests;

public class WebAuthnOptionsTests
{
    [Fact]
    public void Https_origin_derives_rp_id_from_host_and_normalises_origin()
    {
        var o = WebAuthnOptions.Resolve("https://Cash.Example.com/some/path", null, null, null);

        Assert.True(o.Enabled);
        Assert.Equal("https://cash.example.com", o.Origin);
        Assert.Equal("cash.example.com", o.RelyingPartyId);
        Assert.Equal(WebAuthnOptions.DefaultRelyingPartyName, o.RelyingPartyName);
    }

    [Fact]
    public void Public_url_is_the_fallback_origin()
    {
        var o = WebAuthnOptions.Resolve("", "https://cash.example.com", "", "Our Money");

        Assert.True(o.Enabled);
        Assert.Equal("cash.example.com", o.RelyingPartyId);
        Assert.Equal("Our Money", o.RelyingPartyName);
    }

    [Fact]
    public void Explicit_rp_id_wins()
    {
        var o = WebAuthnOptions.Resolve("https://app.cash.example.com", null, "cash.example.com", null);

        Assert.Equal("cash.example.com", o.RelyingPartyId);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://LOCALHOST:3000/")]
    public void Http_localhost_is_the_only_non_https_exception(string origin)
    {
        var o = WebAuthnOptions.Resolve(origin, null, null, null);

        Assert.True(o.Enabled);
        Assert.Equal("localhost", o.RelyingPartyId);
        Assert.StartsWith("http://localhost", o.Origin);
    }

    [Theory]
    [InlineData("http://cash.example.com")]
    [InlineData("http://192.168.1.20:3000")]
    [InlineData("http://raspberrypi.local")]
    [InlineData("ftp://cash.example.com")]
    [InlineData("not a url")]
    public void Insecure_or_invalid_origins_disable_passkeys(string origin)
    {
        var o = WebAuthnOptions.Resolve(origin, null, null, null);

        Assert.False(o.Enabled);
        Assert.Null(o.RelyingPartyId);
        Assert.NotNull(o.DisabledReason);
    }

    [Fact]
    public void Blank_everything_disables_passkeys()
    {
        var o = WebAuthnOptions.Resolve("", "", "", "");

        Assert.False(o.Enabled);
        Assert.Equal(WebAuthnOptions.DefaultRelyingPartyName, o.RelyingPartyName);
    }

    [Theory]
    [InlineData("America/Chicago", true)]
    [InlineData("Europe/London", true)]
    [InlineData("UTC", true)]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Iana_time_zones_resolve_under_invariant_globalization(string? id, bool expected)
    {
        Assert.Equal(expected, HouseholdSettingsRequestValidator.IsKnownTimeZone(id));
    }

    [Fact]
    public void Chicago_offset_is_minus_five_or_six_hours()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var offset = tz.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TimeSpan.FromHours(-5), offset);
    }
}
