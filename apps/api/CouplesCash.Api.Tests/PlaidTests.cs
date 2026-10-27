using System.Net;
using System.Text.Json;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Plaid;
using CouplesCash.Api.SimpleFin;
using Microsoft.Extensions.Configuration;

namespace CouplesCash.Api.Tests;

/// <summary>v7 Plaid: parsing into the shared feed shape, error handling, scheduling and the explicit pending → posted link.</summary>
public class PlaidTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);
    private static readonly DateTime BalanceAt = new(2026, 10, 27, 13, 0, 0, DateTimeKind.Utc);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private const string Page = """
        {
          "accounts": [
            { "account_id": "chk", "name": "Plaid Checking", "mask": "0000", "type": "depository", "subtype": "checking",
              "balances": { "available": 2345.67, "current": 2408.07, "iso_currency_code": "USD" } },
            { "account_id": "card", "name": "Visa Signature", "mask": "4471", "type": "credit", "subtype": "credit card",
              "balances": { "available": 4460.00, "current": 540.00, "limit": 5000, "iso_currency_code": "USD" } }
          ],
          "transactions": [
            { "transaction_id": "t-coffee", "account_id": "chk", "amount": 4.25, "date": "2026-10-27", "authorized_date": "2026-10-26",
              "name": "Blue Bottle", "merchant_name": "Blue Bottle Coffee", "original_description": "SQ *BLUE BOTTLE 0042", "pending": true },
            { "transaction_id": "t-pay", "account_id": "chk", "amount": -1500.00, "date": "2026-10-23", "name": "ACME PAYROLL",
              "pending": false, "pending_transaction_id": null },
            { "transaction_id": "t-diner", "account_id": "chk", "amount": 48.00, "date": "2026-10-25", "authorized_date": "2026-10-24",
              "name": "The Diner", "merchant_name": "The Diner", "pending": false, "pending_transaction_id": "p-diner" },
            { "transaction_id": "t-later", "account_id": "chk", "amount": 9.99, "date": "2026-10-29", "name": "FUTURE AUTH", "pending": true },
            { "transaction_id": "t-card", "account_id": "card", "amount": 62.40, "date": "2026-10-26", "name": "SHELL OIL", "pending": false },
            { "transaction_id": "t-pay", "account_id": "chk", "amount": -1500.00, "date": "2026-10-23", "name": "duplicate id", "pending": false }
          ],
          "total_transactions": 6
        }
        """;

    private static SimpleFinAccountSet Parse(string json = Page)
    {
        var page = PlaidParser.ParseTransactionsPage(Json(json));
        return PlaidParser.ToAccountSet(page.Accounts, page.Transactions, "U.S. Bank", BalanceAt, Today, []);
    }

    [Fact]
    public void Amounts_flip_sign_money_out_is_negative()
    {
        var chk = Parse().Accounts.Single(a => a.Id == "chk");
        Assert.Equal(-425, chk.Transactions.Single(t => t.Id == "t-coffee").AmountCents);
        Assert.Equal(150_000, chk.Transactions.Single(t => t.Id == "t-pay").AmountCents);
    }

    [Fact]
    public void Balances_follow_the_simplefin_shape_credit_owed_is_negative_without_available()
    {
        var set = Parse();
        var chk = set.Accounts.Single(a => a.Id == "chk");
        Assert.Equal((240_807L, (long?)234_567, "USD", "U.S. Bank", BalanceAt), (chk.BalanceCents, chk.AvailableCents, chk.Currency, chk.OrgName, chk.BalanceAt));
        var card = set.Accounts.Single(a => a.Id == "card");
        Assert.Equal((-54_000L, (long?)null), (card.BalanceCents, card.AvailableCents));
        Assert.Equal("Visa Signature ··4471", card.Name);
        Assert.Equal("Plaid Checking ··0000", chk.Name);
    }

    [Fact]
    public void Pending_is_dated_by_authorization_and_never_after_today()
    {
        var chk = Parse().Accounts.Single(a => a.Id == "chk");
        var coffee = chk.Transactions.Single(t => t.Id == "t-coffee");
        Assert.Equal((true, new DateOnly(2026, 10, 26)), (coffee.Pending, coffee.PostedOn));
        Assert.Equal(Today, chk.Transactions.Single(t => t.Id == "t-later").PostedOn);
        // posted: the posted date, not the authorization
        Assert.Equal(new DateOnly(2026, 10, 25), chk.Transactions.Single(t => t.Id == "t-diner").PostedOn);
    }

    [Fact]
    public void Description_is_the_banks_wording_payee_the_merchant_and_posted_rows_name_their_pending_row()
    {
        var chk = Parse().Accounts.Single(a => a.Id == "chk");
        var coffee = chk.Transactions.Single(t => t.Id == "t-coffee");
        Assert.Equal(("SQ *BLUE BOTTLE 0042", "Blue Bottle Coffee", (string?)null), (coffee.Description, coffee.Payee, coffee.ReplacesId));
        var diner = chk.Transactions.Single(t => t.Id == "t-diner");
        Assert.Equal(("The Diner", (string?)null, "p-diner"), (diner.Description, diner.Payee, diner.ReplacesId));
        // a repeated id is kept once; transactions go to their own account
        Assert.Single(chk.Transactions, t => t.Id == "t-pay");
        Assert.Single(Parse().Accounts.Single(a => a.Id == "card").Transactions);
    }

    [Fact]
    public void Errors_parse_and_classify()
    {
        var body = PlaidParser.ParseError("""{ "error_type": "ITEM_ERROR", "error_code": "ITEM_LOGIN_REQUIRED", "error_message": "the login details of this item have changed", "display_message": null }""")!;
        Assert.Equal(("ITEM_ERROR", "ITEM_LOGIN_REQUIRED"), (body.Type, body.Code));
        Assert.Equal(SimpleFinError.ReauthRequired, PlaidClient.Classify(body, HttpStatusCode.BadRequest));
        Assert.Contains("Reconnect", PlaidClient.Message(body));

        Assert.Equal(SimpleFinError.AccessDenied, PlaidClient.Classify(body with { Code = "INVALID_ACCESS_TOKEN" }, HttpStatusCode.BadRequest));
        Assert.Equal(SimpleFinError.InvalidToken, PlaidClient.Classify(body with { Code = "INVALID_PUBLIC_TOKEN" }, HttpStatusCode.BadRequest));
        Assert.Equal(SimpleFinError.Unreachable, PlaidClient.Classify(body with { Type = "INSTITUTION_ERROR", Code = "INSTITUTION_DOWN" }, HttpStatusCode.BadRequest));
        Assert.Equal(SimpleFinError.BadResponse, PlaidClient.Classify(body with { Type = "INVALID_REQUEST", Code = "INVALID_FIELD" }, HttpStatusCode.BadRequest));
        Assert.Equal(SimpleFinError.Unreachable, PlaidClient.Classify(body with { Code = "RATE_LIMIT_EXCEEDED" }, HttpStatusCode.TooManyRequests));
        Assert.Equal("The bank is down", PlaidClient.Message(body with { Code = "INSTITUTION_DOWN", DisplayMessage = "The bank is down" }));
        Assert.Null(PlaidParser.ParseError("not json"));
        Assert.Null(PlaidParser.ParseError("""{ "ok": true }"""));
    }

    [Fact]
    public void Item_status_gives_the_last_successful_update_and_the_institution()
    {
        var item = Json("""{ "item": { "item_id": "i1", "institution_name": "U.S. Bank" }, "status": { "transactions": { "last_successful_update": "2026-10-27T08:15:00Z" } } }""");
        Assert.Equal(new DateTime(2026, 10, 27, 8, 15, 0, DateTimeKind.Utc), PlaidParser.LastSuccessfulUpdate(item));
        Assert.Equal("U.S. Bank", PlaidParser.InstitutionName(item));
        Assert.Null(PlaidParser.LastSuccessfulUpdate(Json("""{ "item": {}, "status": null }""")));
    }

    [Fact]
    public void Plaid_links_are_checked_every_interval_every_few_minutes_while_refreshing_and_never_while_waiting_for_a_sign_in()
    {
        var now = new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);
        var hour = TimeSpan.FromHours(1);
        var poll = TimeSpan.FromMinutes(3);
        Assert.True(BankSyncRules.IsPlaidDue(now.AddMinutes(-61), null, null, false, now, hour, poll));
        Assert.False(BankSyncRules.IsPlaidDue(now.AddMinutes(-10), null, null, false, now, hour, poll));
        // refreshing: every 3 minutes until refresh_until
        Assert.True(BankSyncRules.IsPlaidDue(now.AddMinutes(-10), now.AddMinutes(-4), now.AddMinutes(10), false, now, hour, poll));
        Assert.False(BankSyncRules.IsPlaidDue(now.AddMinutes(-2), now.AddMinutes(-2), now.AddMinutes(10), false, now, hour, poll));
        Assert.False(BankSyncRules.IsPlaidDue(now.AddMinutes(-10), now.AddMinutes(-4), now.AddMinutes(-1), false, now, hour, poll));
        // the bank wants a new sign-in: not even on the interval
        Assert.False(BankSyncRules.IsPlaidDue(now.AddHours(-5), null, null, true, now, hour, poll));
    }

    [Fact]
    public void A_posted_row_naming_its_pending_row_replaces_it_even_when_the_wording_and_amount_changed()
    {
        var created = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var pending = new ReconcileExisting(Guid.CreateVersion7(), "p-diner", true, new DateOnly(2026, 10, 24), -3_000, "SQ *DINER AUTH", null, "confirmed", null, created);
        var other = new ReconcileExisting(Guid.CreateVersion7(), "p-other", true, new DateOnly(2026, 10, 24), -4_800, "THE DINER", null, "pending", null, created.AddMinutes(1));
        var posted = new ReconcileIncoming("t-diner", false, new DateOnly(2026, 10, 25), -4_800, "THE DINER", null, ReplacesExternalId: "p-diner");
        var result = PendingReconciler.Reconcile([pending, other], [posted], Today.AddDays(-30), Today, judgeAbsence: false);

        // the fuzzy rule would have picked "p-other" (same wording, same amount); the provider's link wins
        var replacement = Assert.Single(result.Replacements);
        Assert.Equal((pending.Id, true), (replacement.ExistingId, replacement.AmountChanged));
        Assert.Empty(result.New);
    }

    [Fact]
    public void Options_default_to_production_and_derive_the_oauth_page_from_an_https_origin()
    {
        IConfiguration Config(params (string Key, string? Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

        var none = PlaidOptions.Read(Config());
        Assert.False(none.Configured);
        Assert.Equal(("production", "https://production.plaid.com", (string?)null), (none.Environment, none.ResolvedBaseUrl, none.RedirectUri));

        var https = PlaidOptions.Read(Config(("Plaid:ClientId", "id"), ("Plaid:Secret", "s"), ("WebAuthn:Origin", "https://ledger.example.com")));
        Assert.True(https.Configured);
        Assert.Equal("https://ledger.example.com/settings/banks/plaid-oauth", https.RedirectUri);

        var sandbox = PlaidOptions.Read(Config(("Plaid:Environment", "Sandbox"), ("WebAuthn:Origin", "http://localhost:5173"), ("Plaid:IntervalMinutes", "15")));
        Assert.Equal(("sandbox", "https://sandbox.plaid.com", (string?)null, 15.0), (sandbox.Environment, sandbox.ResolvedBaseUrl, sandbox.RedirectUri, sandbox.IntervalMinutes));
    }
}

public class ProviderTwinsTests
{
    private static readonly DateOnly D = new(2026, 10, 20);

    private static TwinRow R(int day, long cents) => new(Guid.CreateVersion7(), D.AddDays(day), cents);

    [Fact]
    public void The_same_transaction_from_the_other_provider_pairs_on_the_exact_amount_within_three_days()
    {
        var plaidPay = R(0, 150_000);
        var simplefinPay = R(-2, 150_000);
        var pairs = ProviderTwins.Match([plaidPay, R(0, -1_549)], [simplefinPay, R(-4, -1_549), R(0, -1_550)]);
        // the payroll pairs; Netflix is 4 days off (no) and $15.50 is not the same amount (no)
        Assert.Equal([(plaidPay.Id, simplefinPay.Id)], pairs);
    }

    [Fact]
    public void Two_coffees_on_consecutive_days_pair_one_to_one_closest_date_first()
    {
        var newMonday = R(0, -425);
        var newTuesday = R(1, -425);
        var oldMonday = R(0, -425);
        var oldTuesday = R(1, -425);
        var pairs = ProviderTwins.Match([newTuesday, newMonday], [oldTuesday, oldMonday]);
        Assert.Equal(2, pairs.Count);
        Assert.Contains((newMonday.Id, oldMonday.Id), pairs);
        Assert.Contains((newTuesday.Id, oldTuesday.Id), pairs);
        // a third coffee has no twin left
        Assert.Single(ProviderTwins.Match([R(0, -425), R(0, -425)], [oldMonday]));
        Assert.Empty(ProviderTwins.Match([R(0, 0)], [R(0, 0)]));
    }
}

/// <summary>v7: the built-in matcher recognises a bill from either provider's wording.</summary>
public class ProviderWordingTests
{
    [Fact]
    public void A_rule_learned_on_the_description_still_hits_when_a_merchant_name_is_sent_too()
    {
        var itemId = Guid.CreateVersion7();
        var rules = new Dictionary<string, PayeeRule>(StringComparer.Ordinal) { ["oakwood property mgmt rent"] = new("oakwood property mgmt rent", null, itemId, 3) };
        var tx = new MatchTransaction(Guid.CreateVersion7(), null, new DateOnly(2026, 11, 1), -185_000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", "Oakwood");
        Assert.True(TransactionMatcher.RuleMatchesItem(tx, itemId, rules));
        Assert.Equal(itemId, TransactionMatcher.FindRule(rules, tx.Payee, tx.Description)?.ItemId);
        Assert.Equal(["oakwood", "oakwood property mgmt rent"], TransactionMatcher.PayeeKeys(tx.Payee, tx.Description));
    }

    [Fact]
    public void A_bills_name_counts_when_all_its_words_are_in_the_payee_or_the_description()
    {
        Assert.True(MatchingRules.NameContained("Netflix", "NETFLIX.COM 866-579-7172 CA", ["Streaming", "NETFLIX COM"]));
        Assert.True(MatchingRules.NameContained(null, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", ["Oakwood Property"]));
        Assert.True(MatchingRules.NameContained("Netflix", "NFLX", ["Netflix"]));
        // every word of the name, as whole words
        Assert.False(MatchingRules.NameContained(null, "CARD PURCHASE SHELL OIL", ["Car Insurance"]));
        Assert.False(MatchingRules.NameContained(null, "CITY WATER UTIL", ["Water Softener"]));
        Assert.False(MatchingRules.NameContained(null, "", ["Rent"]));
        Assert.False(MatchingRules.NameContained("Netflix", "NETFLIX.COM", []));
    }
}
