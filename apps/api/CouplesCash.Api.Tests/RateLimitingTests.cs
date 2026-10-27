using System.Net;
using System.Security.Claims;
using CouplesCash.Api.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CouplesCash.Api.Tests;

public class RateLimitingTests
{
    [Fact]
    public void Signed_in_requests_are_limited_per_user_and_the_rest_per_client_ip()
    {
        // every request comes from the web container's IP, so two signed-in people must not share one bucket
        var web = IPAddress.Parse("172.18.0.5");
        ClaimsPrincipal SignedIn(string sub) => new(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, sub)], "Bearer"));

        Assert.Equal("user:alex", RateLimitingExtensions.GlobalKey(SignedIn("alex"), web));
        Assert.Equal("user:sam", RateLimitingExtensions.GlobalKey(SignedIn("sam"), web));
        Assert.Equal("172.18.0.5", RateLimitingExtensions.GlobalKey(new ClaimsPrincipal(new ClaimsIdentity()), web));
        // an unauthenticated identity carrying a sub is still anonymous
        Assert.Equal("172.18.0.5", RateLimitingExtensions.GlobalKey(new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, "x")])), web));
        Assert.Equal("unknown", RateLimitingExtensions.GlobalKey(new ClaimsPrincipal(), null));
        Assert.Equal(600, new RateLimitingOptions().UserPerMinute);
    }
}
