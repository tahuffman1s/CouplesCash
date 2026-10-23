using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CouplesCash.Api.Infrastructure;

/// <summary>Turns unhandled exceptions into ProblemDetails. Stack traces and messages are only revealed in Development.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, IHostEnvironment environment, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // A request body that does not deserialize (bad enum value, wrong type, malformed JSON) is reported like
        // any other validation failure: 400 with `errors: { field: [message] }`, the field taken from the JSON path.
        if (exception is BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest, InnerException: JsonException json })
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new HttpValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [FieldFromJsonPath(json.Path)] = ["Invalid or malformed value."],
                })
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                },
            });
        }

        var (status, title, detail) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "Bad request", bad.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Client closed request", null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (string?)null),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else if (status != 499)
        {
            logger.LogWarning("{Title} ({Status}) for {Method} {Path}: {Message}", title, status, httpContext.Request.Method, httpContext.Request.Path, exception.Message);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = environment.IsDevelopment() ? exception.ToString() : detail,
            },
        });
    }

    /// <summary>"$.type" → "type", "$.items[0].name" → "items[0].name", "$" or null → "body".</summary>
    internal static string FieldFromJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return "body";
        }

        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
        return trimmed.Length == 0 ? "body" : trimmed;
    }
}
