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

public static class RecurringEndpoints
{
    public static RouteGroupBuilder MapRecurring(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/recurring").WithTags("Recurring").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Recurring items, optionally filtered by kind");
        group.MapPost("", Create).WithValidation<RecurringRequest>().WithSummary("Create a bill, subscription or income");
        group.MapGet("/{id:guid}", Get).WithSummary("Get a recurring item");
        group.MapPut("/{id:guid}", Update).WithValidation<RecurringRequest>().WithSummary("Replace a recurring item");
        group.MapDelete("/{id:guid}", Archive).WithSummary("Archive (soft delete) a recurring item");
        group.MapPut("/{id:guid}/occurrences/{date}/paid", MarkPaid).WithValidation<MarkPaidRequest>()
            .WithSummary("Mark an occurrence paid (404 unless {date} is a real occurrence)");
        group.MapDelete("/{id:guid}/occurrences/{date}/paid", MarkUnpaid)
            .WithSummary("Un-mark an occurrence (undo for autopay: the scheduler will not re-pay it)");

        return group;
    }

    private static async Task<Results<Ok<List<RecurringItemDto>>, ValidationProblem>> List(
        ClaimsPrincipal principal,
        RecurringRepository items,
        TimeProvider time,
        CancellationToken ct,
        string? kind = null)
    {
        // Query enums arrive camelCase ("subscription"); parse case-insensitively instead of relying on default binding.
        Kind? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<Kind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Problems.Validation("kind", "Must be one of: bill, subscription, income.");
            }

            filter = parsed;
        }

        var rows = await items.ListAsync(principal.GetHouseholdId(), filter, ct);
        var today = time.Today();
        return TypedResults.Ok(rows.Select(i => i.ToDto(today)).ToList());
    }

    private static async Task<Results<Created<RecurringItemDto>, ValidationProblem>> Create(
        RecurringRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        CategoryRepository categories,
        AccountRepository accounts,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await CheckReferencesAsync(householdId, request, categories, accounts, ct) is { } problem)
        {
            return problem;
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await items.CreateAsync(uow.Connection, householdId, userId, ToFields(request), time.Now(), ct);
        await activity.RecordAsync(uow, householdId, userId, "recurring.created", "recurring", row.Id,
            ActivitySummaries.RecurringCreated(row, currency), new { itemId = row.Id, kind = row.Kind.ToDb(), amountCents = row.AmountCents }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Created($"/v1/recurring/{row.Id}", row.ToDto(time.Today()));
    }

    private static async Task<Results<Ok<RecurringItemDto>, NotFound>> Get(Guid id, ClaimsPrincipal principal, RecurringRepository items, TimeProvider time, CancellationToken ct)
    {
        var row = await items.GetAsync(principal.GetHouseholdId(), id, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok(row.ToDto(time.Today()));
    }

    private static async Task<Results<Ok<RecurringItemDto>, NotFound, ValidationProblem>> Update(
        Guid id,
        RecurringRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        CategoryRepository categories,
        AccountRepository accounts,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        if (await items.GetAsync(householdId, id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        if (await CheckReferencesAsync(householdId, request, categories, accounts, ct) is { } problem)
        {
            return problem;
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await items.UpdateAsync(uow.Connection, householdId, id, ToFields(request), time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "recurring.updated", "recurring", row.Id,
            ActivitySummaries.RecurringUpdated(row), new { itemId = row.Id, kind = row.Kind.ToDb(), amountCents = row.AmountCents }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto(time.Today()));
    }

    private static async Task<Results<NoContent, NotFound>> Archive(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await items.GetAsync(uow.Connection, householdId, id, ct);
        if (row is null || !await items.ArchiveAsync(uow.Connection, householdId, id, time.Now(), ct))
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "recurring.deleted", "recurring", row.Id,
            ActivitySummaries.RecurringDeleted(row), new { itemId = row.Id, kind = row.Kind.ToDb() }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<OccurrenceDto>, NotFound>> MarkPaid(
        Guid id,
        DateOnly date,
        MarkPaidRequest? request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var item = await items.GetAsync(householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        var amount = request?.AmountCents ?? item.AmountCents;

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var payment = await items.UpsertPaymentAsync(uow.Connection, item.Id, date, userId, amount, time.Now(), auto: false, PaymentSource.Manual, ct);
        await activity.RecordAsync(uow, householdId, userId, "occurrence.paid", "occurrence", item.Id,
            ActivitySummaries.OccurrencePaid(item.Name, amount, currency), new { itemId = item.Id, dueDate = date, amountCents = amount }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Ok(item.ToOccurrence(date, payment));
    }

    private static async Task<Results<Ok<OccurrenceDto>, NotFound>> MarkUnpaid(
        Guid id,
        DateOnly date,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        RecurringRepository items,
        InboxService inbox,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var item = await items.GetAsync(householdId, id, ct);
        if (item is null || !RecurrenceEngine.IsOccurrence(item.Rule, date))
        {
            return TypedResults.NotFound();
        }

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var removed = await items.DeletePaymentAsync(uow.Connection, item.Id, date, ct);
        if (removed is not null)
        {
            // v3: a payment that came from the inbox (confirmed or auto-matched) puts its transaction back to pending.
            var restored = await inbox.RestoreForOccurrenceAsync(uow.Connection, householdId, item.Id, date, ct);

            // Undo guard: once a user un-marks an autopay occurrence the scheduler must leave it alone. An auto-matched
            // (bank) payment is not autopay, so it only needs the skip when the item itself is autoPay.
            if (item.AutoPay || (removed.Auto && restored is null))
            {
                await items.InsertAutopaySkipAsync(uow.Connection, item.Id, date, userId, time.Now(), ct);
            }

            await activity.RecordAsync(uow, householdId, userId, "occurrence.unpaid", "occurrence", item.Id,
                ActivitySummaries.OccurrenceUnpaid(item.Name), new { itemId = item.Id, dueDate = date, wasAuto = removed.Auto, restoredTransactionId = restored?.Id }, ct, principal.GetDisplayName());
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(item.ToOccurrence(date, null));
    }

    /// <summary>categoryId / accountId must belong to the caller's household; otherwise a field-level 400 (not a 404, which is reserved for the route id).</summary>
    private static async Task<ValidationProblem?> CheckReferencesAsync(Guid householdId, RecurringRequest request, CategoryRepository categories, AccountRepository accounts, CancellationToken ct)
    {
        if (request.CategoryId is { } categoryId && !await categories.ExistsAsync(householdId, categoryId, ct))
        {
            return Problems.Validation("categoryId", "Unknown category.");
        }

        if (request.AccountId is { } accountId && !await accounts.ExistsAsync(householdId, accountId, ct))
        {
            return Problems.Validation("accountId", "Unknown account.");
        }

        return null;
    }

    private static RecurringFields ToFields(RecurringRequest r) => new(
        r.Name.Trim(),
        r.Kind!.Value,
        r.AmountCents,
        r.CategoryId,
        r.AccountId,
        r.Frequency!.Value,
        r.AnchorDate!.Value,
        r.EndDate,
        r.AutoPay ?? false,
        string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
        string.IsNullOrWhiteSpace(r.Url) ? null : r.Url.Trim());
}
