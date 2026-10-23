using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CouplesCash.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no valid 'sub' claim.");

    /// <summary>The household id from the <c>hh</c> claim. This is the ONLY source of household scoping for data routes.</summary>
    public static bool TryGetHouseholdId(this ClaimsPrincipal user, out Guid householdId) =>
        Guid.TryParse(user.FindFirst(TokenService.HouseholdClaim)?.Value, out householdId);

    public static Guid GetHouseholdId(this ClaimsPrincipal user) =>
        user.TryGetHouseholdId(out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no 'hh' claim.");
}
