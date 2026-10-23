namespace CouplesCash.Api.Endpoints;

public static class EndpointExtensions
{
    /// <summary>Maps every route of the v1 contract plus <c>/health</c>.</summary>
    public static IEndpointRouteBuilder MapCouplesCash(this IEndpointRouteBuilder app)
    {
        app.MapHealth();

        var v1 = app.MapGroup("/v1");
        v1.MapAuth();
        v1.MapCouple();
        v1.MapAccounts();
        v1.MapCategories();
        v1.MapRecurring();
        v1.MapCalendar();
        v1.MapSummary();

        return app;
    }

    internal static string? UserAgent(this HttpRequest request) => request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    internal static DateOnly Today(this TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    internal static DateTime Now(this TimeProvider time) => time.GetUtcNow().UtcDateTime;
}
