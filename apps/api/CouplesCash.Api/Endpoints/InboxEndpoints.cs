using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

/// <summary>The review inbox: imported transactions waiting to be confirmed (as an expense or a bill/income payment) or ignored.</summary>
public static class InboxEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static readonly string[] Statuses = ["pending", "confirmed", "ignored"];

    public static RouteGroupBuilder MapInbox(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/inbox").WithTags("Inbox").RequireAuthorization().RequireHousehold();

        group.MapGet("/summary", Summary).WithSummary("Pending / suggested counts, last sync and link count (dashboard card)");
        group.MapGet("", List).WithSummary("Inbox rows newest postedOn first; ?status=pending|confirmed|ignored, keyset ?before=<nextBefore>, ?limit≤200");
        group.MapPost("/{id:guid}/confirm", Confirm).WithValidation<InboxConfirmRequest>()
            .WithSummary("Confirm as an expense ({ as:'expense', categoryId?, name? }) or as a bill/income payment ({ as:'occurrence', itemId, dueDate })");
        group.MapPost("/{id:guid}/ignore", Ignore).WithSummary("Ignore a pending row (transfer, refund, noise)");
        group.MapPost("/{id:guid}/restore", Restore).WithSummary("Back to pending; reverses the confirmed expense or payment");
        group.MapPost("/bulk", Bulk).WithValidation<InboxBulkRequest>()
            .WithSummary("{ action:'confirm-suggested'|'ignore', ids? } — no ids = every pending row; confirm-suggested takes confidence ≥ 70 only");

        return group;
    }

    private static async Task<Ok<InboxSummaryDto>> Summary(ClaimsPrincipal principal, ImportRepository imports, BankRepository banks, CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var (pending, suggested) = await imports.SummaryAsync(householdId, TransactionMatcher.BulkConfidence, ct);
        var (lastSyncedAt, linkCount) = await banks.SyncSummaryAsync(householdId, ct);
        return TypedResults.Ok(new InboxSummaryDto(pending, suggested, lastSyncedAt, linkCount));
    }

    private static async Task<Results<Ok<InboxPage>, ValidationProblem>> List(
        ClaimsPrincipal principal,
        ImportRepository imports,
        CancellationToken ct,
        string? status = null,
        string? before = null,
        int limit = DefaultLimit)
    {
        if (status is not null && !Statuses.Contains(status, StringComparer.Ordinal))
        {
            return Problems.Validation("status", "Must be one of: pending, confirmed, ignored.");
        }

        InboxCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (TryParseCursor(before, out var parsed))
            {
                cursor = parsed;
            }
            else
            {
                return Problems.Validation("before", "Must be a nextBefore value from a previous page.");
            }
        }

        var size = Math.Clamp(limit, 1, MaxLimit);
        var rows = await imports.ListAsync(principal.GetHouseholdId(), status, cursor, size, ct);
        var page = rows.Take(size).Select(r => r.ToDto()).ToList();
        var nextBefore = rows.Count > size ? Cursor(rows[size - 1]) : null;
        return TypedResults.Ok(new InboxPage(page, nextBefore));
    }

    private static async Task<Results<Ok<InboxItemDto>, NotFound, ValidationProblem>> Confirm(
        Guid id,
        InboxConfirmRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ImportRepository imports,
        HouseholdRepository households,
        InboxService inbox,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var actorName = principal.GetDisplayName();
        var currency = await households.CurrencyAsync(householdId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        await imports.LockAsync(uow.Connection, householdId, id, ct);
        var row = await imports.GetAsync(uow.Connection, householdId, id, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        var error = request.As switch
        {
            ConfirmAs.Expense => await inbox.ConfirmExpenseAsync(uow, householdId, userId, actorName, currency, row, request.CategoryId, request.Name, quiet: false, ct),
            ConfirmAs.Occurrence => await inbox.ConfirmOccurrenceAsync(uow, householdId, userId, actorName, currency, row, request.ItemId!.Value, request.DueDate!.Value, quiet: false, ct),
            _ => new InboxError("as", "Must be 'expense' or 'occurrence'."),
        };
        if (error is not null)
        {
            return Problems.Validation(error.Field, error.Message);
        }

        var fresh = await imports.GetAsync(uow.Connection, householdId, id, ct) ?? row;
        await uow.CommitAsync(ct);
        return TypedResults.Ok(fresh.ToDto());
    }

    private static Task<Results<Ok<InboxItemDto>, NotFound, ValidationProblem>> Ignore(
        Guid id, ClaimsPrincipal principal, NpgsqlDataSource db, ImportRepository imports, HouseholdRepository households, InboxService inbox, CancellationToken ct) =>
        Review(id, principal, db, imports, households, ct, (uow, hh, user, name, currency, row) => inbox.IgnoreAsync(uow, hh, user, name, currency, row, quiet: false, ct));

    private static Task<Results<Ok<InboxItemDto>, NotFound, ValidationProblem>> Restore(
        Guid id, ClaimsPrincipal principal, NpgsqlDataSource db, ImportRepository imports, HouseholdRepository households, InboxService inbox, CancellationToken ct) =>
        Review(id, principal, db, imports, households, ct, (uow, hh, user, name, currency, row) => inbox.RestoreAsync(uow, hh, user, name, currency, row, ct));

    private static async Task<Results<Ok<InboxItemDto>, NotFound, ValidationProblem>> Review(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ImportRepository imports,
        HouseholdRepository households,
        CancellationToken ct,
        Func<UnitOfWork, Guid, Guid, string?, string, ImportedTransactionRow, Task<InboxError?>> action)
    {
        var householdId = principal.GetHouseholdId();
        var currency = await households.CurrencyAsync(householdId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        await imports.LockAsync(uow.Connection, householdId, id, ct);
        var row = await imports.GetAsync(uow.Connection, householdId, id, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        var error = await action(uow, householdId, principal.GetUserId(), principal.GetDisplayName(), currency, row);
        if (error is not null)
        {
            return Problems.Validation(error.Field, error.Message);
        }

        var fresh = await imports.GetAsync(uow.Connection, householdId, id, ct) ?? row;
        await uow.CommitAsync(ct);
        return TypedResults.Ok(fresh.ToDto());
    }

    private static async Task<Ok<BulkResultDto>> Bulk(
        InboxBulkRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        ImportRepository imports,
        HouseholdRepository households,
        InboxService inbox,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var actorName = principal.GetDisplayName();
        var currency = await households.CurrencyAsync(householdId, ct);
        var confirmSuggested = request.Action == InboxBulkRequest.ConfirmSuggested;

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var rows = request.Ids is { Length: > 0 } ids
            ? await imports.ListByIdsAsync(uow.Connection, householdId, ids, ct)
            : await imports.ListPendingAsync(uow.Connection, householdId, ct);

        int confirmed = 0, ignored = 0, skipped = 0;
        foreach (var listed in rows)
        {
            await imports.LockAsync(uow.Connection, householdId, listed.Id, ct);
            var row = await imports.GetAsync(uow.Connection, householdId, listed.Id, ct);
            if (row is null || !row.IsPending)
            {
                skipped++;
                continue;
            }

            if (!confirmSuggested)
            {
                _ = await inbox.IgnoreAsync(uow, householdId, userId, actorName, currency, row, quiet: true, ct);
                ignored++;
                continue;
            }

            if (row.Confidence < TransactionMatcher.BulkConfidence)
            {
                skipped++;
                continue;
            }

            InboxError? error;
            if (row is { SuggestedItemId: { } itemId, SuggestedDueDate: { } dueDate })
            {
                error = await inbox.ConfirmOccurrenceAsync(uow, householdId, userId, actorName, currency, row, itemId, dueDate, quiet: true, ct);
            }
            else if (row.SuggestedTransfer)
            {
                error = await inbox.IgnoreAsync(uow, householdId, userId, actorName, currency, row, quiet: true, ct);
                if (error is null)
                {
                    ignored++;
                    continue;
                }
            }
            else if (row.SuggestedCategoryId is { } categoryId && row.IsOutflow)
            {
                error = await inbox.ConfirmExpenseAsync(uow, householdId, userId, actorName, currency, row, categoryId, null, quiet: true, ct);
            }
            else
            {
                skipped++;
                continue;
            }

            if (error is null)
            {
                confirmed++;
            }
            else
            {
                skipped++;
            }
        }

        if (confirmed + ignored > 0)
        {
            var summary = confirmSuggested
                ? $"confirmed {ActivitySummaries.Plural(confirmed, "suggested transaction")}{(ignored > 0 ? $", ignored {ActivitySummaries.Plural(ignored, "transfer")}" : "")}"
                : $"ignored {ActivitySummaries.Plural(ignored, "transaction")}";
            await activity.RecordAsync(uow, householdId, userId, confirmSuggested ? "transaction.confirmed" : "transaction.ignored", "transaction", null,
                summary, new { bulk = true, action = request.Action, confirmed, ignored, skipped }, ct, actorName);
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(new BulkResultDto(confirmed, ignored, skipped));
    }

    /// <summary>Cursor = "YYYY-MM-DD_&lt;uuid&gt;" (a bare date means "everything before that date").</summary>
    internal static string Cursor(ImportedTransactionRow last) => $"{last.PostedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{last.Id:D}";

    internal static bool TryParseCursor(string value, out InboxCursor cursor)
    {
        cursor = default;
        var parts = value.Split('_', 2);
        if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            cursor = new InboxCursor(date, Guid.Empty);
            return true;
        }

        if (!Guid.TryParse(parts[1], out var id))
        {
            return false;
        }

        cursor = new InboxCursor(date, id);
        return true;
    }
}
