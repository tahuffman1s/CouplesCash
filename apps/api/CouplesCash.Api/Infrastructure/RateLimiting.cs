using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CouplesCash.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Credential endpoints and couple/join: a tight fixed window per client IP.</summary>
    public const string Auth = "auth";
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddCouplesCashRateLimiting(this IServiceCollection services, RateLimitingOptions options)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.GlobalPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            limiter.AddPolicy(RateLimitPolicies.Auth, context =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.AuthPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            limiter.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = $"Rate limit exceeded. Retry in {seconds} second(s).",
                    },
                });
            };
        });

        return services;
    }

    /// <summary>Partition key: the client IP as seen after ForwardedHeaders; "unknown" for in-process/test hosts.</summary>
    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
