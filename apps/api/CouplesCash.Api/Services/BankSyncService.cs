using System.Security.Cryptography;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Push;
using CouplesCash.Api.Security;
using CouplesCash.Api.SimpleFin;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>
/// SimpleFIN bank links: claim (store the access URL encrypted), sync (fetch, upsert external accounts, insert transactions
/// with fingerprint dedupe, run the matcher, auto-match, push balances to mapped accounts), and the scheduler's "sync what is due".
/// The access URL is decrypted only inside <see cref="SyncAsync"/> and never logged or returned.
/// </summary>
public sealed class BankSyncService(
    NpgsqlDataSource db,
    BankRepository banks,
    ImportRepository imports,
    AccountRepository accounts,
    HouseholdRepository households,
    InboxService inbox,
    ISimpleFinClient client,
    DataProtector protector,
    SyncOptions options,
    ActivityService activity,
    PushNotifier notifier,
    TimeProvider time,
    ILogger<BankSyncService> logger)
{
    public const string DefaultLinkName = "SimpleFIN";
    public const string Source = "simplefin";

    public bool Configured => protector.IsConfigured;

    // ---------------------------------------------------------------------------------------------
    // Claim
    // ---------------------------------------------------------------------------------------------

    /// <summary>Claims the setup token, stores the link and runs a first sync within <c>Sync__ClaimSyncBudgetSeconds</c>. Throws <see cref="SimpleFinException"/> when the claim fails.</summary>
    public async Task<BankLinkRow> ClaimAsync(Guid householdId, Guid userId, string? actorName, string setupToken, string? name, CancellationToken ct)
    {
        var accessUrl = await client.ClaimAsync(setupToken, ct);
        var encrypted = protector.Protect(accessUrl);
        var linkName = string.IsNullOrWhiteSpace(name) ? DefaultLinkName : name.Trim();
        var now = time.Now();

        BankLinkRow link;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            link = await banks.InsertLinkAsync(uow.Connection, householdId, userId, linkName, encrypted, now, ct);
            await activity.RecordAsync(uow, householdId, userId, "bank.linked", "bank", link.Id,
                ActivitySummaries.BankLinked(link.Name), new { linkId = link.Id, provider = link.Provider }, ct, actorName);
            await uow.CommitAsync(ct);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ClaimSyncBudgetSeconds, 1, 120)));
        try
        {
            await SyncAsync(link, userId, actorName, manual: false, budget.Token);
        }
        catch (SimpleFinException ex)
        {
            logger.LogWarning("First sync of bank link {LinkId} failed: {Message}", link.Id, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await banks.SetErrorAsync(householdId, link.Id, "First sync timed out; the scheduler will retry.", manual: false, time.Now(), CancellationToken.None);
        }

        return await banks.GetLinkAsync(householdId, link.Id, ct) ?? link;
    }

    // ---------------------------------------------------------------------------------------------
    // Sync
    // ---------------------------------------------------------------------------------------------

    /// <summary>One sync of one link. Bridge failures are recorded on the link (plus a <c>bank.error</c> event) and rethrown as <see cref="SimpleFinException"/>.</summary>
    public async Task<SyncResultDto> SyncAsync(BankLinkRow link, Guid? actorId, string? actorName, bool manual, CancellationToken ct)
    {
        var householdId = link.HouseholdId;
        string accessUrl;
        try
        {
            accessUrl = protector.Unprotect(link.AccessUrlEnc);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            var message = protector.IsConfigured ? "Stored credentials cannot be decrypted (was Security__DataKey rotated?). Unlink and link again." : DataProtector.KeyMissingMessage;
            await RecordFailureAsync(link, actorId, message, manual, ct);
            throw new SimpleFinException(SimpleFinError.AccessDenied, message, ex);
        }

        var today = time.Today();
        var (start, endExclusive) = BankSyncRules.FetchWindow(link.LastSyncedAt, today);

        SimpleFinAccountSet set;
        try
        {
            set = await client.GetAccountsAsync(accessUrl, start, endExclusive, includePending: true, ct);
        }
        catch (SimpleFinException ex)
        {
            await RecordFailureAsync(link, actorId, ex.Message, manual, ct);
            throw;
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        var now = time.Now();

        try
        {
            await using var uow = await db.BeginUnitOfWorkAsync(ct);
            var newIds = new List<Guid>();
            var updatedAccounts = 0;
            string? firstOrg = null;

            foreach (var account in set.Accounts)
            {
                firstOrg ??= string.IsNullOrWhiteSpace(account.OrgName) ? null : account.OrgName;
                var external = await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, account, now, ct);

                if (external.AccountId is { } mappedId && await accounts.GetAsync(uow.Connection, householdId, mappedId, ct) is { } mapped)
                {
                    var balance = BankSyncRules.ToAccountBalance(mapped.Type, account.BalanceCents);
                    if (await banks.ApplyBalanceAsync(uow.Connection, householdId, mapped.Id, balance, link.CreatedBy, now, ct))
                    {
                        updatedAccounts++;
                    }
                }

                foreach (var tx in account.Transactions)
                {
                    var description = string.IsNullOrWhiteSpace(tx.Description) ? tx.Payee ?? "(no description)" : tx.Description.Trim();
                    var row = new NewImportedTransaction(Source, external.Id, external.AccountId, tx.Id,
                        Fingerprint.ForExternal(Source, account.Id, tx.Id), tx.PostedOn, tx.AmountCents, description, tx.Payee, tx.Memo, tx.Pending);
                    if (await imports.InsertAsync(uow.Connection, householdId, row, now, ct) is { } id)
                    {
                        newIds.Add(id);
                    }
                }
            }

            var newRows = await imports.ListByIdsAsync(uow.Connection, householdId, newIds, ct);
            var suggestions = await inbox.SuggestAsync(uow.Connection, householdId, newRows, ct);
            var autoMatched = await inbox.AutoMatchAsync(uow, householdId, currency, newRows, suggestions, ct);

            var nameOverride = link.Name == DefaultLinkName && firstOrg is not null ? firstOrg : null;
            var updated = await banks.MarkSyncedAsync(uow.Connection, householdId, link.Id, now, manual, set.Errors, nameOverride, ct) ?? link;

            var toReview = newRows.Count - autoMatched;
            await activity.RecordAsync(uow.Connection, uow.Transaction, householdId, actorId, "bank.synced", "bank", link.Id,
                ActivitySummaries.BankSynced(updated.Name, newRows.Count, autoMatched),
                new { linkId = link.Id, newTransactions = newRows.Count, updatedAccounts, autoMatched, errors = set.Errors, manual }, ct);

            if (toReview > 0)
            {
                var linkName = updated.Name;
                uow.AfterCommit(() => notifier.ToHousehold(householdId, actorId, new PushPayload(
                    Title: "Bank sync",
                    Body: $"Synced {linkName} · {toReview} to review",
                    Url: "/inbox",
                    Tag: $"sync-{link.Id}")));
            }

            await uow.CommitAsync(ct);
            logger.LogInformation("Scheduler bank sync of {LinkId}: {New} new, {Updated} balance(s) updated, {Auto} auto-matched, {Errors} bridge error(s)",
                link.Id, newRows.Count, updatedAccounts, autoMatched, set.Errors.Count);

            return new SyncResultDto(link.Id, newRows.Count, updatedAccounts, autoMatched, set.Errors, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bank sync of {LinkId} failed while storing results", link.Id);
            await RecordFailureAsync(link, actorId, "Sync failed while saving transactions; see the API log.", manual, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Scheduler phase: every enabled link whose last sync (or last failed attempt) is older than <c>Sync__IntervalHours</c>.</summary>
    public async Task SyncDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        var links = await banks.ListSyncEnabledAsync(ct);
        if (links.Count == 0)
        {
            return;
        }

        if (!protector.IsConfigured)
        {
            logger.LogDebug("Scheduler bank sync skipped: Security__DataKey is not set ({Count} link(s) waiting)", links.Count);
            return;
        }

        foreach (var link in links.Where(l => BankSyncRules.IsDue(l.LastSyncedAt, l.LastAttemptAt, nowUtc, options.Interval)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncAsync(link, null, null, manual: false, ct);
            }
            catch (SimpleFinException ex)
            {
                logger.LogWarning("Scheduler bank sync of {LinkId} failed: {Message}", link.Id, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler bank sync of {LinkId} crashed", link.Id);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------------------------------

    /// <summary>Mapping an external account pushes its balance (and a snapshot) onto the CouplesCash account right away and re-points its inbox rows.</summary>
    public async Task<bool> ApplyMappingAsync(NpgsqlConnection conn, Guid householdId, ExternalAccountRow external, AccountRow account, Guid recordedBy, DateTime now, CancellationToken ct)
    {
        var balance = BankSyncRules.ToAccountBalance(account.Type, external.BalanceCents);
        var changed = await banks.ApplyBalanceAsync(conn, householdId, account.Id, balance, recordedBy, now, ct);
        await imports.SetAccountForExternalAsync(conn, householdId, external.Id, account.Id, ct);
        return changed;
    }

    // ---------------------------------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<BankLinkDto>> ToDtosAsync(Guid householdId, IEnumerable<BankLinkRow> links, CancellationToken ct)
    {
        var externals = (await banks.ListExternalAccountsAsync(householdId, ct)).ToLookup(e => e.BankLinkId);
        return links.Select(l => ToDto(l, externals[l.Id])).ToList();
    }

    public BankLinkDto ToDto(BankLinkRow link, IEnumerable<ExternalAccountRow> externals) => new(
        link.Id,
        link.Provider,
        link.Name,
        link.CreatedAt,
        link.LastSyncedAt,
        protector.IsConfigured ? link.LastError : DataProtector.KeyMissingMessage,
        link.SyncEnabled,
        link.BridgeErrors,
        externals.Select(e => e.ToDto()).ToList());

    private async Task RecordFailureAsync(BankLinkRow link, Guid? actorId, string message, bool manual, CancellationToken ct)
    {
        try
        {
            var now = time.Now();
            await banks.SetErrorAsync(link.HouseholdId, link.Id, message, manual, now, ct);
            await using var uow = await db.BeginUnitOfWorkAsync(ct);
            await activity.RecordAsync(uow.Connection, uow.Transaction, link.HouseholdId, actorId, "bank.error", "bank", link.Id,
                ActivitySummaries.BankError(link.Name, message), new { linkId = link.Id, error = message }, ct);
            await uow.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record bank sync failure for {LinkId}", link.Id);
        }
    }
}
