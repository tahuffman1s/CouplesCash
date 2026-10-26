using System.Text;
using System.Text.Json;
using CouplesCash.Api.SimpleFin;

namespace CouplesCash.Api.Tests;

public class SimpleFinParserTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private const string V1Sample =
        """
        {
          "errors": ["Connection to Chase may need to be re-authenticated"],
          "accounts": [
            {
              "org": { "domain": "chase.com", "name": "Chase", "url": "https://chase.com", "sfin-url": "https://bridge/simplefin" },
              "id": "ACT-1",
              "name": "Total Checking",
              "currency": "USD",
              "balance": "2450.50",
              "available-balance": "2400.00",
              "balance-date": 1792756800,
              "transactions": [
                { "id": "tx-1", "posted": 1792670400, "amount": "-1850.00", "description": "RENT PAYMENT" },
                { "id": "tx-2", "posted": 1792670400, "amount": "-12.49", "description": "KROGER #123", "payee": "Kroger", "memo": "card 1234", "pending": false },
                { "id": "tx-3", "posted": 1792756800, "amount": "3200.00", "description": "ACME PAYROLL" },
                { "id": "tx-4", "posted": 1792756800, "amount": "-4.50", "description": "SQ *COFFEE", "pending": true }
              ]
            },
            {
              "org": { "domain": "visa.example", "name": "Visa" },
              "id": "ACT-2",
              "name": "Visa Signature",
              "currency": "USD",
              "balance": "-540.00",
              "balance-date": 1792756800,
              "transactions": []
            }
          ]
        }
        """;

    [Fact]
    public void Parses_the_v1_bridge_shape()
    {
        var set = SimpleFinParser.Parse(V1Sample, Today);

        Assert.Equal(["Connection to Chase may need to be re-authenticated"], set.Errors);
        Assert.Equal(2, set.Accounts.Count);

        var checking = set.Accounts[0];
        Assert.Equal("ACT-1", checking.Id);
        Assert.Equal("Total Checking", checking.Name);
        Assert.Equal("USD", checking.Currency);
        Assert.Equal(245050, checking.BalanceCents);
        Assert.Equal(240000, checking.AvailableCents);
        Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc), checking.BalanceAt);
        Assert.Equal("Chase", checking.OrgName);
        Assert.Equal("chase.com", checking.OrgDomain);
        Assert.Equal(4, checking.Transactions.Count);

        var rent = checking.Transactions[0];
        Assert.Equal("tx-1", rent.Id);
        Assert.Equal(new DateOnly(2026, 10, 22), rent.PostedOn);
        Assert.Equal(-185000, rent.AmountCents);
        Assert.Equal("RENT PAYMENT", rent.Description);
        Assert.Null(rent.Payee);
        Assert.Null(rent.Memo);
        Assert.False(rent.Pending);

        var kroger = checking.Transactions[1];
        Assert.Equal(-1249, kroger.AmountCents);
        Assert.Equal("Kroger", kroger.Payee);
        Assert.Equal("card 1234", kroger.Memo);

        Assert.Equal(320000, checking.Transactions[2].AmountCents);
        Assert.True(checking.Transactions[3].Pending);
        Assert.Equal(-450, checking.Transactions[3].AmountCents);

        var visa = set.Accounts[1];
        Assert.Equal(-54000, visa.BalanceCents);
        Assert.Null(visa.AvailableCents);
        Assert.Empty(visa.Transactions);
    }

    [Fact]
    public void Tolerates_missing_optional_fields()
    {
        const string json = """{ "accounts": [ { "id": "A", "name": "Minimal", "balance": "0", "balance-date": 1792756800 } ] }""";

        var set = SimpleFinParser.Parse(json, Today);

        Assert.Empty(set.Errors);
        var account = Assert.Single(set.Accounts);
        Assert.Equal("USD", account.Currency);
        Assert.Equal("", account.OrgName);
        Assert.Null(account.OrgDomain);
        Assert.Null(account.AvailableCents);
        Assert.Empty(account.Transactions);
    }

    [Fact]
    public void Parses_the_v2_draft_shape_with_errlist_connections_and_posted_zero()
    {
        const string json =
            """
            {
              "errlist": [ { "code": "con.auth", "msg": "Re-authenticate with your bank", "conn_id": "c1" }, { "code": "gen.x", "msg": "Re-authenticate with your bank" } ],
              "connections": [ { "conn_id": "c1", "name": "Credit Union", "org_id": "cu", "org_url": "https://cu.example", "sfin_url": "https://bridge" } ],
              "accounts": [
                {
                  "id": "A1", "conn_id": "c1", "name": "Share Draft", "currency": "USD", "balance": 100.5, "balance-date": "1792756800",
                  "transactions": [
                    { "id": "p1", "posted": 0, "amount": "-9.99", "description": "PENDING THING", "transacted_at": 1792670400 },
                    { "id": "p2", "posted": 0, "amount": "-1.00", "description": "NO DATE" }
                  ]
                }
              ]
            }
            """;

        var set = SimpleFinParser.Parse(json, Today);

        Assert.Equal(["Re-authenticate with your bank"], set.Errors); // deduplicated
        var account = Assert.Single(set.Accounts);
        Assert.Equal("Credit Union", account.OrgName);
        Assert.Equal(10050, account.BalanceCents);
        Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc), account.BalanceAt);

        var pending = account.Transactions[0];
        Assert.True(pending.Pending);
        Assert.Equal(new DateOnly(2026, 10, 22), pending.PostedOn);

        var undated = account.Transactions[1];
        Assert.True(undated.Pending);
        Assert.Equal(Today, undated.PostedOn);
    }

    [Theory]
    [InlineData("1234.56", 123456)]
    [InlineData("-12.34", -1234)]
    [InlineData("1,850.00", 185000)]
    [InlineData("$3.5", 350)]
    [InlineData("0", 0)]
    [InlineData("0.125", 12)]   // banker's rounding: 12.5 → 12
    [InlineData("0.135", 14)]   // 13.5 → 14
    [InlineData("-0.125", -12)]
    [InlineData("1e2", 10000)]
    public void ParseCents_handles_decimal_strings_with_bankers_rounding(string amount, long cents)
    {
        Assert.Equal(cents, SimpleFinParser.ParseCents(amount));
    }

    [Fact]
    public void ParseCents_accepts_json_numbers()
    {
        using var doc = JsonDocument.Parse("""{ "a": -12.34, "b": "7" }""");

        Assert.Equal(-1234, SimpleFinParser.ParseCents(doc.RootElement.GetProperty("a")));
        Assert.Equal(700, SimpleFinParser.ParseCents(doc.RootElement.GetProperty("b")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "accounts": [ { "id": "A", "transactions": [ { "id": "t", "amount": "abc" } ] } ] }""")]
    [InlineData("""{ "accounts": [ { "id": "A", "transactions": [ { "id": "t" } ] } ] }""")]
    [InlineData("""{ "accounts": [ { "name": "no id" } ] }""")]
    public void Malformed_responses_are_BadResponse(string json)
    {
        var ex = Assert.Throws<SimpleFinException>(() => SimpleFinParser.Parse(json, Today));

        Assert.Equal(SimpleFinError.BadResponse, ex.Error);
    }

    [Fact]
    public void FromUnix_is_utc()
    {
        var at = SimpleFinParser.FromUnix(1792756800);

        Assert.Equal(DateTimeKind.Utc, at.Kind);
        Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0), at);
    }
}

public class SimpleFinClientHelperTests
{
    private const string ClaimUrl = "https://beta-bridge.simplefin.org/simplefin/claim/DEMO-TOKEN";

    [Fact]
    public void DecodeSetupToken_accepts_standard_base64()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClaimUrl));

        Assert.Equal(new Uri(ClaimUrl), SimpleFinClient.DecodeSetupToken(token));
    }

    [Fact]
    public void DecodeSetupToken_accepts_urlsafe_unpadded_and_whitespace()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClaimUrl)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(new Uri(ClaimUrl), SimpleFinClient.DecodeSetupToken($"  {token[..10]}\n{token[10..]} "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not base64!!!")]
    [InlineData("aGVsbG8=")]              // "hello": not a URL
    [InlineData("ZnRwOi8vZXhhbXBsZS5jb20=")] // ftp://example.com
    public void DecodeSetupToken_rejects_garbage(string token)
    {
        var ex = Assert.Throws<SimpleFinException>(() => SimpleFinClient.DecodeSetupToken(token));

        Assert.Equal(SimpleFinError.InvalidToken, ex.Error);
    }

    [Fact]
    public void Rewrite_points_at_the_override_but_keeps_path_and_credentials()
    {
        var url = new Uri("https://user:secret@beta-bridge.simplefin.org/simplefin/claim/abc");

        var rewritten = SimpleFinClient.Rewrite(url, "http://127.0.0.1:8765");

        Assert.Equal("http://user:secret@127.0.0.1:8765/simplefin/claim/abc", rewritten.ToString());
        Assert.Same(url, SimpleFinClient.Rewrite(url, null));
    }

    [Fact]
    public void A_pending_charge_is_dated_when_it_happened_never_ahead()
    {
        // Today is 2026-10-23 (1792756800 = 12:00 UTC); some banks put the expected post day in `posted`.
        const string json =
            """
            {
              "errors": [],
              "accounts": [
                {
                  "id": "A1", "name": "Checking", "currency": "USD", "balance": "10.00", "balance-date": 1792756800,
                  "transactions": [
                    { "id": "p1", "posted": 1793016000, "transacted_at": 1792756800, "amount": "-25.00", "description": "WALMART", "pending": true },
                    { "id": "p2", "posted": 1793016000, "amount": "-7.40", "description": "STARBUCKS", "pending": true },
                    { "id": "t1", "posted": 1792670400, "transacted_at": 1792584000, "amount": "-5.00", "description": "POSTED" }
                  ]
                }
              ]
            }
            """;

        var today = new DateOnly(2026, 10, 23);
        var txs = SimpleFinParser.Parse(json, today).Accounts[0].Transactions;

        Assert.Equal(today, txs[0].PostedOn);                   // transacted_at, not the post day 3 days out
        Assert.Equal(today, txs[1].PostedOn);                   // no transacted_at: never in the future
        Assert.Equal(new DateOnly(2026, 10, 22), txs[2].PostedOn); // posted rows keep their posted date
    }

    [Fact]
    public void History_requests_add_older_transactions_but_keep_the_newest_balances()
    {
        static SimpleFinAccount Account(string id, long balance, params string[] txIds) =>
            new(id, id, "USD", balance, null, DateTime.UnixEpoch, "Bank", null,
                [.. txIds.Select(t => new SimpleFinTransaction(t, new DateOnly(2026, 10, 23), -100, t, null, null, false))]);
        var recent = new SimpleFinAccountSet(["notice"], [Account("A", 500, "t3", "t2")]);
        var older = new SimpleFinAccountSet(["notice", "older"], [Account("A", 100, "t2", "t1"), Account("B", 7, "b1")]);

        var merged = recent.WithHistory(older);

        Assert.Equal(["notice", "older"], merged.Errors);
        Assert.Equal(["A", "B"], merged.Accounts.Select(a => a.Id));
        Assert.Equal(500, merged.Accounts[0].BalanceCents);
        Assert.Equal(["t3", "t2", "t1"], merged.Accounts[0].Transactions.Select(t => t.Id));
    }

    [Fact]
    public void AccountsEndpoint_strips_credentials_into_basic_auth_and_builds_the_query()
    {
        var (endpoint, basic) = SimpleFinClient.AccountsEndpoint(
            "https://user:p%40ss@beta-bridge.simplefin.org/simplefin",
            new DateOnly(2026, 7, 25), new DateOnly(2026, 10, 24), includePending: true, baseUrl: null);

        Assert.Equal("https://beta-bridge.simplefin.org/simplefin/accounts?start-date=1784937600&end-date=1792800000&pending=1", endpoint.ToString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss")), basic);
    }

    [Fact]
    public void AccountsEndpoint_omits_pending_when_not_requested_and_honours_the_override()
    {
        var (endpoint, _) = SimpleFinClient.AccountsEndpoint(
            "https://user:pass@bridge.example/simplefin",
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), includePending: false, baseUrl: "http://localhost:9000");

        Assert.Equal("http://localhost:9000/simplefin/accounts?start-date=1790812800&end-date=1790899200", endpoint.ToString());
    }

    [Fact]
    public void Mask_hides_credentials()
    {
        var masked = SimpleFinClient.Mask("https://user:secret@beta-bridge.simplefin.org/simplefin/accounts?pending=1");

        Assert.DoesNotContain("secret", masked);
        Assert.DoesNotContain("user:", masked);
        Assert.StartsWith("https://***@beta-bridge.simplefin.org/simplefin/accounts", masked);
        Assert.Equal("<invalid url>", SimpleFinClient.Mask("nope"));
    }
}
