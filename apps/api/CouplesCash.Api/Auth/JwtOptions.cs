using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace CouplesCash.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinSecretBytes = 32;
    public const string Issuer = "couplescash";
    public const string Audience = "couplescash";

    public string Secret { get; set; } = "";

    public int AccessMinutes { get; set; } = 15;

    public int RefreshDays { get; set; } = 30;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Secret));

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
        RoleClaimType = "role",
    };
}
