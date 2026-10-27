using System.Globalization;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
using CouplesCash.Api.Plaid;
using CouplesCash.Api.Push;
using CouplesCash.Api.Security;
using CouplesCash.Api.Services;
using CouplesCash.Api.SimpleFin;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CouplesCash.Api.Tests;

/// <summary>
/// A <c>[Fact]</c> that needs PostgreSQL: runs when <c>COUPLESCASH_TEST_DB</c> holds a connection string (the database is migrated first),
/// skipped otherwise. Example: <c>COUPLESCASH_TEST_DB="Host=127.0.0.1;Port=5433;Database=couplescash;Username=postgres" dotnet test</c>.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public const string Variable = "COUPLESCASH_TEST_DB";

    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string to run the database tests.";
        }
    }
}

/// <summary>
/// v7 "only one pending transaction shows": the whole pending path against a real database — parser → per-account reconcile →
/// fingerprint upsert → <c>/v1/transactions</c> pending list → <c>/v1/pending</c> → the plan's bills account — with five pending
/// authorizations on one account (two same merchant and amount with different ids, one v2 <c>posted = 0</c>, one with
/// <c>transacted_at</c> only, one pending transfer leg that is auto-ignored).
/// </summary>
public class V7DbTests
{
    private sealed class FakeBridge(Func<string> json) : ISimpleFinClient
    {
        public Task<string> ClaimAsync(string setupToken, CancellationToken ct) => Task.FromResult("https://user:pass@bridge.example/simplefin");

        public Task<SimpleFinAccountSet> GetAccountsAsync(string accessUrl, DateOnly startDate, DateOnly endExclusive, bool includePending, CancellationToken ct) =>
            Task.FromResult(SimpleFinParser.Parse(json(), DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    /// <summary>Plaid as the sync sees it: a /transactions/get page (JSON) parsed by the real <see cref="PlaidParser"/>; <see cref="Fail"/> answers every feed call.</summary>
    private sealed class FakePlaid : IPlaidClient
    {
        public string Page { get; set; } = """{ "accounts": [], "transactions": [], "total_transactions": 0 }""";

        public SimpleFinException? Fail { get; set; }

        public int Refreshes { get; private set; }

        public string? LastUpdateToken { get; private set; }

        public DateOnly? LastStart { get; private set; }

        public bool Configured => true;

        public Task<PlaidLinkToken> CreateLinkTokenAsync(string clientUserId, string? accessToken, CancellationToken ct)
        {
            LastUpdateToken = accessToken;
            return Task.FromResult(new PlaidLinkToken("link-sandbox-123", DateTime.UtcNow.AddHours(4)));
        }

        public Task<(string AccessToken, string ItemId)> ExchangeAsync(string publicToken, CancellationToken ct) =>
            publicToken == "public-bad"
                ? throw new SimpleFinException(SimpleFinError.InvalidToken, "bad")
                : Task.FromResult(("access-sandbox-secret", "item-1"));

        public Task<SimpleFinAccountSet> GetFeedAsync(string accessToken, string orgName, DateOnly start, DateOnly end, DateOnly today, CancellationToken ct)
        {
            LastStart = start;
            if (Fail is { } fail)
            {
                throw fail;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(Page);
            var page = PlaidParser.ParseTransactionsPage(doc.RootElement);
            return Task.FromResult(PlaidParser.ToAccountSet(page.Accounts, page.Transactions, orgName, DateTime.UtcNow, today, []));
        }

        public Task RefreshAsync(string accessToken, CancellationToken ct)
        {
            Refreshes++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string accessToken, CancellationToken ct) => Task.CompletedTask;
    }

    private static long Unix(DateOnly date, int hour = 12) =>
        new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Feed()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var yesterday = today.AddDays(-1);
        string U(DateOnly d) => Unix(d).ToString(CultureInfo.InvariantCulture);
        return $$"""
            {
              "errors": [],
              "accounts": [
                {
                  "org": { "domain": "usbank.com", "name": "U.S. Bank" }, "id": "V7DB-USB", "name": "Checking", "currency": "USD",
                  "balance": "2408.07", "available-balance": "2345.67", "balance-date": {{U(today)}},
                  "transactions": [
                    { "id": "p-walmart-1", "posted": {{U(today)}}, "amount": "-25.00", "description": "WALMART #1234", "pending": true },
                    { "id": "p-walmart-2", "posted": {{U(today)}}, "amount": "-25.00", "description": "WALMART #1234", "pending": true },
                    { "id": "p-shell-v2", "posted": 0, "transacted_at": {{U(yesterday)}}, "amount": "-30.00", "description": "SHELL OIL 5741" },
                    { "id": "p-sbux-ta", "transacted_at": {{U(yesterday)}}, "amount": "-7.40", "description": "STARBUCKS 0091", "pending": true },
                    { "id": "p-xfer-sav", "posted": {{U(today)}}, "amount": "-50.00", "description": "ONLINE TRANSFER TO SAVINGS", "pending": true },
                    { "id": "t-payroll", "posted": {{U(today)}}, "amount": "1500.00", "description": "ACME PAYROLL" }
                  ]
                },
                {
                  "org": { "domain": "usbank.com", "name": "U.S. Bank" }, "id": "V7DB-SAV", "name": "Savings", "currency": "USD",
                  "balance": "10000.00", "balance-date": {{U(today)}},
                  "transactions": [
                    { "id": "p-xfer-in", "posted": {{U(today)}}, "amount": "50.00", "description": "ONLINE TRANSFER FROM CHECKING", "pending": true }
                  ]
                }
              ]
            }
            """;
    }

    private static ServiceProvider Services(string connectionString, Func<string>? feed = null, FakePlaid? plaid = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCouplesCashData(connectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new DataProtector(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())));
        services.AddSingleton(new SyncOptions());
        services.AddSingleton(new MatcherOptions());
        services.AddSingleton<IMatcherClient>(sp => new MatcherClient(new HttpClient(), sp.GetRequiredService<MatcherOptions>()));
        services.AddSingleton<ISimpleFinClient>(new FakeBridge(feed ?? Feed));
        services.AddSingleton(new PlaidOptions { ClientId = "client", Secret = "secret" });
        services.AddSingleton<IPlaidClient>(plaid ?? new FakePlaid());
        services.AddSingleton<PushNotifier>();
        services.AddSingleton<MatcherStatusTracker>();
        services.AddSingleton<MatchingQueue>();
        services.AddSingleton<OccurrenceService>();
        services.AddSingleton<ActivityService>();
        services.AddSingleton<InboxService>();
        services.AddSingleton<AddedTransactionService>();
        services.AddSingleton<PlanService>();
        services.AddSingleton<FridayService>();
        services.AddSingleton<MatchingService>();
        services.AddSingleton<BankSyncService>();
        return services.BuildServiceProvider();
    }

    [DbFact]
    public async Task A_bank_transaction_takes_over_a_tick_by_hand_or_autopay_but_never_another_bank_payment()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        await using var sp = Services(connectionString);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var items = sp.GetRequiredService<RecurringRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB takeover", now, ct);
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            RecurringItemRow Create(string name, bool autoPay) => items.CreateAsync(conn, household.Id, user.Id,
                new RecurringFields(name, Kind.Bill, 6_000, null, null, Frequency.Monthly, today, null, autoPay, null, null), now, ct).GetAwaiter().GetResult();
            var water = Create("Water", autoPay: false);
            var phone = Create("Phone", autoPay: true);

            // Ticked by hand / by autopay before the bank shows it.
            await items.UpsertPaymentAsync(conn, water.Id, today, user.Id, 6_000, now, auto: false, PaymentSource.Manual, ct);
            await items.UpsertPaymentAsync(conn, phone.Id, today, null, 6_000, now, auto: true, PaymentSource.Autopay, ct);

            // The bank rows arrive at their real amounts: each takes the payment over.
            var waterPaid = await items.PayFromBankAsync(conn, water.Id, today, 7_240, now, ct);
            Assert.Equal((PaymentSource.Bank, 7_240L, (Guid?)null, true), (waterPaid!.Source, waterPaid.AmountCents, waterPaid.PaidBy, waterPaid.Auto));
            Assert.Equal(PaymentSource.Bank, (await items.PayFromBankAsync(conn, phone.Id, today, 6_000, now, ct))!.Source);

            // A second bank row never replaces a bank payment.
            Assert.Null(await items.PayFromBankAsync(conn, water.Id, today, 1_000, now, ct));
            Assert.Equal(7_240, (await items.GetPaymentAsync(conn, water.Id, today, ct))!.AmountCents);

            // An unpaid occurrence is simply paid.
            var fresh = Create("Internet", autoPay: false);
            Assert.Equal(PaymentSource.Bank, (await items.PayFromBankAsync(conn, fresh.Id, today, 5_750, now, ct))!.Source);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = household.Id });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task A_transaction_added_by_hand_counts_until_the_banks_copy_arrives()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        var json = Feed();
        await using var sp = Services(connectionString, () => json);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();
        var added = sp.GetRequiredService<AddedTransactionService>();
        var plans = sp.GetRequiredService<PlanService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB added", now, ct);
        var householdId = household.Id;
        try
        {
            AccountRow checking;
            BankLinkRow link;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                var savings = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("Savings", "U.S. Bank", AccountType.Savings, 0, "#000000", "piggy-bank", null), now, ct);
                link = await banks.InsertLinkAsync(uow.Connection, householdId, user.Id, "U.S. Bank", sp.GetRequiredService<DataProtector>().Protect("https://user:pass@bridge.example/simplefin"), now, ct);
                var set = SimpleFinParser.Parse(json, today);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[0], now, ct)).Id, checking.Id, now, ct);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[1], now, ct)).Id, savings.Id, now, ct);
                await accounts.SetRoleAsync(uow.Connection, householdId, checking.Id, AccountRole.Bills, null, now, ct);
                await uow.CommitAsync(ct);
            }

            await sync.SyncAsync(link, user.Id, "Tester", manual: true, ct);
            async Task<PlanBillsAccountDto> Bills() => (await plans.GetAsync(householdId, null, ct))!.BillsAccount!;
            Assert.Equal(234_567, (await Bills()).AvailableCents);

            // A coffee the bank has not shown: it comes off available now and is listed with the pending rows.
            var coffee = await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -425, "Coffee", today, force: false, ct);
            Assert.NotNull(coffee.Row);
            Assert.Equal(("added", true, "confirmed"), (coffee.Row!.Source, coffee.Row.Pending, coffee.Row.Status));
            var bills = await Bills();
            Assert.Equal((234_567 - 425, -425L, 13_740L, 6), (bills.AvailableCents, bills.AddedCents, bills.PendingOutCents, bills.Pending.Count));

            // The bank already shows a Walmart −$25.00 today: adding it again is refused unless forced.
            var walmart = await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -2_500, "Walmart", today, force: false, ct);
            Assert.Null(walmart.Row);
            Assert.Equal(-2_500, walmart.Duplicate!.AmountCents);
            var forced = await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -2_500, "Walmart", today, force: true, ct);
            Assert.NotNull(forced.Row);
            Assert.Equal(234_567 - 425 - 2_500, (await Bills()).AvailableCents);
            Assert.True((await added.AddAsync(householdId, user.Id, "Tester", Guid.NewGuid(), -100, "x", today, force: false, ct)).UnknownAccount);

            // The bank's coffee arrives (a new pending row, its own name): it replaces the added one, so it is not counted twice.
            var u = Unix(today).ToString(CultureInfo.InvariantCulture);
            json = json.Replace(
                "{ \"id\": \"t-payroll\"",
                "{ \"id\": \"p-coffee\", \"posted\": " + u + ", \"amount\": \"-4.25\", \"description\": \"SQ *BLUE BOTTLE\", \"pending\": true },\n                    { \"id\": \"t-payroll\"",
                StringComparison.Ordinal);
            await sync.SyncAsync(await banks.GetLinkAsync(householdId, link.Id, ct) ?? link, user.Id, "Tester", manual: true, ct);
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                var replaced = await imports.GetAsync(conn, householdId, coffee.Row.Id, ct);
                Assert.Equal(("ignored", "superseded"), (replaced!.Status, replaced.IgnoreReason));
                Assert.NotNull(await conn.ExecuteScalarAsync<Guid?>("SELECT replaced_by FROM imported_transactions WHERE id = @Id", new { coffee.Row.Id }));
            }

            // The bank's numbers now hold the coffee: available is the bank's (with the forced Walmart still waiting).
            bills = await Bills();
            Assert.Equal((-2_500L, 13_740L + 425), (bills.AddedCents, bills.PendingOutCents));

            // Delete the forced Walmart; bank rows cannot be deleted.
            Assert.True(await added.DeleteAsync(householdId, user.Id, "Tester", forced.Row!.Id, ct));
            Assert.Equal(0, (await Bills()).AddedCents);
            var bankRow = (await imports.ListBankPendingAsync(householdId, checking.Id, null, reviewOrConfirmedOnly: false, ct)).First();
            Assert.False(await added.DeleteAsync(householdId, user.Id, "Tester", bankRow.Id, ct));
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task A_transaction_added_by_hand_can_pay_a_bill_and_the_banks_copy_takes_the_payment_over()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        var json = Feed();
        await using var sp = Services(connectionString, () => json);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var items = sp.GetRequiredService<RecurringRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();
        var added = sp.GetRequiredService<AddedTransactionService>();
        var inbox = sp.GetRequiredService<InboxService>();
        var plans = sp.GetRequiredService<PlanService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB added bill", now, ct);
        var householdId = household.Id;
        try
        {
            AccountRow checking;
            BankLinkRow link;
            RecurringItemRow netflix, gym;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                var savings = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("Savings", "U.S. Bank", AccountType.Savings, 0, "#000000", "piggy-bank", null), now, ct);
                link = await banks.InsertLinkAsync(uow.Connection, householdId, user.Id, "U.S. Bank", sp.GetRequiredService<DataProtector>().Protect("https://user:pass@bridge.example/simplefin"), now, ct);
                var set = SimpleFinParser.Parse(json, today);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[0], now, ct)).Id, checking.Id, now, ct);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[1], now, ct)).Id, savings.Id, now, ct);
                await accounts.SetRoleAsync(uow.Connection, householdId, checking.Id, AccountRole.Bills, null, now, ct);
                netflix = await items.CreateAsync(uow.Connection, householdId, user.Id,
                    new RecurringFields("Netflix", Kind.Subscription, 1_549, null, checking.Id, Frequency.Monthly, today, null, false, null, null), now, ct);
                gym = await items.CreateAsync(uow.Connection, householdId, user.Id,
                    new RecurringFields("Gym", Kind.Bill, 4_000, null, checking.Id, Frequency.Monthly, today, null, false, null, null), now, ct);
                await uow.CommitAsync(ct);
            }

            await sync.SyncAsync(link, user.Id, "Tester", manual: true, ct);
            async Task<PlanDto> Plan() => (await plans.GetAsync(householdId, null, ct))!;
            async Task<PaymentRow?> Payment(Guid itemId)
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                return await items.GetPaymentAsync(conn, itemId, today, ct);
            }

            async Task<InboxError?> Match(Guid id, RecurringItemRow item, Func<UnitOfWork, ImportedTransactionRow, Task<InboxError?>>? action = null)
            {
                await using var uow = await db.BeginUnitOfWorkAsync(ct);
                var row = (await imports.GetAsync(uow.Connection, householdId, id, ct))!;
                var error = action is null
                    ? await inbox.MatchAsync(uow, householdId, user.Id, "Tester", "USD", row, item.Id, today, quiet: false, ct)
                    : await action(uow, row);
                await uow.CommitAsync(ct);
                return error;
            }

            var before = await Plan();
            Assert.Equal(1_549 + 4_000, before.BillsOutCents);

            // Netflix paid this morning, added by hand: before it is linked the plan counts it twice (added row + unpaid bill) …
            var paid = (await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -1_549, "Netflix", today, force: false, ct)).Row!;
            Assert.Equal(before.LeftAtEndCents - 1_549, (await Plan()).LeftAtEndCents);

            // … linked to the bill it is counted once: the bill is paid (source bank), the added row stays added (confirmed, pending).
            Assert.Null(await Match(paid.Id, netflix));
            var plan = await Plan();
            Assert.Equal((1_549L + 4_000, 1_549L, before.LeftAtEndCents), (plan.BillsOutCents, plan.PaidThisWeekCents, plan.LeftAtEndCents));
            Assert.Equal(PaymentSource.Bank, (await Payment(netflix.Id))!.Source);
            var linked = (await imports.GetAsync(householdId, paid.Id, ct))!;
            Assert.Equal(("added", "confirmed", (Guid?)netflix.Id), (linked.Source, linked.Status, linked.ConfirmedItemId));

            // Unmatch: the bill is unpaid again and the row is still added (never to review), then link it again.
            Assert.Null(await Match(paid.Id, netflix, (uow, row) => inbox.UnmatchAsync(uow, householdId, user.Id, "Tester", "USD", row, ct)));
            var unlinked = (await imports.GetAsync(householdId, paid.Id, ct))!;
            Assert.Equal(("confirmed", (Guid?)null, (Guid?)user.Id), (unlinked.Status, unlinked.ConfirmedItemId, unlinked.ReviewedBy));
            Assert.Null(await Payment(netflix.Id));
            Assert.Null(await Match(paid.Id, netflix));

            // The bank's copy arrives: it replaces the added row and takes the payment over (bank amount, learned name).
            var u = Unix(today).ToString(CultureInfo.InvariantCulture);
            json = json.Replace(
                "{ \"id\": \"t-payroll\"",
                "{ \"id\": \"p-netflix\", \"posted\": " + u + ", \"amount\": \"-15.49\", \"description\": \"NETFLIX.COM\", \"pending\": true },\n                    { \"id\": \"t-payroll\"",
                StringComparison.Ordinal);
            await sync.SyncAsync(await banks.GetLinkAsync(householdId, link.Id, ct) ?? link, user.Id, "Tester", manual: true, ct);
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                var replaced = (await imports.GetAsync(conn, householdId, paid.Id, ct))!;
                Assert.Equal(("ignored", "superseded", (Guid?)null), (replaced.Status, replaced.IgnoreReason, replaced.ConfirmedItemId));
                var bankId = await conn.ExecuteScalarAsync<Guid>("SELECT replaced_by FROM imported_transactions WHERE id = @Id", new { paid.Id });
                var bank = (await imports.GetAsync(conn, householdId, bankId, ct))!;
                Assert.Equal(("confirmed", (Guid?)netflix.Id, (DateOnly?)today, (Guid?)user.Id), (bank.Status, bank.ConfirmedItemId, bank.ConfirmedDueDate, bank.ReviewedBy));
                Assert.Equal(bankId, (await imports.FindConfirmedForOccurrenceAsync(conn, householdId, netflix.Id, today, ct))!.Id);
            }

            Assert.Equal(PaymentSource.Bank, (await Payment(netflix.Id))!.Source);
            Assert.Contains((await items.GetAsync(householdId, netflix.Id, ct))!.BankNamesLearned, n => n.Contains("netflix", StringComparison.OrdinalIgnoreCase));
            Assert.Equal((5_549L, 1_549L), ((await Plan()).BillsOutCents, (await Plan()).PaidThisWeekCents));

            // A linked added row that is deleted leaves its bill unpaid.
            var gymPaid = (await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -4_000, "Gym", today, force: false, ct)).Row!;
            Assert.Null(await Match(gymPaid.Id, gym));
            Assert.Equal((5_549L, 5_549L), ((await Plan()).BillsOutCents, (await Plan()).PaidThisWeekCents));
            Assert.True(await added.DeleteAsync(householdId, user.Id, "Tester", gymPaid.Id, ct));
            Assert.Null(await Payment(gym.Id));
            Assert.Equal((5_549L, 1_549L), ((await Plan()).BillsOutCents, (await Plan()).PaidThisWeekCents));
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task A_payment_from_before_a_bill_started_can_be_linked_and_moves_the_start_back()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        await using var sp = Services(connectionString, Feed);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var items = sp.GetRequiredService<RecurringRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var added = sp.GetRequiredService<AddedTransactionService>();
        var inbox = sp.GetRequiredService<InboxService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB earlier payment", now, ct);
        var householdId = household.Id;
        try
        {
            // The phone bill was typed in starting on the latest 15th; last month's payment is older than the bill.
            var start = new DateOnly(today.Year, today.Month, 15);
            start = start > today ? start.AddMonths(-1) : start;
            var earlier = start.AddMonths(-1);
            AccountRow checking;
            RecurringItemRow phone;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                phone = await items.CreateAsync(uow.Connection, householdId, user.Id,
                    new RecurringFields("Phone", Kind.Bill, 8_000, null, checking.Id, Frequency.Monthly, start, null, false, null, null), now, ct);
                await uow.CommitAsync(ct);
            }

            var old = (await added.AddAsync(householdId, user.Id, "Tester", checking.Id, -8_000, "Phone", earlier.AddDays(1), force: false, ct)).Row!;
            async Task<InboxError?> Act(Func<UnitOfWork, ImportedTransactionRow, Task<InboxError?>> action)
            {
                await using var uow = await db.BeginUnitOfWorkAsync(ct);
                var error = await action(uow, (await imports.GetAsync(uow.Connection, householdId, old.Id, ct))!);
                await uow.CommitAsync(ct);
                return error;
            }

            // The transaction's candidates offer the date before the start; two months back is not a due date.
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                var candidates = await inbox.CandidatesAsync(conn, householdId, (await imports.GetAsync(conn, householdId, old.Id, ct))!, 30, ct);
                Assert.Contains(candidates.Occurrences, c => c.ItemId == phone.Id && c.DueDate == earlier && c.Paid is null);
            }

            Assert.NotNull(await Act((uow, row) => inbox.MatchAsync(uow, householdId, user.Id, "Tester", "USD", row, phone.Id, earlier.AddMonths(-1), quiet: false, ct)));

            // Linked: the bill starts there now, so the payment is on a real occurrence (and the month before opens up).
            Assert.Null(await Act((uow, row) => inbox.MatchAsync(uow, householdId, user.Id, "Tester", "USD", row, phone.Id, earlier, quiet: false, ct)));
            var moved = (await items.GetAsync(householdId, phone.Id, ct))!;
            Assert.Equal(earlier, moved.AnchorDate);
            Assert.True(RecurrenceEngine.IsOccurrence(moved.Rule, start));
            await using (var conn = await db.OpenConnectionAsync(ct))
            {
                Assert.Equal(8_000, (await items.GetPaymentAsync(conn, phone.Id, earlier, ct))!.AmountCents);
            }

            // Unlinked again: the start goes back, so no unpaid month from before the bill counts as due.
            Assert.Null(await Act((uow, row) => inbox.UnmatchAsync(uow, householdId, user.Id, "Tester", "USD", row, ct)));
            Assert.Equal(start, (await items.GetAsync(householdId, phone.Id, ct))!.AnchorDate);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task A_plaid_link_syncs_into_the_same_pipeline_and_a_posted_row_takes_over_its_pending_row()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        var plaid = new FakePlaid();
        await using var sp = Services(connectionString, plaid: plaid);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        string D(int daysAgo) => today.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB plaid", now, ct);
        var householdId = household.Id;
        try
        {
            string Page(string transactions, decimal available) => $$"""
                {
                  "accounts": [
                    { "account_id": "acc-chk", "name": "Checking", "mask": "1234", "type": "depository", "subtype": "checking",
                      "balances": { "available": {{available.ToString(CultureInfo.InvariantCulture)}}, "current": 2408.07, "iso_currency_code": "USD" } }
                  ],
                  "transactions": [ {{transactions}} ],
                  "total_transactions": 2
                }
                """;
            plaid.Page = Page($$"""
                { "transaction_id": "p-diner", "account_id": "acc-chk", "amount": 40.00, "date": "{{D(1)}}", "name": "SQ *DINER", "pending": true },
                { "transaction_id": "t-pay", "account_id": "acc-chk", "amount": -1500.00, "date": "{{D(2)}}", "name": "ACME PAYROLL", "pending": false }
                """, 2345.67m);

            // Link: the bad public token is refused; the good one stores the link (provider plaid, token encrypted) and syncs it.
            await Assert.ThrowsAsync<SimpleFinException>(() => sync.PlaidLinkAsync(householdId, user.Id, "Tester", "public-bad", "U.S. Bank", ct));
            var link = await sync.PlaidLinkAsync(householdId, user.Id, "Tester", "public-good", "U.S. Bank", ct);
            Assert.Equal(("plaid", "U.S. Bank", "item-1"), (link.Provider, link.Name, link.PlaidItemId));
            Assert.NotNull(link.LastSyncedAt);
            Assert.True(link.RefreshUntil > now);
            Assert.DoesNotContain("access-sandbox-secret", System.Text.Encoding.UTF8.GetString(link.AccessUrlEnc), StringComparison.Ordinal);

            var external = Assert.Single(await banks.ListExternalAccountsAsync(householdId, ct));
            Assert.Equal(("Checking ··1234", 240_807L, (long?)234_567), (external.Name, external.BalanceCents, external.AvailableCents));
            AccountRow checking;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                await banks.SetMappingAsync(uow.Connection, householdId, external.Id, checking.Id, now, ct);
                await sync.ApplyMappingAsync(uow, householdId, external, checking, user.Id, now, ct);
                await uow.CommitAsync(ct);
            }

            var pending = (await imports.ListBankPendingAsync(householdId, checking.Id, null, reviewOrConfirmedOnly: false, ct)).Single();
            Assert.Equal(("plaid", -4_000L, "SQ *DINER"), (pending.Source, pending.AmountCents, pending.Description));

            // The diner posts with a tip under a new id and its own wording: Plaid names the pending row, so it is replaced in place.
            plaid.Page = Page($$"""
                { "transaction_id": "t-diner", "account_id": "acc-chk", "amount": 48.00, "date": "{{D(0)}}", "name": "THE DINER LLC", "pending": false, "pending_transaction_id": "p-diner",
                  "personal_finance_category": { "detailed": "FOOD_AND_DRINK_RESTAURANT", "confidence_level": "HIGH" } },
                { "transaction_id": "t-pay", "account_id": "acc-chk", "amount": -1500.00, "date": "{{D(2)}}", "name": "ACME PAYROLL", "pending": false,
                  "personal_finance_category": { "detailed": "INCOME_WAGES", "confidence_level": "VERY_HIGH" } }
                """, 2297.67m);
            var result = await sync.SyncAsync((await banks.GetLinkAsync(householdId, link.Id, ct))!, user.Id, "Tester", manual: true, ct);
            Assert.Equal(0, result.NewTransactions);
            Assert.Equal(1, plaid.Refreshes);
            // the link holds no history yet (its oldest row is days old), so the sync asks for all of Plaid's 90 days, not 30
            Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-BankSyncRules.PlaidHistoryDays), plaid.LastStart);
            var posted = (await imports.GetAsync(householdId, pending.Id, ct))!;
            Assert.Equal((false, -4_800L, "THE DINER LLC", "t-diner"), (posted.Pending, posted.AmountCents, posted.Description, posted.ExternalId));
            Assert.Equal(("FOOD_AND_DRINK_RESTAURANT", "HIGH"), (posted.ProviderCategory, posted.ProviderCategoryConfidence));
            // a row imported before Plaid sent a category gets it when the feed sends it again
            var payroll = (await imports.ListAccountTransactionsAsync(householdId, new AccountTransactionQuery(checking.Id, "all", "ACME", null, null, false), null, 10, ct)).Single();
            Assert.Equal("INCOME_WAGES", payroll.ProviderCategory);
            Assert.Equal(229_767, (await accounts.GetAsync(householdId, checking.Id, ct))!.AvailableCents);

            // The bank wants a new sign-in: the link says so and the scheduler leaves it alone until Reconnect.
            plaid.Fail = new SimpleFinException(SimpleFinError.ReauthRequired, "The bank wants you to sign in again. Tap Reconnect.");
            await Assert.ThrowsAsync<SimpleFinException>(() => sync.SyncAsync(link, null, null, manual: false, ct));
            var stuck = (await banks.GetLinkAsync(householdId, link.Id, ct))!;
            Assert.True(stuck.NeedsReconnect);
            Assert.Null(stuck.RefreshUntil);
            Assert.True(sync.ToDto(stuck, []).NeedsReconnect);
            Assert.False(BankSyncRules.IsPlaidDue(stuck.LastSyncedAt, stuck.LastAttemptAt, stuck.RefreshUntil, stuck.NeedsReconnect, now.AddDays(1), TimeSpan.FromHours(1), TimeSpan.FromMinutes(3)));

            // Update mode gets a token for this Item; once the user has signed in again the sync clears the flag.
            await sync.PlaidLinkTokenAsync(householdId, stuck, ct);
            Assert.Equal("access-sandbox-secret", plaid.LastUpdateToken);
            plaid.Fail = null;
            await sync.PlaidReconnectedAsync(stuck, user.Id, "Tester", ct);
            var back = (await banks.GetLinkAsync(householdId, link.Id, ct))!;
            Assert.Equal((false, (string?)null), (back.NeedsReconnect, back.LastError));
            Assert.True(back.RefreshUntil > now);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task Switching_the_same_bank_from_simplefin_to_plaid_keeps_each_transaction_once()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        var plaid = new FakePlaid();
        await using var sp = Services(connectionString, plaid: plaid);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        string D(int daysAgo) => today.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB switch", now, ct);
        var householdId = household.Id;
        try
        {
            // SimpleFIN first: US Bank checking with the payroll (+1,500 today) and five pending charges (plus one on its unmapped savings).
            AccountRow checking;
            BankLinkRow simplefin;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                simplefin = await banks.InsertLinkAsync(uow.Connection, householdId, user.Id, "SimpleFIN", sp.GetRequiredService<DataProtector>().Protect("https://user:pass@bridge.example/simplefin"), now, ct);
                var set = SimpleFinParser.Parse(Feed(), today);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, simplefin.Id, set.Accounts[0], now, ct)).Id, checking.Id, now, ct);
                await uow.CommitAsync(ct);
            }

            await sync.SyncAsync(simplefin, user.Id, "Tester", manual: true, ct);
            async Task<IReadOnlyList<ImportedTransactionRow>> Visible() =>
                (await imports.ListAccountTransactionsAsync(householdId, new AccountTransactionQuery(checking.Id, "all", null, null, null, false), null, 200, ct)).ToList();
            Assert.Single(await Visible(), r => r.AmountCents == 150_000);

            // Plaid for the same bank: its 90 days include the same payroll (two days earlier) and a coffee SimpleFIN never sent.
            plaid.Page = $$"""
                {
                  "accounts": [ { "account_id": "acc-chk", "name": "Checking", "mask": "1234", "type": "depository",
                                  "balances": { "available": 2345.67, "current": 2408.07, "iso_currency_code": "USD" } } ],
                  "transactions": [
                    { "transaction_id": "t-pay", "account_id": "acc-chk", "amount": -1500.00, "date": "{{D(2)}}", "name": "ACME PAYROLL", "pending": false },
                    { "transaction_id": "t-coffee", "account_id": "acc-chk", "amount": 4.25, "date": "{{D(1)}}", "name": "BLUE BOTTLE", "pending": false }
                  ],
                  "total_transactions": 2
                }
                """;
            var link = await sync.PlaidLinkAsync(householdId, user.Id, "Tester", "public-good", "U.S. Bank", ct);
            var external = (await banks.ListExternalAccountsAsync(householdId, ct)).Single(e => e.BankLinkId == link.Id);
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                await sync.ApplyMappingAsync(uow, householdId, external, checking, user.Id, now, ct);
                await uow.CommitAsync(ct);
            }

            // Mapped onto the same account: the payroll is there once (SimpleFIN's), the coffee is new.
            var rows = await Visible();
            Assert.Equal("simplefin", Assert.Single(rows, r => r.AmountCents == 150_000).Source);
            Assert.Equal("plaid", Assert.Single(rows, r => r.AmountCents == -425).Source);

            // Unlinking SimpleFIN drops its pending charges (nothing follows them any more); posted rows stay.
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                Assert.Equal(6, await sync.DropPendingForLinkAsync(uow, householdId, simplefin.Id, user.Id, ct));
                await uow.CommitAsync(ct);
            }

            Assert.Empty(await imports.ListBankPendingAsync(householdId, checking.Id, null, reviewOrConfirmedOnly: false, ct));
            Assert.Single(await Visible(), r => r.AmountCents == 150_000);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task Plaid_rows_auto_match_bills_learned_from_simplefin_wording_and_file_themselves_by_plaids_category()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        var plaid = new FakePlaid();
        await using var sp = Services(connectionString, plaid: plaid);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var items = sp.GetRequiredService<RecurringRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB plaid automatch", now, ct);
        var householdId = household.Id;
        try
        {
            // The bill learned SimpleFIN's wording ("NETFLIX COM", and a payee rule on it); its own name is not in the bank text.
            RecurringItemRow streaming;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                streaming = await items.CreateAsync(uow.Connection, householdId, user.Id,
                    new RecurringFields("Streaming", Kind.Subscription, 1_549, null, null, Frequency.Monthly, today, null, false, null, null), now, ct);
                await items.SetLearnedNamesAsync(uow.Connection, householdId, streaming.Id, ["NETFLIX COM"], now, ct);
                await imports.UpsertRuleAsync(uow.Connection, householdId, "netflix com", null, streaming.Id, now, ct);
                await uow.CommitAsync(ct);
            }

            // Plaid: a merchant name ("Netflix") and the bank's own wording with a phone number and state.
            plaid.Page = $$"""
                {
                  "accounts": [ { "account_id": "acc-chk", "name": "Checking", "mask": "1234", "type": "depository",
                                  "balances": { "available": 2345.67, "current": 2408.07, "iso_currency_code": "USD" } } ],
                  "transactions": [
                    { "transaction_id": "t-netflix", "account_id": "acc-chk", "amount": 15.49, "date": "{{today:yyyy-MM-dd}}", "name": "Netflix",
                      "merchant_name": "Netflix", "original_description": "NETFLIX.COM 866-579-7172 CA", "pending": false },
                    { "transaction_id": "t-coffee", "account_id": "acc-chk", "amount": 6.50, "date": "{{today:yyyy-MM-dd}}", "name": "Blue Bottle",
                      "merchant_name": "Blue Bottle Coffee", "original_description": "SQ *BLUE BOTTLE 0042", "pending": false,
                      "personal_finance_category": { "primary": "FOOD_AND_DRINK", "detailed": "FOOD_AND_DRINK_COFFEE", "confidence_level": "VERY_HIGH" } },
                    { "transaction_id": "t-market", "account_id": "acc-chk", "amount": 42.10, "date": "{{today:yyyy-MM-dd}}", "name": "Corner Market",
                      "original_description": "CORNER MKT 118", "pending": false,
                      "personal_finance_category": { "primary": "FOOD_AND_DRINK", "detailed": "FOOD_AND_DRINK_GROCERIES", "confidence_level": "HIGH" } }
                  ],
                  "total_transactions": 3
                }
                """;
            await sync.PlaidLinkAsync(householdId, user.Id, "Tester", "public-good", "U.S. Bank", ct);

            await using var conn = await db.OpenConnectionAsync(ct);
            async Task<ImportedTransactionRow> Row(string externalId) => (await imports.GetAsync(conn, householdId,
                await conn.ExecuteScalarAsync<Guid>("SELECT id FROM imported_transactions WHERE household_id = @Id AND external_id = @X", new { Id = householdId, X = externalId }), ct))!;
            var matched = await Row("t-netflix");
            Assert.Equal(("confirmed", (Guid?)streaming.Id, true), (matched.Status, matched.ConfirmedItemId, matched.ConfirmedAuto));

            // Plaid's own category: VERY_HIGH coffee is filed under Fun by itself; HIGH groceries only waits as a suggestion.
            var coffee = await Row("t-coffee");
            Assert.Equal(("confirmed", true, "Fun", "FOOD_AND_DRINK_COFFEE"), (coffee.Status, coffee.ConfirmedAuto, coffee.ConfirmedCategoryName, coffee.ProviderCategory));
            var market = await Row("t-market");
            Assert.Equal(("pending", "Groceries", 74), (market.Status, market.SuggestedCategoryName, market.Confidence));
            Assert.Contains("bank category: food and drink groceries", market.MatchDetail);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }
    }

    [DbFact]
    public async Task Starting_over_empties_the_household_but_keeps_both_people_and_the_couple()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        await using var sp = Services(connectionString);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var items = sp.GetRequiredService<RecurringRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();

        var travis = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Travis", "x", now, ct) ?? throw new InvalidOperationException("user");
        var partner = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Partner", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(travis.Id, "Our money", now, ct);
        var householdId = household.Id;
        try
        {
            AccountRow checking;
            BankLinkRow link;
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                await uow.Connection.ExecuteAsync("INSERT INTO household_members (household_id, user_id, joined_at) VALUES (@H, @U, @N)", new { H = householdId, U = partner.Id, N = now });
                checking = await accounts.CreateAsync(uow.Connection, householdId, travis.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
                var savings = await accounts.CreateAsync(uow.Connection, householdId, travis.Id, new AccountFields("Savings", "U.S. Bank", AccountType.Savings, 0, "#000000", "piggy-bank", null), now, ct);
                link = await banks.InsertLinkAsync(uow.Connection, householdId, travis.Id, "U.S. Bank", sp.GetRequiredService<DataProtector>().Protect("https://user:pass@bridge.example/simplefin"), now, ct);
                var set = SimpleFinParser.Parse(Feed(), today);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[0], now, ct)).Id, checking.Id, now, ct);
                await banks.SetMappingAsync(uow.Connection, householdId, (await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[1], now, ct)).Id, savings.Id, now, ct);
                var rent = await items.CreateAsync(uow.Connection, householdId, travis.Id,
                    new RecurringFields("Rent", Kind.Bill, 185_000, null, checking.Id, Frequency.Monthly, today, null, false, null, null), now, ct);
                await items.UpsertPaymentAsync(uow.Connection, rent.Id, today, travis.Id, 185_000, now, auto: false, PaymentSource.Manual, ct);
                await accounts.SetRoleAsync(uow.Connection, householdId, checking.Id, AccountRole.Bills, null, now, ct);
                await uow.Connection.ExecuteAsync("UPDATE households SET payday_weekday = 2, auto_match_level = 'off' WHERE id = @H", new { H = householdId });
                await uow.CommitAsync(ct);
            }

            await sp.GetRequiredService<BankSyncService>().SyncAsync(link, travis.Id, "Travis", manual: true, ct);

            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                await households.LockAsync(uow.Connection, householdId, ct);
                await households.ResetDataAsync(uow.Connection, householdId, ct);
                await uow.CommitAsync(ct);
            }

            await using var conn = await db.OpenConnectionAsync(ct);
            foreach (var table in new[] { "accounts", "recurring_items", "bank_links", "external_accounts", "imported_transactions", "sweeps", "payee_rules", "activity_events", "expenses" })
            {
                Assert.Equal(0, await conn.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE household_id = @H", new { H = householdId }));
            }

            Assert.Equal(HouseholdRepository.DefaultCategories.Count, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM categories WHERE household_id = @H", new { H = householdId }));
            Assert.Equal(2, await households.MemberCountAsync(householdId, ct));
            var after = await households.GetForUserAsync(partner.Id, ct) ?? throw new InvalidOperationException("household");
            Assert.Equal(("Our money", 5, AutoMatchLevel.All), (after.Name, after.PaydayWeekday, after.AutoMatchLevel));
            Assert.NotNull(await users.FindByIdAsync(travis.Id, ct));
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = ANY(@Ids)", new { Ids = new[] { travis.Id, partner.Id } });
        }
    }

    [DbFact]
    public async Task Five_pending_transactions_on_one_account_are_all_stored_and_listed()
    {
        var connectionString = Environment.GetEnvironmentVariable(DbFactAttribute.Variable)!;
        await using var sp = Services(connectionString);
        Assert.True(sp.GetRequiredService<DbUpMigrator>().Run());

        var ct = CancellationToken.None;
        var now = DateTime.UtcNow;
        var users = sp.GetRequiredService<UserRepository>();
        var households = sp.GetRequiredService<HouseholdRepository>();
        var accounts = sp.GetRequiredService<AccountRepository>();
        var banks = sp.GetRequiredService<BankRepository>();
        var imports = sp.GetRequiredService<ImportRepository>();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        var sync = sp.GetRequiredService<BankSyncService>();

        var user = await users.CreateAsync($"v7db+{Guid.NewGuid():N}@example.com", "Tester", "x", now, ct) ?? throw new InvalidOperationException("user");
        var household = await households.CreateAsync(user.Id, "V7 DB test", now, ct);
        var householdId = household.Id;

        AccountRow checking, savings;
        BankLinkRow link;
        await using (var uow = await db.BeginUnitOfWorkAsync(ct))
        {
            checking = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("US Bank", "U.S. Bank", AccountType.Checking, 0, "#000000", "landmark", null), now, ct);
            savings = await accounts.CreateAsync(uow.Connection, householdId, user.Id, new AccountFields("Savings", "U.S. Bank", AccountType.Savings, 0, "#000000", "piggy-bank", null), now, ct);
            link = await banks.InsertLinkAsync(uow.Connection, householdId, user.Id, "U.S. Bank", sp.GetRequiredService<DataProtector>().Protect("https://user:pass@bridge.example/simplefin"), now, ct);
            var set = SimpleFinParser.Parse(Feed(), DateOnly.FromDateTime(now));
            var extChecking = await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[0], now, ct);
            var extSavings = await banks.UpsertExternalAccountAsync(uow.Connection, householdId, link.Id, set.Accounts[1], now, ct);
            await banks.SetMappingAsync(uow.Connection, householdId, extChecking.Id, checking.Id, now, ct);
            await banks.SetMappingAsync(uow.Connection, householdId, extSavings.Id, savings.Id, now, ct);
            await uow.CommitAsync(ct);
        }

        try
        {
            var first = await sync.SyncAsync(link, user.Id, "Tester", manual: true, ct);
            Assert.Equal(7, first.NewTransactions); // 5 pending + payroll on checking, 1 pending inflow on savings

            await AssertAllFiveAsync();

            // A second sync of the same feed changes nothing: same ids, nothing new, nothing dropped / superseded / replaced.
            var again = await banks.GetLinkAsync(householdId, link.Id, ct) ?? link;
            var second = await sync.SyncAsync(again, user.Id, "Tester", manual: true, ct);
            Assert.Equal(0, second.NewTransactions);
            await AssertAllFiveAsync();

            // The Friday plan's bills account lists all five and uses the bank's available balance.
            await using (var uow = await db.BeginUnitOfWorkAsync(ct))
            {
                await accounts.SetRoleAsync(uow.Connection, householdId, checking.Id, AccountRole.Bills, null, now, ct);
                await uow.CommitAsync(ct);
            }

            var plan = await sp.GetRequiredService<PlanService>().GetAsync(householdId, null, ct) ?? throw new InvalidOperationException("plan");
            var billsAccount = plan.BillsAccount!;
            Assert.Equal(5, billsAccount.Pending.Count);
            Assert.Equal(13_740, billsAccount.PendingOutCents);
            Assert.True(billsAccount.AvailableFromBank);
            Assert.Equal(234_567, billsAccount.AvailableCents);
            Assert.Equal(5, billsAccount.Feed!.Pending);
        }
        finally
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync("DELETE FROM households WHERE id = @Id", new { Id = householdId });
            await conn.ExecuteAsync("DELETE FROM users WHERE id = @Id", new { Id = user.Id });
        }

        async Task AssertAllFiveAsync()
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var stored = (await conn.QueryAsync<(string ExternalId, bool Pending, string Status, string? IgnoreReason)>(
                "SELECT external_id, pending, status, ignore_reason FROM imported_transactions WHERE household_id = @Id AND account_id = @Account",
                new { Id = householdId, Account = checking.Id })).ToList();
            Assert.Equal(5, stored.Count(r => r.Pending));
            Assert.Equal(new[] { "p-sbux-ta", "p-shell-v2", "p-walmart-1", "p-walmart-2", "p-xfer-sav" }, stored.Where(r => r.Pending).Select(r => r.ExternalId).Order());
            Assert.Contains(stored, r => r is { ExternalId: "p-xfer-sav", Status: "ignored", IgnoreReason: "transfer" }); // auto-ignored transfer leg

            // GET /v1/transactions → pending (every visible pending row, any status), filtered to the account and household-wide.
            Assert.Equal(5, (await imports.ListBankPendingAsync(householdId, checking.Id, null, reviewOrConfirmedOnly: false, ct)).Count);
            Assert.Equal(6, (await imports.ListBankPendingAsync(householdId, null, null, reviewOrConfirmedOnly: false, ct)).Count);

            // GET /v1/pending (v7: any status) groups the same rows; v6 listed pending|confirmed only and hid the transfer leg (4).
            var view = PendingGrouping.Group(
                (await accounts.ListAsync(householdId, ct)).Select(a => new PendingAccount(a.Id, null, a.Name, a.Institution, a.BalanceCents, a.AvailableCents, a.Linked)).ToList(),
                (await imports.ListBankPendingAsync(householdId, null, null, reviewOrConfirmedOnly: false, ct)).Select(r => new PendingEntry<Guid>(r.Id, r.AccountId, r.ExternalAccountId, r.AmountCents)).ToList());
            Assert.Equal(5, view.Single(g => g.Account.AccountId == checking.Id).Count);
            Assert.Equal(4, (await imports.ListBankPendingAsync(householdId, checking.Id, null, reviewOrConfirmedOnly: true, ct)).Count);

            // GET /v1/accounts/{id}/transactions?filter=pending and Account.pendingOutCents.
            Assert.Equal(5, (await imports.ListAccountTransactionsAsync(householdId, new AccountTransactionQuery(checking.Id, "pending", null, null, null, false), null, 50, ct)).Count);
            Assert.Equal(13_740, (await accounts.GetAsync(householdId, checking.Id, ct))!.PendingOutCents);

            // Feed diagnostics: what the bank sent.
            var external = (await banks.ListExternalAccountsAsync(householdId, ct)).Single(e => e.AccountId == checking.Id);
            var feed = MappingV3.ParseFeed(external.LastFeed)!;
            Assert.Equal((5, 1, true), (feed.Pending, feed.Posted, feed.AvailableReported));
        }
    }
}
