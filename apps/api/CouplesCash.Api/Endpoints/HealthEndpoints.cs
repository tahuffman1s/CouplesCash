using CouplesCash.Api.Contracts;
using Dapper;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", GetHealth)
            .WithTags("Health")
            .WithSummary("Liveness + database connectivity")
            .AllowAnonymous()
            .DisableRateLimiting();
        return app;
    }

    private static async Task<Results<Ok<HealthResponse>, JsonHttpResult<HealthResponse>>> GetHealth(NpgsqlDataSource db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var conn = await db.OpenConnectionAsync(timeout.Token);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: timeout.Token));
            return TypedResults.Ok(new HealthResponse("ok", "ok"));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("Health").LogWarning(ex, "Health check: database unreachable");
            return TypedResults.Json(new HealthResponse("degraded", "error"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
