using System.Globalization;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Matching;
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

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCouplesCashData(connectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new DataProtector(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray())));
        services.AddSingleton(new SyncOptions());
        services.AddSingleton(new MatcherOptions());
        services.AddSingleton<IMatcherClient>(sp => new MatcherClient(new HttpClient(), sp.GetRequiredService<MatcherOptions>()));
        services.AddSingleton<ISimpleFinClient>(new FakeBridge(Feed));
        services.AddSingleton<PushNotifier>();
        services.AddSingleton<MatcherStatusTracker>();
        services.AddSingleton<MatchingQueue>();
        services.AddSingleton<OccurrenceService>();
        services.AddSingleton<ActivityService>();
        services.AddSingleton<InboxService>();
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
