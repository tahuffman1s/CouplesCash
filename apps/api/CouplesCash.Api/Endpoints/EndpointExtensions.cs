using CouplesCash.Api.Data;

namespace CouplesCash.Api.Endpoints;

public static class EndpointExtensions
{
    /// <summary>Maps every route of the v1 – v7 contract plus <c>/health</c>.</summary>
    public static IEndpointRouteBuilder MapCouplesCash(this IEndpointRouteBuilder app)
    {
        app.MapHealth();

        var v1 = app.MapGroup("/v1");
        v1.MapAuth();
        v1.MapPasskeys();
        v1.MapCouple();
        v1.MapAccounts();
        v1.MapCategories();
        v1.MapRecurring();
        v1.MapCalendar();
        v1.MapSummary();
        v1.MapActivity();
        v1.MapExpenses();
        v1.MapBudgets();
        v1.MapPayPeriod();
        v1.MapTodos();
        v1.MapGoals();
        v1.MapPush();
        v1.MapReports();
        v1.MapBanks();
        v1.MapInbox();
        v1.MapImports();
        v1.MapWeek();
        v1.MapTransfers();
        v1.MapMatching();
        v1.MapTransactions();
        v1.MapBillSuggestions();
        v1.MapPlan();

        return app;
    }

    internal static string? UserAgent(this HttpRequest request) => request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    internal static DateOnly Today(this TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    internal static DateTime Now(this TimeProvider time) => time.GetUtcNow().UtcDateTime;

    /// <summary>The household's currency for activity summaries ("USD" when the row is somehow missing).</summary>
    internal static async Task<string> CurrencyAsync(this HouseholdRepository households, Guid householdId, CancellationToken ct) =>
        (await households.GetRowAsync(householdId, ct))?.Currency ?? "USD";
}
