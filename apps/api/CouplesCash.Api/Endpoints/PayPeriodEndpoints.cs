using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

public static class PayPeriodEndpoints
{
    public static RouteGroupBuilder MapPayPeriod(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/payperiod").WithTags("Pay period").RequireAuthorization().RequireHousehold();
        group.MapGet("", Get).WithSummary("The pay period containing ?date (default: today in the household's time zone): income, due, paid, remaining, spendable");
        return group;
    }

    private static async Task<Results<Ok<PayPeriodViewDto>, ProblemHttpResult>> Get(
        ClaimsPrincipal principal,
        HouseholdRepository households,
        AccountRepository accounts,
        ExpenseRepository expenses,
        OccurrenceService occurrences,
        TimeProvider time,
        CancellationToken ct,
        DateOnly? date = null)
    {
        var householdId = principal.GetHouseholdId();
        var household = await households.GetRowAsync(householdId, ct);
        if (household?.PayAnchorDate is not { } anchor)
        {
            return Problems.PayPeriodNotConfigured();
        }

        var today = DateOnly.FromDateTime(SchedulerRules.ToLocal(time.Now(), household.Timezone));
        var at = date ?? today;
        var window = PayPeriodEngine.Resolve(household.PayPeriod, anchor, at);

        var list = await occurrences.ListAsync(householdId, window.Start, window.End, ct);
        var expenseRows = await expenses.ListAsync(householdId, window.Start, window.End, ct);
        var accountRows = await accounts.ListAsync(householdId, ct);

        var outflows = list.Where(o => o.Kind.IsOutflow()).ToList();
        var due = outflows.Sum(o => o.AmountCents);
        var paid = outflows.Where(o => o.Paid is not null).Sum(o => o.Paid!.AmountCents);
        var remaining = outflows.Where(o => o.Paid is null).Sum(o => o.AmountCents);
        var spendable = accountRows.Where(a => a.Type.IsSpendable()).Sum(a => a.BalanceCents) - remaining;

        return TypedResults.Ok(new PayPeriodViewDto(
            Period: household.PayPeriod,
            Start: window.Start,
            End: window.End,
            NextPayday: window.NextPayday,
            DaysLeft: window.DaysLeft(at),
            ExpectedIncomeCents: list.Where(o => o.Kind == Kind.Income).Sum(o => o.AmountCents),
            DueCents: due,
            PaidCents: paid,
            RemainingCents: remaining,
            SpendableCents: spendable,
            Occurrences: list,
            ExpensesCents: expenseRows.Sum(e => e.AmountCents)));
    }
}
