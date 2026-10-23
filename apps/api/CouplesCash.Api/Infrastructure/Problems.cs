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
    public const string BankSyncNotConfiguredType = "https://couplescash.app/problems/bank_sync_not_configured";
    public const string BridgeUnreachableType = "https://couplescash.app/problems/bridge_unreachable";

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

    public static ProblemHttpResult BankSyncNotConfigured() =>
        TypedResults.Problem(
            type: BankSyncNotConfiguredType,
            title: "Bank sync not configured",
            detail: "Set Security__DataKey (base64 of 32 random bytes, e.g. `openssl rand -base64 32`) to store bank credentials encrypted.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>502: the SimpleFIN Bridge could not be reached or answered with an error. <paramref name="errors"/> is surfaced as the <c>errors</c> extension.</summary>
    public static ProblemHttpResult BridgeUnreachable(string detail, IReadOnlyList<string>? errors = null) =>
        TypedResults.Problem(
            type: BridgeUnreachableType,
            title: "Bridge unreachable",
            detail: detail,
            statusCode: StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?> { ["errors"] = errors ?? [] });

    /// <summary>429 for the manual "Sync now" cooldown; the caller sets <c>Retry-After</c>.</summary>
    public static ProblemHttpResult TooManyRequests(int retryAfterSeconds) =>
        TypedResults.Problem(
            title: "Too many requests",
            detail: $"This link was synced recently. Retry in {retryAfterSeconds} second(s).",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["retryAfter"] = retryAfterSeconds });

    public static ProblemHttpResult PayPeriodNotConfigured() =>
        TypedResults.Problem(title: "Pay period not configured", detail: "Set payPeriod and payAnchorDate in household settings.", statusCode: StatusCodes.Status409Conflict);

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
