using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Infrastructure;

/// <summary>RFC 9457 problem responses used across endpoints. 404 is deliberately bland so ids cannot be enumerated.</summary>
public static class Problems
{
    public static ProblemHttpResult NoHousehold() =>
        TypedResults.Problem(title: "No household", detail: "Create or join a household first.", statusCode: StatusCodes.Status409Conflict);

    public static ProblemHttpResult Conflict(string title, string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    public static ProblemHttpResult NotFound(string title = "Not found", string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status404NotFound);

    public static ProblemHttpResult Unauthorized(string title, string? detail = null) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status401Unauthorized);

    public const string PushNotConfiguredType = "https://couplescash.app/problems/push_not_configured";
    public const string PasskeysNotConfiguredType = "https://couplescash.app/problems/passkeys_not_configured";

    public static ProblemHttpResult PushNotConfigured() =>
        TypedResults.Problem(
            type: PushNotConfiguredType,
            title: "Push not configured",
            detail: "Set Push__VapidPublicKey, Push__VapidPrivateKey and Push__Subject (generate keys with `dotnet CouplesCash.Api.dll --vapid`).",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static ProblemHttpResult PasskeysNotConfigured() =>
        TypedResults.Problem(
            type: PasskeysNotConfiguredType,
            title: "Passkeys not configured",
            detail: "Passkeys need an https origin: set WebAuthn__Origin (or Api__PublicUrl) to the public URL of the app.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static ProblemHttpResult PayPeriodNotConfigured() =>
        TypedResults.Problem(title: "Pay period not configured", detail: "Set payPeriod and payAnchorDate in household settings.", statusCode: StatusCodes.Status409Conflict);

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
