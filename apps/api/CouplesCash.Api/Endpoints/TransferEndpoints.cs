using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

/// <summary>v5 transfer analysis: money moved between the household's own accounts (never spending, budgets or income).</summary>
public static class TransferEndpoints
{
    public static RouteGroupBuilder MapTransfers(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/transfers").WithTags("Transfers").RequireAuthorization().RequireHousehold();

        group.MapGet("", Report)
            .WithSummary("Transfer report for ?month=YYYY-MM (default this month): flows between accounts, net per account, pairs, one-sided transfers");

        return group;
    }

    private static async Task<Results<Ok<TransferReportDto>, ValidationProblem>> Report(
        ClaimsPrincipal principal,
        ImportRepository imports,
        TimeProvider time,
        CancellationToken ct,
        string? month = null)
    {
        if (!SummaryService.TryParseMonth(month, time.Today(), out var monthStart))
        {
            return Problems.Validation("month", "Must be YYYY-MM.");
        }

        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var rows = await imports.ListTransfersAsync(principal.GetHouseholdId(), monthStart, monthEnd, ct);
        var byId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var legs = byId.Values
            .Select(r => new TransferLeg(r.Id, r.AccountId, r.AccountName, r.PostedOn, r.AmountCents, r.TransferPeerId, r.TransferAuto))
            .ToList();
        var analysis = TransferAnalysis.Analyse(legs, monthStart, monthEnd);

        return TypedResults.Ok(new TransferReportDto(
            monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            analysis.TotalCents,
            analysis.Flows.Select(f => new TransferFlowDto(f.FromAccountId, f.FromName, f.ToAccountId, f.ToName, f.TotalCents, f.Count)).ToList(),
            analysis.NetByAccount.Select(n => new TransferNetDto(n.AccountId, n.Name, n.InCents, n.OutCents, n.NetCents)).ToList(),
            analysis.Pairs.Select(p => new TransferPairDto(p.Id, p.Date, p.AmountCents, Side(p.From), Side(p.To), p.Auto)).ToList(),
            analysis.OneSided.Select(l => byId[l.Id].ToDto()).ToList()));
    }

    private static TransferSideDto Side(TransferLeg leg) =>
        new(leg.AccountId, leg.AccountId is null ? TransferAnalysis.OtherAccount : leg.AccountName ?? TransferAnalysis.OtherAccount, leg.Id);
}
