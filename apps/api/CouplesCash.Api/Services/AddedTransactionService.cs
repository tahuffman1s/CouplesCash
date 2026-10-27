using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Infrastructure;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>The outcome of adding a transaction by hand.</summary>
/// <param name="Duplicate">The bank already shows a row that looks like it (nothing was stored; repeat with <c>force</c> to add anyway).</param>
public sealed record AddedOutcome(ImportedTransactionRow? Row, bool UnknownAccount = false, ImportedTransactionRow? Duplicate = null);

/// <summary>
/// Transactions added by hand (the "+" sheet): a charge the bank feed has not shown yet. Stored on an account as source <c>added</c>,
/// confirmed (never to review) and pending, so it is listed with the account's pending rows and counts in the plan until the bank's
/// own row arrives — then <see cref="ReplaceFromBankAsync"/> hides it (ignored / superseded, <c>replaced_by</c>). Matching:
/// <see cref="ManualMatcher"/>.
/// </summary>
public sealed class AddedTransactionService(
    NpgsqlDataSource db,
    AccountRepository accounts,
    ImportRepository imports,
    HouseholdRepository households,
    ActivityService activity,
    TimeProvider time)
{
    public async Task<AddedOutcome> AddAsync(Guid householdId, Guid userId, string? actorName, Guid accountId, long amountCents, string description, DateOnly postedOn, bool force, CancellationToken ct)
    {
        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (await accounts.GetAsync(uow.Connection, householdId, accountId, ct) is null)
        {
            return new AddedOutcome(null, UnknownAccount: true);
        }

        var name = description.Trim();
        if (!force)
        {
            // The bank may already show it: a row it could be the copy of, not already standing for another added one.
            var recent = await imports.ListUnclaimedBankRowsAsync(uow.Connection, householdId, accountId,
                postedOn.AddDays(-ManualMatcher.DaysAfter), postedOn.AddDays(ManualMatcher.DaysBefore), ct);
            var probe = new ManualOpen(Guid.Empty, postedOn, amountCents, name, time.Now());
            var duplicate = recent
                .Select(r => (Row: r, Score: ManualMatcher.Score(probe, Candidate(r))))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Row)
                .FirstOrDefault();
            if (duplicate is not null)
            {
                return new AddedOutcome(null, Duplicate: duplicate);
            }
        }

        var now = time.Now();
        var id = await imports.InsertAddedAsync(uow.Connection, householdId, accountId, postedOn, amountCents, name, userId, now, ct);
        await activity.RecordAsync(uow, householdId, userId, "transaction.added", "transaction", id,
            ActivitySummaries.TransactionAdded(name, amountCents, currency),
            new { transactionId = id, accountId, amountCents, postedOn, forced = force }, ct, actorName);
        var row = await imports.GetAsync(uow.Connection, householdId, id, ct);
        await uow.CommitAsync(ct);
        return new AddedOutcome(row);
    }

    /// <summary>Deletes a transaction added by hand. False when there is none with that id (bank rows cannot be deleted).</summary>
    public async Task<bool> DeleteAsync(Guid householdId, Guid userId, string? actorName, Guid id, CancellationToken ct)
    {
        var currency = await households.CurrencyAsync(householdId, ct);
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await imports.GetAsync(uow.Connection, householdId, id, ct);
        if (row is not { Source: ImportRepository.AddedSource } || !await imports.DeleteAddedAsync(uow.Connection, householdId, id, ct))
        {
            return false;
        }

        await activity.RecordAsync(uow, householdId, userId, "transaction.added_removed", "transaction", null,
            ActivitySummaries.TransactionAddedRemoved(row.Description, row.AmountCents, currency),
            new { transactionId = id, accountId = row.AccountId, amountCents = row.AmountCents }, ct, actorName);
        await uow.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Bank sync, inside its transaction: the account's new bank rows that are the bank's copies of transactions added by hand replace
    /// them (system activity <c>transaction.added_replaced</c>). Returns how many were replaced.
    /// </summary>
    public async Task<int> ReplaceFromBankAsync(UnitOfWork uow, Guid householdId, Guid accountId, IReadOnlyCollection<Guid> newRowIds, CancellationToken ct)
    {
        if (newRowIds.Count == 0)
        {
            return 0;
        }

        var conn = uow.Connection;
        var open = await imports.ListOpenAddedAsync(conn, householdId, accountId, ct);
        if (open.Count == 0)
        {
            return 0;
        }

        var fresh = (await imports.ListByIdsAsync(conn, householdId, newRowIds.ToList(), ct)).Where(r => r.AccountId == accountId && !r.IsHidden).ToList();
        var pairs = ManualMatcher.Match(
            open.Select(o => new ManualOpen(o.Id, o.PostedOn, o.AmountCents, o.Description, o.CreatedAt)).ToList(),
            fresh.Select(Candidate).ToList());
        var byId = open.Concat(fresh).ToDictionary(r => r.Id);
        foreach (var (addedId, bankId) in pairs)
        {
            if (await imports.ReplaceAddedAsync(conn, householdId, addedId, bankId, ct) == 0)
            {
                continue;
            }

            await activity.RecordAsync(uow, householdId, null, "transaction.added_replaced", "transaction", bankId,
                ActivitySummaries.TransactionAddedReplaced(byId[addedId].Description, InboxService.DisplayName(byId[bankId])),
                new { addedId, transactionId = bankId, accountId }, ct);
        }

        return pairs.Count;
    }

    private static ManualCandidate Candidate(ImportedTransactionRow r) => new(r.Id, r.PostedOn, r.AmountCents, r.Description, r.Payee);
}
