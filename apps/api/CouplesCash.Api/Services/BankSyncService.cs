using System.Security.Cryptography;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Plaid;
using CouplesCash.Api.Push;
using CouplesCash.Api.Security;
using CouplesCash.Api.SimpleFin;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>
/// Bank links — SimpleFIN (claim a setup token, store the access URL encrypted) and Plaid (exchange Link's public token, store the
/// access token encrypted) — sync (fetch, upsert external accounts, insert transactions with fingerprint dedupe, run the matcher,
/// auto-match, push balances to mapped accounts), and the scheduler's "sync what is due". Both providers produce the same feed shape
/// (<see cref="SimpleFinAccountSet"/>), so their rows share the pending lifecycle, matching and plan. Credentials are decrypted only
/// inside this class and never logged or returned.
/// </summary>
public sealed class BankSyncService(
    NpgsqlDataSource db,
    BankRepository banks,
    ImportRepository imports,
    AccountRepository accounts,
    HouseholdRepository households,
    InboxService inbox,
    MatchingService matching,
    FridayService friday,
    AddedTransactionService added,
    MatchingQueue matchingQueue,
    ISimpleFinClient client,
    IPlaidClient plaid,
    PlaidOptions plaidOptions,
    DataProtector protector,
    SyncOptions options,
    ActivityService activity,
    PushNotifier notifier,
    TimeProvider time,
    ILogger<BankSyncService> logger)
{
    public const string DefaultLinkName = "SimpleFIN";
    public const string Source = "simplefin";
    public const string PlaidSource = "plaid";
    public const string DefaultPlaidLinkName = "Plaid";

    /// <summary>After linking or reconnecting, Plaid is still fetching from the bank: check the link every few minutes for this long.</summary>
    public static readonly TimeSpan PlaidLinkPollWindow = TimeSpan.FromMinutes(30);

    /// <summary>After "Sync now" (which asks Plaid to refresh from the bank).</summary>
    public static readonly TimeSpan PlaidRefreshPollWindow = TimeSpan.FromMinutes(20);

    public bool Configured => protector.IsConfigured;

    public bool PlaidConfigured => plaid.Configured;

    // ---------------------------------------------------------------------------------------------
    // Plaid
    // ---------------------------------------------------------------------------------------------

    /// <summary>A Link token for a new connection, or — with <paramref name="link"/> — for signing in again (update mode).</summary>
    public async Task<PlaidLinkToken> PlaidLinkTokenAsync(Guid householdId, BankLinkRow? link, CancellationToken ct)
    {
        var accessToken = link is null ? null : protector.Unprotect(link.AccessUrlEnc);
        return await plaid.CreateLinkTokenAsync(householdId.ToString("D"), accessToken, ct);
    }

    /// <summary>
    /// Exchanges Link's public token, stores the access token encrypted and runs a first sync within <c>Sync__ClaimSyncBudgetSeconds</c>
    /// (Plaid usually needs a few more minutes for the transactions; the scheduler keeps checking for <see cref="PlaidLinkPollWindow"/>).
    /// Throws <see cref="SimpleFinException"/> when the exchange fails.
    /// </summary>
    public async Task<BankLinkRow> PlaidLinkAsync(Guid householdId, Guid userId, string? actorName, string publicToken, string? institutionName, CancellationToken ct)
    {
        var (accessToken, itemId) = await plaid.ExchangeAsync(publicToken, ct);
        var encrypted = protector.Protect(accessToken);
        var linkName = string.IsNullOrWhiteSpace(institutionName) ? DefaultPlaidLinkName : institutionName.Trim();
        if (linkName.Length > 40)
        {
            linkName = linkName[..40].TrimEnd();
        }

        var now = time.Now();
        BankLinkRow link;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            link = await banks.InsertLinkAsync(uow.Connection, householdId, userId, linkName, encrypted, now, ct,
                provider: BankProviders.Plaid, plaidItemId: itemId, refreshUntil: now + PlaidLinkPollWindow);
            await activity.RecordAsync(uow, householdId, userId, "bank.linked", "bank", link.Id,
                ActivitySummaries.BankLinked(link.Name), new { linkId = link.Id, provider = link.Provider }, ct, actorName);
            await uow.CommitAsync(ct);
        }

        await FirstSyncAsync(link, userId, actorName, ct);
        return await banks.GetLinkAsync(householdId, link.Id, ct) ?? link;
    }

    /// <summary>Plaid Link in update mode finished: clear "needs reconnect", sync now (no cooldown) and keep checking for a while.</summary>
    public async Task<SyncResultDto> PlaidReconnectedAsync(BankLinkRow link, Guid userId, string? actorName, CancellationToken ct)
    {
        await banks.SetRefreshUntilAsync(link.HouseholdId, link.Id, time.Now() + PlaidLinkPollWindow, reconnected: true, ct);
        var fresh = await banks.GetLinkAsync(link.HouseholdId, link.Id, ct) ?? link;
        return await SyncAsync(fresh, userId, actorName, manual: false, ct);
    }

    /// <summary>Unlinking a Plaid link also revokes its access token at Plaid (best effort; the link is gone either way).</summary>
    public async Task RevokeAsync(BankLinkRow link, CancellationToken ct)
    {
        if (!link.IsPlaid || !protector.IsConfigured || !plaid.Configured)
        {
            return;
        }

        try
        {
            await plaid.RemoveAsync(protector.Unprotect(link.AccessUrlEnc), ct);
        }
        catch (Exception ex) when (ex is SimpleFinException or CryptographicException or InvalidOperationException)
        {
            logger.LogWarning("Could not revoke Plaid link {LinkId} at Plaid: {Message}", link.Id, ex.Message);
        }
    }

    private async Task FirstSyncAsync(BankLinkRow link, Guid userId, string? actorName, CancellationToken ct)
    {
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
            await banks.SetErrorAsync(link.HouseholdId, link.Id, "First sync timed out; the scheduler will retry.", manual: false, time.Now(), CancellationToken.None);
        }
    }

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

        await FirstSyncAsync(link, userId, actorName, ct);
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
        var source = link.IsPlaid ? PlaidSource : Source;

        SimpleFinAccountSet set;
        if (link.IsPlaid)
        {
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                start = BankSyncRules.PlaidStart(start, await imports.OldestPostedForLinkAsync(conn, householdId, link.Id, ct), today);
            }

            set = await FetchPlaidAsync(link, accessUrl, actorId, manual, start, today, ct);
        }
        else
        {
            set = await FetchSimpleFinAsync(link, accessUrl, actorId, manual, start, endExclusive, ct);
        }

        return await StoreAsync(link, set, source, actorId, manual, start, today, ct);
    }

    /// <summary>Plaid: "Sync now" first asks Plaid to refresh from the bank (results arrive minutes later, so the scheduler keeps checking).</summary>
    private async Task<SimpleFinAccountSet> FetchPlaidAsync(BankLinkRow link, string accessToken, Guid? actorId, bool manual, DateOnly start, DateOnly today, CancellationToken ct)
    {
        try
        {
            if (manual)
            {
                try
                {
                    await plaid.RefreshAsync(accessToken, ct);
                }
                catch (SimpleFinException ex) when (ex.Error is not SimpleFinError.ReauthRequired and not SimpleFinError.AccessDenied)
                {
                    logger.LogWarning("Plaid refresh of {LinkId} skipped: {Message}", link.Id, ex.Message);
                }

                await banks.SetRefreshUntilAsync(link.HouseholdId, link.Id, time.Now() + PlaidRefreshPollWindow, reconnected: false, ct);
            }

            var set = await plaid.GetFeedAsync(accessToken, link.Name, start, today, today, ct);
            if (set.Errors.Contains(PlaidClient.StillLoading))
            {
                await banks.SetRefreshUntilAsync(link.HouseholdId, link.Id, time.Now() + PlaidLinkPollWindow, reconnected: false, ct);
            }

            return set;
        }
        catch (SimpleFinException ex)
        {
            await RecordFailureAsync(link, actorId, ex.Message, manual, ct, needsReconnect: ex.Error == SimpleFinError.ReauthRequired);
            throw;
        }
    }

    /// <summary>
    /// SimpleFIN: one request on a regular sync; the first sync's 90 days of history come in further requests that stay inside the
    /// Bridge's per-request limit (a longer one is cut and reported as an error).
    /// </summary>
    private async Task<SimpleFinAccountSet> FetchSimpleFinAsync(BankLinkRow link, string accessUrl, Guid? actorId, bool manual, DateOnly start, DateOnly endExclusive, CancellationToken ct)
    {
        var requests = BankSyncRules.FetchRequests(start, endExclusive);
        SimpleFinAccountSet set;
        try
        {
            set = await client.GetAccountsAsync(accessUrl, requests[0].Start, requests[0].EndExclusive, includePending: true, ct);
        }
        catch (SimpleFinException ex)
        {
            await RecordFailureAsync(link, actorId, ex.Message, manual, ct);
            throw;
        }

        foreach (var (olderStart, olderEnd, _) in requests.Skip(1))
        {
            try
            {
                set = set.WithHistory(await client.GetAccountsAsync(accessUrl, olderStart, olderEnd, includePending: false, ct));
            }
            catch (SimpleFinException ex)
            {
                // History is a nice-to-have: the recent window already holds balances and pending.
                logger.LogWarning("Bank sync of {LinkId}: history before {End} skipped: {Message}", link.Id, olderEnd, ex.Message);
                break;
            }
        }

        return set;
    }

    private async Task<SyncResultDto> StoreAsync(BankLinkRow link, SimpleFinAccountSet set, string source, Guid? actorId, bool manual, DateOnly start, DateOnly today, CancellationToken ct)
    {
        var householdId = link.HouseholdId;
        var currency = await households.CurrencyAsync(householdId, ct);
        var now = time.Now();

        try
        {
            await using var uow = await db.BeginUnitOfWorkAsync(ct);
            var newIds = new List<Guid>();
            var updatedAccounts = 0;
            var stats = new ReconcileStats();
            string? firstOrg = null;

            // v5: absence is only judged on a clean response (no Bridge errors) for accounts that are in it.
            var judgeAbsence = set.Errors.Count == 0;

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

                // v7 feed diagnostics: what the bank sent for this account, before any dedupe or reconciliation.
                var feed = new FeedStatsDto(account.Transactions.Count(t => t.Pending), account.Transactions.Count(t => !t.Pending), account.AvailableCents is not null, now);
                await banks.SetLastFeedAsync(uow.Connection, householdId, external.Id, MappingV3.SerializeFeed(feed), ct);

                var funAccount = external.AccountId is { } target
                    && (await accounts.GetAsync(uow.Connection, householdId, target, ct))?.Role == AccountRole.Fun;
                var inserted = await ReconcileAccountAsync(uow, link, account, external, start, today, judgeAbsence, currency, now, stats, ct, funAccount, source);

                // The same bank linked through the other provider too (switching SimpleFIN → Plaid): keep each transaction once.
                if (external.AccountId is { } twinAccount && inserted.Count > 0)
                {
                    var twins = await DedupeAcrossProvidersAsync(uow, householdId, twinAccount, source,
                        await imports.ListByIdsAsync(uow.Connection, householdId, inserted, ct), now, ct);
                    inserted = inserted.Where(id => !twins.Contains(id)).ToList();
                    stats.Twins += twins.Count;
                }

                newIds.AddRange(inserted);

                // The bank's own copies of transactions added by hand replace them (so nothing counts twice).
                if (external.AccountId is { } addedTo)
                {
                    await added.ReplaceFromBankAsync(uow, householdId, addedTo, inserted, ct);
                }
            }

            // v7: planned transfers (fun money) and the savings sweep are claimed from the bank rows first, so the matcher never scores them.
            var plannedPass = await friday.RunAsync(uow, householdId, ct);

            // v6: the fuzzy matcher (built-in fallback) scores the new rows; transfers are auto-ignored first, then bills / income /
            // one-offs auto-match and (level all) expenses auto-categorise.
            var newRows = await imports.ListByIdsAsync(uow.Connection, householdId, newIds, ct);
            var run = await matching.RunAsync(uow, householdId, newRows, MatchRunMode.NewRows, link.CreatedBy, ct);
            var autoMatched = run.AutoMatched + run.AutoCategorized;
            var autoIgnored = run.AutoIgnored;

            // Rows a person still has to look at (fun-account rows are stored confirmed; claimed / matched / ignored rows are not pending).
            var toReview = (await imports.ListByIdsAsync(uow.Connection, householdId, newIds, ct)).Count(r => r.IsPending);

            var nameOverride = link.Name == DefaultLinkName && firstOrg is not null ? firstOrg : null;
            var updated = await banks.MarkSyncedAsync(uow.Connection, householdId, link.Id, now, manual, set.Errors, nameOverride, ct) ?? link;

            await activity.RecordAsync(uow.Connection, uow.Transaction, householdId, actorId, "bank.synced", "bank", link.Id,
                ActivitySummaries.BankSynced(updated.Name, newRows.Count, autoMatched),
                new
                {
                    linkId = link.Id,
                    newTransactions = newRows.Count,
                    updatedAccounts,
                    autoMatched,
                    autoMatchedOccurrences = run.AutoMatched,
                    autoCategorized = run.AutoCategorized,
                    autoIgnoredTransfers = autoIgnored,
                    engine = run.Engine,
                    posted = stats.Posted,
                    replaced = stats.Replaced,
                    dropped = stats.Dropped,
                    superseded = stats.Superseded,
                    alreadyFromOtherProvider = stats.Twins,
                    plannedTransfersPaid = plannedPass.PlannedTransfersPaid,
                    sweepsDone = plannedPass.SweepsDone,
                    toReview,
                    errors = set.Errors,
                    manual,
                }, ct);

            if (toReview > 0)
            {
                var linkName = updated.Name;
                uow.AfterCommit(() => notifier.ToHousehold(householdId, actorId, new PushPayload(
                    Title: "Bank sync",
                    Body: $"Synced {linkName} · {toReview} to review",
                    Url: "/transactions",
                    Tag: $"sync-{link.Id}")));
            }

            // Found bills after every sync that brought new transactions (debounced in the background; skipped without the matcher).
            if (newRows.Count > 0)
            {
                uow.AfterCommit(() => matchingQueue.Enqueue(householdId, MatchingJobKind.FoundBills));
            }

            await uow.CommitAsync(ct);
            logger.LogInformation(
                "Scheduler bank sync of {LinkId}: {New} new, {Updated} balance(s) updated, {Auto} auto-matched ({Engine}), {Transfers} transfer(s) auto-ignored, {Posted} posted ({Replaced} under a new id), {Dropped} dropped, {Superseded} superseded, {Twins} already in from the other provider, {Errors} bridge error(s)",
                link.Id, newRows.Count, updatedAccounts, autoMatched, run.Engine, autoIgnored, stats.Posted, stats.Replaced, stats.Dropped, stats.Superseded, stats.Twins, set.Errors.Count);

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

        var due = links.Where(l => l.IsPlaid
            ? plaid.Configured && BankSyncRules.IsPlaidDue(l.LastSyncedAt, l.LastAttemptAt, l.RefreshUntil, l.NeedsReconnect, nowUtc, plaidOptions.Interval, plaidOptions.RefreshPoll)
            : BankSyncRules.IsDue(l.LastSyncedAt, l.LastAttemptAt, nowUtc, options.Interval));
        foreach (var link in due)
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
    // v5: pending lifecycle per external account
    // ---------------------------------------------------------------------------------------------

    private sealed class ReconcileStats
    {
        public int Posted { get; set; }

        public int Replaced { get; set; }

        public int Dropped { get; set; }

        public int Superseded { get; set; }

        public int Twins { get; set; }
    }

    /// <summary>
    /// PLAN-V5 §1 for one external account of a successful response: same-id rows that posted are refreshed in place, a posted
    /// transaction with a new id replaces its absent pending row in place (keeping status, suggestion and confirmation; a confirmed
    /// expense / matched payment follows the posted amount), absent stale authorizations are dropped (their confirmation reversed),
    /// and everything else is inserted. Returns the ids of the inserted rows.
    /// </summary>
    private async Task<List<Guid>> ReconcileAccountAsync(
        UnitOfWork uow,
        BankLinkRow link,
        SimpleFinAccount account,
        ExternalAccountRow external,
        DateOnly windowStart,
        DateOnly today,
        bool judgeAbsence,
        string currency,
        DateTime now,
        ReconcileStats stats,
        CancellationToken ct,
        bool funAccount = false,
        string source = Source)
    {
        var householdId = link.HouseholdId;
        var conn = uow.Connection;
        var byExternalId = new Dictionary<string, SimpleFinTransaction>(StringComparer.Ordinal);
        foreach (var tx in account.Transactions)
        {
            byExternalId.TryAdd(tx.Id, tx);
        }

        var fingerprints = byExternalId.Keys.ToDictionary(id => id, id => Fingerprint.ForExternal(source, account.Id, id), StringComparer.Ordinal);
        var stored = await imports.ListForReconcileAsync(conn, householdId, external.Id, fingerprints.Values.ToList(),
            windowStart.AddDays(-PendingReconciler.DaysAfter), ct);
        var storedById = stored.ToDictionary(r => r.Id);

        var incoming = byExternalId.Values
            .Select(tx => new ReconcileIncoming(tx.Id, tx.Pending, tx.PostedOn, tx.AmountCents, DescriptionOf(tx), tx.Payee, tx.ReplacesId))
            .ToList();
        var existing = stored
            .Select(r => new ReconcileExisting(r.Id, r.ExternalId, r.Pending, r.PostedOn, r.AmountCents, r.Description, r.Payee, r.Status, r.IgnoreReason, r.CreatedAt))
            .ToList();
        var result = PendingReconciler.Reconcile(existing, incoming, windowStart, today, judgeAbsence);

        // 1. Same id: a dropped authorization that reappears comes back; a pending row that posted is refreshed in place.
        foreach (var same in result.SameId)
        {
            var row = await LockedAsync(conn, householdId, same.ExistingId, ct);
            if (row is null)
            {
                continue;
            }

            // the feed's own category (Plaid) follows its latest copy; rows imported before categories were stored get one
            if (byExternalId[same.Incoming.ExternalId] is { Category: { } category } sent
                && (row.ProviderCategory != category || row.ProviderCategoryConfidence != sent.CategoryConfidence))
            {
                await imports.SetProviderCategoryAsync(conn, householdId, row.Id, category, sent.CategoryConfidence, ct);
            }

            if (row.IgnoreReason == PendingReconciler.Dropped)
            {
                await imports.RestoreAsync(conn, householdId, row.Id, ct);
                await activity.RecordAsync(conn, uow.Transaction, householdId, null, "transaction.restored", "transaction", row.Id,
                    ActivitySummaries.TransactionRestored(InboxService.DisplayName(row), row.AmountCents, currency),
                    new { transactionId = row.Id, amountCents = row.AmountCents, reason = "reappeared" }, ct);
            }

            if (same.Posts)
            {
                await ApplyPostedAsync(uow, householdId, row, same.Incoming, externalId: null, fingerprint: null, byExternalId, currency, now, ct);
                stats.Posted++;
            }
        }

        // 2. A posted transaction with a new id takes over its absent pending row.
        foreach (var replacement in result.Replacements)
        {
            var row = await LockedAsync(conn, householdId, replacement.ExistingId, ct);
            if (row is null)
            {
                continue;
            }

            var externalId = replacement.Incoming.ExternalId;
            await ApplyPostedAsync(uow, householdId, row, replacement.Incoming, externalId, fingerprints[externalId], byExternalId, currency, now, ct);
            if (byExternalId[externalId] is { Category: { } postedCategory } postedTx)
            {
                await imports.SetProviderCategoryAsync(conn, householdId, row.Id, postedCategory, postedTx.CategoryConfidence, ct);
            }

            stats.Posted++;
            stats.Replaced++;
        }

        // 3. The posted version is already its own row: hide one of the two (the reviewed one survives).
        foreach (var pair in result.Superseded)
        {
            var hidden = await LockedAsync(conn, householdId, pair.HiddenId, ct);
            var survivor = await LockedAsync(conn, householdId, pair.SurvivorId, ct);
            if (hidden is null || survivor is null)
            {
                continue;
            }

            await inbox.HideAsync(uow, householdId, link.CreatedBy, currency, hidden, PendingReconciler.Superseded, ct);
            if (pair.ApplyToSurvivor)
            {
                await ApplyPostedAsync(uow, householdId, survivor, pair.Posted, externalId: null, fingerprint: null, byExternalId, currency, now, ct);
            }

            stats.Superseded++;
        }

        // 4. Authorizations that fell off the feed.
        foreach (var id in result.Dropped)
        {
            var row = await LockedAsync(conn, householdId, id, ct);
            if (row is null || !row.Pending || row.IsHidden)
            {
                continue;
            }

            await inbox.HideAsync(uow, householdId, link.CreatedBy, currency, row, PendingReconciler.Dropped, ct);
            stats.Dropped++;
        }

        // 5. New rows.
        var newIds = new List<Guid>();
        foreach (var fresh in result.New)
        {
            var tx = byExternalId[fresh.ExternalId];
            // v7: personal fun money never enters To review — stored confirmed with nothing attached (still listed on the account).
            var row = new NewImportedTransaction(source, external.Id, external.AccountId, tx.Id, fingerprints[tx.Id], tx.PostedOn, tx.AmountCents,
                fresh.Description, tx.Payee, tx.Memo, tx.Pending, funAccount ? "confirmed" : "pending", tx.Category, tx.CategoryConfidence);
            if (await imports.InsertAsync(conn, householdId, row, now, ct) is { } id)
            {
                newIds.Add(id);
            }
        }

        await imports.TouchSeenAsync(conn, householdId, fingerprints.Values.ToList(), now, ct);
        return newIds;
    }

    /// <summary>Writes the posted values onto a row (optionally under a new external id), lets a confirmation follow, records <c>transaction.posted</c> when the amount changed.</summary>
    private async Task ApplyPostedAsync(
        UnitOfWork uow,
        Guid householdId,
        ImportedTransactionRow row,
        ReconcileIncoming posted,
        string? externalId,
        byte[]? fingerprint,
        IReadOnlyDictionary<string, SimpleFinTransaction> byExternalId,
        string currency,
        DateTime now,
        CancellationToken ct)
    {
        var memo = byExternalId.TryGetValue(posted.ExternalId, out var tx) ? tx.Memo : row.Memo;
        await imports.ApplyPostedAsync(uow.Connection, householdId, row.Id, externalId, fingerprint, posted.PostedOn, posted.AmountCents,
            posted.Description, posted.Payee, memo, now, ct);

        var amountChanged = row.AmountCents != posted.AmountCents;
        if (amountChanged || row.PostedOn != posted.PostedOn)
        {
            await inbox.FollowPostedAsync(uow.Connection, householdId, row, posted.AmountCents, posted.PostedOn, ct);
        }

        if (amountChanged)
        {
            await activity.RecordAsync(uow.Connection, uow.Transaction, householdId, null, "transaction.posted", "transaction", row.Id,
                ActivitySummaries.TransactionPosted(InboxService.DisplayName(row), row.AmountCents, posted.AmountCents, currency),
                new
                {
                    transactionId = row.Id,
                    fromCents = row.AmountCents,
                    toCents = posted.AmountCents,
                    postedOn = posted.PostedOn,
                    newExternalId = externalId is not null,
                    status = row.Status,
                    expenseId = row.ConfirmedExpenseId,
                    itemId = row.ConfirmedItemId,
                }, ct);
        }
    }

    private async Task<ImportedTransactionRow?> LockedAsync(NpgsqlConnection conn, Guid householdId, Guid id, CancellationToken ct)
    {
        await imports.LockAsync(conn, householdId, id, ct);
        return await imports.GetAsync(conn, householdId, id, ct);
    }

    private static string DescriptionOf(SimpleFinTransaction tx) =>
        string.IsNullOrWhiteSpace(tx.Description) ? tx.Payee ?? "(no description)" : tx.Description.Trim();

    // ---------------------------------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------------------------------

    /// <summary>Mapping an external account pushes its balance (and a snapshot) onto the CouplesCash account right away and re-points its inbox rows.</summary>
    public async Task<bool> ApplyMappingAsync(UnitOfWork uow, Guid householdId, ExternalAccountRow external, AccountRow account, Guid recordedBy, DateTime now, CancellationToken ct)
    {
        var conn = uow.Connection;
        var balance = BankSyncRules.ToAccountBalance(account.Type, external.BalanceCents);
        var changed = await banks.ApplyBalanceAsync(conn, householdId, account.Id, balance, recordedBy, now, ct);
        await imports.SetAccountForExternalAsync(conn, householdId, external.Id, account.Id, ct);

        // Mapped onto an account another provider already feeds (switching banks' provider): its history is there already.
        var rows = await imports.ListPostedForExternalAsync(conn, householdId, external.Id, ct);
        if (rows.Count > 0)
        {
            await DedupeAcrossProvidersAsync(uow, householdId, account.Id, rows[0].Source, rows, now, ct);
        }

        if (account.Role == AccountRole.Fun)
        {
            await imports.ConfirmFunPendingAsync(conn, householdId, account.Id, ct);
        }

        return changed;
    }

    /// <summary>
    /// Posted <paramref name="newcomers"/> from <paramref name="source"/> that another provider already brought onto the account
    /// (<see cref="ProviderTwins"/>) are hidden as its twins (ignored / superseded, <c>replaced_by</c>), anything they were
    /// confirmed as reversed, with one <c>bank.deduplicated</c> event (actor null). Returns the hidden ids.
    /// </summary>
    public async Task<HashSet<Guid>> DedupeAcrossProvidersAsync(UnitOfWork uow, Guid householdId, Guid accountId, string source,
        IReadOnlyList<ImportedTransactionRow> newcomers, DateTime now, CancellationToken ct)
    {
        var posted = newcomers.Where(r => !r.Pending && !r.IsHidden && r.Source == source && r.Source is Source or PlaidSource).ToList();
        if (posted.Count == 0)
        {
            return [];
        }

        var conn = uow.Connection;
        var from = posted.Min(r => r.PostedOn).AddDays(-ProviderTwins.MaxDays);
        var to = posted.Max(r => r.PostedOn).AddDays(ProviderTwins.MaxDays);
        var existing = await imports.ListOtherProviderPostedAsync(conn, householdId, accountId, source, from, to, ct);
        var pairs = ProviderTwins.Match(
            posted.Select(r => new TwinRow(r.Id, r.PostedOn, r.AmountCents)).ToList(),
            existing.Select(r => new TwinRow(r.Id, r.PostedOn, r.AmountCents)).ToList());
        if (pairs.Count == 0)
        {
            return [];
        }

        var byId = posted.ToDictionary(r => r.Id);
        foreach (var (newId, existingId) in pairs)
        {
            await imports.LockAsync(conn, householdId, newId, ct);
            await inbox.ReverseAsync(uow, householdId, null, byId[newId], ct);
            await imports.MarkTwinAsync(conn, householdId, newId, existingId, now, ct);
        }

        var other = source == PlaidSource ? "SimpleFIN" : "Plaid";
        await activity.RecordAsync(conn, uow.Transaction, householdId, null, "bank.deduplicated", "account", accountId,
            $"kept {ActivitySummaries.Plural(pairs.Count, "transaction")} once (already in from {other})",
            new { accountId, source, count = pairs.Count }, ct);
        logger.LogInformation("Bank sync: {Count} {Source} row(s) on account {AccountId} were already in from another provider", pairs.Count, source, accountId);
        return pairs.Select(p => p.NewId).ToHashSet();
    }

    /// <summary>Unlinking: the link's pending rows can no longer post or drop, so they are dropped now (confirmations reversed).</summary>
    public async Task<int> DropPendingForLinkAsync(UnitOfWork uow, Guid householdId, Guid linkId, Guid userId, CancellationToken ct)
    {
        var pending = await imports.ListPendingForLinkAsync(uow.Connection, householdId, linkId, ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var currency = await households.CurrencyAsync(householdId, ct);
        foreach (var row in pending)
        {
            await imports.LockAsync(uow.Connection, householdId, row.Id, ct);
            await inbox.HideAsync(uow, householdId, userId, currency, row, PendingReconciler.Dropped, ct);
        }

        return pending.Count;
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
        externals.Select(e => e.ToDto()).ToList(),
        link.NeedsReconnect);

    private async Task RecordFailureAsync(BankLinkRow link, Guid? actorId, string message, bool manual, CancellationToken ct, bool needsReconnect = false)
    {
        try
        {
            var now = time.Now();
            await banks.SetErrorAsync(link.HouseholdId, link.Id, message, manual, now, ct, needsReconnect);
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
