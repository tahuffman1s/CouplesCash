using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

/// <summary>CSV import: the web parses and maps columns client-side and posts normalised rows (≤ 5000, body ≤ 2 MB on this route only).</summary>
public static class ImportEndpoints
{
    public const int MaxRows = 5000;
    public const long MaxBodyBytes = 2 * 1024 * 1024;
    public const int HistoryLimit = 20;
    public const string Source = "csv";

    public static RouteGroupBuilder MapImports(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/imports").WithTags("Imports").RequireAuthorization().RequireHousehold();

        group.MapPost("/csv", ImportCsv)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .WithValidation<CsvImportRequest>()
            .WithSummary("Import normalised CSV rows into the inbox (fingerprint dedupe; runs the matcher, incl. automatic matches)");
        group.MapGet("/csv", History).WithSummary("Last 20 CSV imports");

        return group;
    }

    private static async Task<Results<Created<CsvImportResultDto>, ValidationProblem>> ImportCsv(
        CsvImportRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ImportRepository imports,
        AccountRepository accounts,
        MatchingService matching,
        MatchingQueue matchingQueue,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var accountId = request.AccountId!.Value;
        if (!await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        var filename = request.Filename.Trim();
        var now = time.Now();
        int imported = 0, duplicates = 0, skipped = 0;
        var newIds = new List<Guid>();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        foreach (var row in request.Rows!)
        {
            if (row.PostedOn is not { } postedOn || row.AmountCents == 0 || string.IsNullOrWhiteSpace(row.Description))
            {
                skipped++;
                continue;
            }

            var description = row.Description.Trim();
            var fingerprint = Fingerprint.ForRow(accountId, postedOn, row.AmountCents, description);
            var candidate = new NewImportedTransaction(Source, null, accountId, null, fingerprint, postedOn, row.AmountCents, description,
                Blank(row.Payee), Blank(row.Memo), Pending: false);
            if (await imports.InsertAsync(uow.Connection, householdId, candidate, now, ct) is { } id)
            {
                newIds.Add(id);
                imported++;
            }
            else
            {
                duplicates++;
            }
        }

        // v6: CSV rows are matched like synced ones (fuzzy matcher, built-in fallback), including automatic matches.
        var newRows = await imports.ListByIdsAsync(uow.Connection, householdId, newIds, ct);
        var run = await matching.RunAsync(uow, householdId, newRows, MatchRunMode.NewRows, userId, ct);

        var record = await imports.InsertCsvImportAsync(uow.Connection, householdId, userId, accountId, filename, request.Rows.Length, imported, duplicates, now, ct);
        await activity.RecordAsync(uow, householdId, userId, "import.csv", "import", record.Id,
            ActivitySummaries.ImportCsv(filename, imported, duplicates),
            new
            {
                importId = record.Id,
                accountId,
                rowCount = request.Rows.Length,
                imported,
                duplicates,
                skipped,
                autoMatched = run.AutoMatched,
                autoCategorized = run.AutoCategorized,
                autoIgnoredTransfers = run.AutoIgnored,
                engine = run.Engine,
            }, ct, principal.GetDisplayName());

        var pending = await imports.CountPendingAsync(uow.Connection, householdId, ct);
        if (imported > 0)
        {
            uow.AfterCommit(() => matchingQueue.Enqueue(householdId, MatchingJobKind.FoundBills));
        }

        await uow.CommitAsync(ct);

        return TypedResults.Created("/v1/imports/csv", new CsvImportResultDto(record.Id, request.Rows.Length, imported, duplicates, skipped, pending));
    }

    private static async Task<Ok<List<CsvImportHistoryDto>>> History(ClaimsPrincipal principal, ImportRepository imports, CancellationToken ct)
    {
        var rows = await imports.ListCsvImportsAsync(principal.GetHouseholdId(), HistoryLimit, ct);
        return TypedResults.Ok(rows.Select(r => r.ToDto()).ToList());
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
