using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CouplesCash.Api.Infrastructure;

/// <summary>Documents the JWT bearer scheme so Scalar shows an "Authorize" box.</summary>
internal sealed class OpenApiBearerTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "CouplesCash API";
        document.Info.Version = "v1";
        document.Info.Description = "Shared-finance API for two. Money is integer cents, dates are YYYY-MM-DD, ids are UUIDs. "
            + "Authenticate with `Authorization: Bearer <accessToken>` from /v1/auth/login.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Access token from /v1/auth/login, /v1/auth/register or /v1/auth/refresh.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }
}
