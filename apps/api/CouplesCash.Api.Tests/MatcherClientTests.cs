using System.Net;
using System.Text;
using System.Text.Json;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Matching;
using Microsoft.Extensions.Logging;

namespace CouplesCash.Api.Tests;

/// <summary>v6: the matcher's wire format (docs/MATCHER-CONTRACT.md samples), chunking, and every way a call falls back to the built-in engine.</summary>
public class MatcherClientTests
{
    private const string Key = "test-key-test-key-test-key-test-key";

    private static readonly MatcherOptions Options = new() { BaseUrl = "http://matcher:8090", Key = Key };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Key, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var key = request.Headers.TryGetValues(MatcherClient.KeyHeader, out var values) ? values.Single() : null;
            Requests.Add((request.Method, request.RequestUri!, key, body));
            return await respond(request, cancellationToken);
        }

        public static FakeHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

        public static FakeHandler Status(HttpStatusCode status) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"title\":\"x\"}", Encoding.UTF8, "application/problem+json") }));
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static MatcherClient Client(FakeHandler handler, MatcherOptions? options = null, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, options ?? Options);

    /// <summary>The request sample of MATCHER-CONTRACT.md "POST /v1/match".</summary>
    private static MatcherMatchRequest ContractRequest() => new(
        new DateOnly(2026, 10, 26),
        new MatcherMatchOptions("all", 85, 55),
        [new MatcherTransaction("tx-1", "acc-1", new DateOnly(2026, 10, 24), -185000, "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", null, null, false)],
        [new MatcherCandidate("item-1", new DateOnly(2026, 11, 1), "bill", "monthly", 185000, false, ["Rent", "OAKWOOD PROPERTY"], null, false)],
        [new MatcherCategoryRule("KROGER", "cat-1", 3)]);

    private const string ContractResponse =
        """
        { "engine": "fuzzy", "version": "1.0.0",
          "matches": [ { "transactionId": "tx-1", "itemId": "item-1", "dueDate": "2026-11-01", "score": 93,
                         "nameScore": 91, "amountScore": 100, "dateScore": 82, "auto": true,
                         "reason": "name 91% · amount exact · 8 days early" } ],
          "categories": [ { "transactionId": "tx-2", "categoryId": "cat-1", "score": 96, "auto": true, "reason": "payee KROGER (3 confirmations)" } ] }
        """;

    // ---------------------------------------------------------------------------------------------
    // Wire format
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Match_request_is_camelCase_with_the_contract_fields_and_carries_the_key()
    {
        var handler = FakeHandler.Json(ContractResponse);

        await Client(handler).MatchAsync(ContractRequest(), CancellationToken.None);

        var (method, uri, key, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("http://matcher:8090/v1/match", uri.ToString());
        Assert.Equal(Key, key);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("2026-10-26", root.GetProperty("today").GetString());
        var options = root.GetProperty("options");
        Assert.Equal("all", options.GetProperty("autoLevel").GetString());
        Assert.Equal(85, options.GetProperty("autoThreshold").GetInt32());
        Assert.Equal(55, options.GetProperty("suggestThreshold").GetInt32());

        var tx = root.GetProperty("transactions")[0];
        Assert.Equal(["id", "accountId", "postedOn", "amountCents", "description", "payee", "memo", "pending"], tx.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-10-24", tx.GetProperty("postedOn").GetString());
        Assert.Equal(-185000, tx.GetProperty("amountCents").GetInt64());

        var candidate = root.GetProperty("candidates")[0];
        Assert.Equal(["itemId", "dueDate", "kind", "frequency", "amountCents", "amountVaries", "names", "accountId", "paid"],
            candidate.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-11-01", candidate.GetProperty("dueDate").GetString());
        Assert.Equal(["Rent", "OAKWOOD PROPERTY"], candidate.GetProperty("names").EnumerateArray().Select(n => n.GetString() ?? "").ToArray());

        var rule = root.GetProperty("categoryRules")[0];
        Assert.Equal(["payee", "categoryId", "hits"], rule.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Match_response_sample_maps_onto_the_records()
    {
        var response = await Client(FakeHandler.Json(ContractResponse)).MatchAsync(ContractRequest(), CancellationToken.None);

        Assert.Equal("fuzzy", response.Engine);
        Assert.Equal("1.0.0", response.Version);
        var m = Assert.Single(response.Matches);
        Assert.Equal(new MatcherMatch("tx-1", "item-1", new DateOnly(2026, 11, 1), 93, 91, 100, 82, true, "name 91% · amount exact · 8 days early"), m);
        var c = Assert.Single(response.Categories);
        Assert.Equal(new MatcherCategoryMatch("tx-2", "cat-1", 96, true, "payee KROGER (3 confirmations)"), c);
    }

    [Fact]
    public async Task Detect_request_and_response_follow_the_contract_samples()
    {
        var handler = FakeHandler.Json(
            """
            { "suggestions": [ { "name": "Netflix", "bankName": "NETFLIX", "kind": "subscription", "frequency": "monthly",
                                 "amountCents": 1549, "amountVaries": false, "nextDue": "2026-11-07", "score": 92,
                                 "transactionIds": ["a", "b", "c"] } ] }
            """);

        var response = await Client(handler).DetectRecurringAsync(new MatcherDetectRequest(
            new DateOnly(2026, 10, 26),
            [new MatcherDetectTransaction("a", new DateOnly(2026, 9, 7), -1549, "NETFLIX.COM 866-579-7172 CA", null)],
            ["Netflix", "Rent", "OAKWOOD PROPERTY"]), CancellationToken.None);

        var (_, uri, key, body) = Assert.Single(handler.Requests);
        Assert.Equal("/v1/detect-recurring", uri.AbsolutePath);
        Assert.Equal(Key, key);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["today", "transactions", "knownNames"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["id", "postedOn", "amountCents", "description", "payee"],
            doc.RootElement.GetProperty("transactions")[0].EnumerateObject().Select(p => p.Name).ToArray());

        var s = Assert.Single(response.Suggestions);
        Assert.Equal(("Netflix", "NETFLIX", "subscription", "monthly", 1549L, false, new DateOnly(2026, 11, 7), 92), (s.Name, s.BankName, s.Kind, s.Frequency, s.AmountCents, s.AmountVaries, s.NextDue, s.Score));
        Assert.Equal(["a", "b", "c"], s.TransactionIds);
    }

    [Fact]
    public async Task Health_does_not_send_the_key()
    {
        var handler = FakeHandler.Json("""{ "status": "ok", "version": "1.0.0" }""");

        var health = await Client(handler).HealthAsync(CancellationToken.None);

        Assert.Equal(("ok", "1.0.0"), (health.Status, health.Version));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/health", request.Uri.AbsolutePath);
        Assert.Null(request.Key);
    }

    [Fact]
    public async Task Long_texts_are_cut_before_they_reach_the_service()
    {
        var handler = FakeHandler.Json("""{ "engine": "fuzzy", "version": "1", "matches": [], "categories": [] }""");
        var request = ContractRequest() with
        {
            Transactions = [new MatcherTransaction("t", null, new DateOnly(2026, 10, 1), -100, new string('x', 5000), new string('y', 2000), null, false)],
        };

        await Client(handler).MatchAsync(request, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var tx = doc.RootElement.GetProperty("transactions")[0];
        Assert.Equal(MatcherClient.MaxTextLength, tx.GetProperty("description").GetString()!.Length);
        Assert.Equal(MatcherClient.MaxTextLength, tx.GetProperty("payee").GetString()!.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Chunking
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Batches_over_1000_transactions_are_split_and_merged()
    {
        var start = new DateOnly(2026, 1, 1);
        var transactions = Enumerable.Range(0, 2500)
            .Select(i => new MatcherTransaction($"t{i:0000}", null, start.AddDays(i / 10), -1000, "SHOP", null, null, false))
            .ToList();
        var candidates = Enumerable.Range(0, 260).Select(d => new MatcherCandidate("item", start.AddDays(d), "bill", "monthly", 1000, false, ["Shop"], null, false)).ToList();
        var handler = new FakeHandler(async (request, ct) =>
        {
            var body = JsonSerializer.Deserialize<MatcherMatchRequest>(await request.Content!.ReadAsStringAsync(ct), MatcherClient.Json)!;
            var first = body.Transactions[0];
            var json = JsonSerializer.Serialize(new MatcherMatchResponse("fuzzy", "1.0.0",
                [new MatcherMatch(first.Id, "item", first.PostedOn, 90, 90, 100, 100, true, "r")], []), MatcherClient.Json);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });

        var response = await Client(handler).MatchAsync(ContractRequest() with { Transactions = transactions, Candidates = candidates }, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        var sent = handler.Requests.Select(r => JsonSerializer.Deserialize<MatcherMatchRequest>(r.Body, MatcherClient.Json)!).ToList();
        Assert.All(sent, r => Assert.True(r.Transactions.Count <= 1000));
        Assert.Equal(2500, sent.Sum(r => r.Transactions.Count));
        Assert.All(sent, r => Assert.All(r.Candidates, c =>
            Assert.Contains(r.Transactions, t => Math.Abs(t.PostedOn.DayNumber - c.DueDate.DayNumber) <= MatchingRules.CandidateWindowDays)));
        Assert.Equal(3, response.Matches.Count);
    }

    [Fact]
    public void Merge_keeps_one_match_per_transaction_and_per_occurrence_and_drops_categories_of_matched_rows()
    {
        var due = new DateOnly(2026, 11, 1);
        var a = new MatcherMatchResponse("fuzzy", "1", [new MatcherMatch("t1", "rent", due, 80, 0, 0, 0, false, "")], [new MatcherCategoryMatch("t9", "c", 90, false, "")]);
        var b = new MatcherMatchResponse("fuzzy", "1",
            [new MatcherMatch("t2", "rent", due, 95, 0, 0, 0, true, ""), new MatcherMatch("t3", "gym", due, 70, 0, 0, 0, false, "")],
            [new MatcherCategoryMatch("t2", "c", 99, true, "")]);

        var merged = MatcherChunking.Merge([a, b]);

        Assert.Equal(["t2", "t3"], merged.Matches.Select(m => m.TransactionId).Order().ToArray()); // t1 lost the occurrence to the better t2
        Assert.Equal("t9", Assert.Single(merged.Categories).TransactionId);
    }

    [Fact]
    public void A_chunk_with_too_many_candidates_is_halved()
    {
        var start = new DateOnly(2026, 1, 1);
        var transactions = Enumerable.Range(0, 40).Select(i => new MatcherTransaction($"t{i}", null, start.AddDays(i * 30), -100, "X", null, null, false)).ToList();
        var candidates = Enumerable.Range(0, 1200).Select(d => new MatcherCandidate($"i{d % 5}", start.AddDays(d), "bill", "weekly", 100, false, ["X"], null, false)).ToList();

        var parts = MatcherChunking.Split(ContractRequest() with { Transactions = transactions, Candidates = candidates }, maxTransactions: 1000, maxCandidates: 100, windowDays: 10);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Candidates.Count <= 100));
        Assert.Equal(40, parts.Sum(p => p.Transactions.Count));
    }

    // ---------------------------------------------------------------------------------------------
    // Failures → built-in fallback
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MatcherFailure.Unauthorized)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, MatcherFailure.PayloadTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity, MatcherFailure.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, MatcherFailure.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, MatcherFailure.ServerError)]
    public async Task Error_statuses_become_matcher_exceptions(HttpStatusCode status, MatcherFailure expected)
    {
        var ex = await Assert.ThrowsAsync<MatcherException>(() => Client(FakeHandler.Status(status)).MatchAsync(ContractRequest(), CancellationToken.None));
        Assert.Equal(expected, ex.Failure);
        Assert.DoesNotContain("OAKWOOD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_slow_matcher_times_out()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<MatcherException>(() => Client(handler, timeout: TimeSpan.FromMilliseconds(100)).MatchAsync(ContractRequest(), CancellationToken.None));
        Assert.Equal(MatcherFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task Connection_errors_and_garbage_bodies_are_failures_too()
    {
        var refused = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        Assert.Equal(MatcherFailure.Unreachable,
            (await Assert.ThrowsAsync<MatcherException>(() => Client(refused).MatchAsync(ContractRequest(), CancellationToken.None))).Failure);

        var garbage = FakeHandler.Json("<html>proxy error</html>");
        Assert.Equal(MatcherFailure.BadResponse,
            (await Assert.ThrowsAsync<MatcherException>(() => Client(garbage).MatchAsync(ContractRequest(), CancellationToken.None))).Failure);
    }

    [Theory]
    [InlineData(null, Key)]
    [InlineData("http://matcher:8090", "")]
    [InlineData("not a url", Key)]
    public async Task Without_a_base_url_or_key_the_matcher_is_disabled_and_never_called(string? baseUrl, string key)
    {
        var handler = FakeHandler.Json(ContractResponse);
        var options = new MatcherOptions { BaseUrl = baseUrl, Key = key };

        var client = Client(handler, options);
        var ex = await Assert.ThrowsAsync<MatcherException>(() => client.MatchAsync(ContractRequest(), CancellationToken.None));

        Assert.False(client.Enabled);
        Assert.Equal(MatcherFailure.Disabled, ex.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fallback_returns_null_records_the_failure_and_warns_once_a_minute_without_descriptions()
    {
        var status = new MatcherStatusTracker();
        var logger = new ListLogger();
        var client = Client(FakeHandler.Status(HttpStatusCode.Unauthorized));
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var household = Guid.NewGuid();

        var first = await MatchingService.TryMatchAsync(client, status, logger, household, ContractRequest(), now, CancellationToken.None);
        var second = await MatchingService.TryMatchAsync(client, status, logger, household, ContractRequest(), now.AddSeconds(30), CancellationToken.None);
        var third = await MatchingService.TryMatchAsync(client, status, logger, household, ContractRequest(), now.AddSeconds(61), CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Null(third);
        Assert.Equal(MatcherFailure.Unauthorized, status.LastFailure);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count); // t = 0 and t = 61 s
        Assert.All(warnings, w => Assert.DoesNotContain("OAKWOOD", w.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Unauthorized", warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_falls_back_and_a_later_success_clears_the_failure()
    {
        var status = new MatcherStatusTracker();
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var disabled = Client(FakeHandler.Json(ContractResponse), new MatcherOptions());

        Assert.Null(await MatchingService.TryMatchAsync(disabled, status, new ListLogger(), Guid.NewGuid(), ContractRequest(), now, CancellationToken.None));
        Assert.Equal(MatcherFailure.Disabled, status.LastFailure);

        var ok = await MatchingService.TryMatchAsync(Client(FakeHandler.Json(ContractResponse)), status, new ListLogger(), Guid.NewGuid(), ContractRequest(), now, CancellationToken.None);
        Assert.NotNull(ok);
        Assert.Null(status.LastFailure);
        Assert.Equal((true, "1.0.0"), status.CachedHealth(now));
    }

    [Fact]
    public void Unreachable_is_cached_briefly_and_a_good_probe_clears_a_network_failure()
    {
        var status = new MatcherStatusTracker();
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        status.RecordFailure(MatcherFailure.Unreachable, now);
        Assert.Equal((false, null), status.CachedHealth(now.AddSeconds(1)));
        Assert.Null(status.CachedHealth(now.AddSeconds(4)));

        status.RecordProbe(true, "1.0.0", now.AddSeconds(5));
        Assert.Null(status.LastFailure);

        status.RecordFailure(MatcherFailure.Unauthorized, now.AddSeconds(6));
        status.RecordProbe(true, "1.0.0", now.AddSeconds(7));
        Assert.Equal(MatcherFailure.Unauthorized, status.LastFailure); // a wrong key is not fixed by /health answering
    }

    // ---------------------------------------------------------------------------------------------
    // Building the request from household data
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Request_names_are_item_name_plus_bank_names_plus_learned_and_rules_exclude_transfers_and_item_only_rules()
    {
        var item = new RecurringItemRow
        {
            Id = Guid.NewGuid(),
            Name = "Rent",
            Kind = Kind.Bill,
            AmountCents = 185000,
            Frequency = Frequency.Monthly,
            AnchorDate = new DateOnly(2026, 1, 1),
            BankNames = ["Oakwood", "rent"],
            BankNamesLearned = ["OAKWOOD PROPERTY MGMT RENT"],
            AmountVaries = true,
        };
        var row = new ImportedTransactionRow { Id = Guid.NewGuid(), PostedOn = new DateOnly(2026, 10, 30), AmountCents = -185000, Description = "ACH DEBIT OAKWOOD", Pending = true };
        var groceries = Guid.NewGuid();
        PayeeRuleRow[] rules =
        [
            new() { PayeeNorm = "kroger", CategoryId = groceries, Hits = 3 },
            new() { PayeeNorm = "oakwood", ItemId = item.Id, Hits = 1 },
            new() { PayeeNorm = "transfer to sav", CategoryId = groceries, IsTransfer = true, Hits = 2 },
        ];

        var request = MatchingService.BuildRequest(new DateOnly(2026, 10, 26), AutoMatchLevel.Bills, [row],
            [(item, new DateOnly(2026, 11, 1), false), (item, new DateOnly(2026, 10, 1), true)], rules);

        Assert.Equal("bills", request.Options.AutoLevel);
        Assert.Equal((85, 55), (request.Options.AutoThreshold, request.Options.SuggestThreshold));
        var tx = Assert.Single(request.Transactions);
        Assert.Equal((row.Id.ToString("D"), -185000L, true), (tx.Id, tx.AmountCents, tx.Pending));
        Assert.Equal(2, request.Candidates.Count);
        var candidate = request.Candidates[0];
        Assert.Equal(["Rent", "Oakwood", "OAKWOOD PROPERTY MGMT RENT"], candidate.Names); // "rent" duplicates the item name
        Assert.Equal(("bill", "monthly", 185000L, true, false), (candidate.Kind, candidate.Frequency, candidate.AmountCents, candidate.AmountVaries, candidate.Paid));
        Assert.True(request.Candidates[1].Paid);
        var rule = Assert.Single(request.CategoryRules);
        Assert.Equal(("kroger", groceries.ToString("D"), 3), (rule.Payee, rule.CategoryId, rule.Hits));
    }
}
