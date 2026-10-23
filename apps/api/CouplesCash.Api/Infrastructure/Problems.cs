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

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
