namespace CouplesCash.Api.Domain;

/// <summary>An imported transaction as the matcher sees it. <paramref name="AmountCents"/> is signed (negative = outflow).</summary>
public sealed record MatchTransaction(Guid Id, Guid? AccountId, DateOnly PostedOn, long AmountCents, string Description, string? Payee)
{
    public bool IsOutflow => AmountCents < 0;

    public long Magnitude => Math.Abs(AmountCents);
}

/// <summary>
/// An unpaid occurrence of an active recurring item. <paramref name="Once"/> (v4) = a planned one-off, matched with the wider window and
/// tolerance; <paramref name="AmountVaries"/> (v6) = utilities / card payments, the widest window and tolerance.
/// </summary>
public sealed record MatchableOccurrence(Guid ItemId, string ItemName, Kind Kind, long AmountCents, DateOnly DueDate, bool Once = false, bool AmountVaries = false)
{
    /// <summary>±5 days for recurring items, ±7 for one-offs, ±10 when the amount varies (v6).</summary>
    public int WindowDays => AmountVaries ? TransactionMatcher.VariesWindowDays : Once ? TransactionMatcher.OnceWindowDays : TransactionMatcher.RecurringWindowDays;
}

/// <summary>A learned <c>payee_rules</c> row. <paramref name="IsTransfer"/> (v5): the payee was marked as a one-sided transfer.</summary>
public sealed record PayeeRule(string PayeeNorm, Guid? CategoryId, Guid? ItemId, int Hits, bool IsTransfer = false);

public enum SuggestionKind
{
    None,
    Category,
    Occurrence,
    Transfer,
}

public sealed record Suggestion(SuggestionKind Kind, Guid? CategoryId, Guid? ItemId, DateOnly? DueDate, int Confidence)
{
    public static readonly Suggestion None = new(SuggestionKind.None, null, null, null, 0);

    public static Suggestion Category(Guid categoryId, int confidence) => new(SuggestionKind.Category, categoryId, null, null, confidence);

    public static Suggestion Occurrence(Guid itemId, DateOnly dueDate, int confidence) => new(SuggestionKind.Occurrence, null, itemId, dueDate, confidence);

    public static readonly Suggestion Transfer = new(SuggestionKind.Transfer, null, null, null, TransactionMatcher.TransferConfidence);

    /// <summary>v5: a transfer suggestion at another confidence (keyword 60, learned payee 85).</summary>
    public static Suggestion TransferAt(int confidence) => new(SuggestionKind.Transfer, null, null, null, confidence);
}

/// <summary>
/// The suggestion engine (PLAN-V3 "Suggestion engine"), pure and unit-tested. For each transaction, in order:
/// recurring match (±5 days, max(2 %, $1); v4 one-offs: ±7 days, max(15 %, $10); sign ↔ kind, unpaid only) → 90, +5 when the item's learned payee rule matches;
/// transfer between two mapped accounts (same |amount|, opposite sign, ≤ 3 days) → ignore, 80;
/// v5 learned transfer payee → transfer, 85; payee rule → category, 70 + min(hits, 20); v5 transfer keyword → transfer, 60;
/// built-in keyword table → 40; otherwise none, 0.
/// </summary>
public static class TransactionMatcher
{
    public const int RecurringConfidence = 90;
    public const int RuleBonus = 5;
    public const int RuleConfidence = 70;
    public const int RuleMaxHits = 20;
    public const int KeywordConfidence = 40;
    public const int TransferConfidence = 80;
    public const int RecurringWindowDays = 5;
    public const int TransferWindowDays = 3;

    /// <summary>v5: one-sided transfer keyword ("ONLINE TRANSFER TO SAV") — shown, never auto-applied, below the bulk threshold.</summary>
    public const int TransferKeywordConfidence = 60;

    /// <summary>v5: a payee the household marked as a transfer before; auto-ignored when <c>autoIgnoreTransfers</c> is on.</summary>
    public const int TransferRuleConfidence = 85;

    /// <summary>v5 manual pairing: the other side within ±5 days and |amount| within 1 %.</summary>
    public const int ManualTransferWindowDays = 5;

    /// <summary>v4: a planned one-off (<c>frequency = once</c>) may post up to a week either side of its planned date.</summary>
    public const int OnceWindowDays = 7;

    /// <summary>v6: an item whose amount varies may post up to 10 days either side, within max(35 %, $15.00) (MATCHER-CONTRACT tolerances).</summary>
    public const int VariesWindowDays = 10;

    /// <summary>Confidence at which the scheduler may mark the occurrence paid without a human (plus a learned rule).</summary>
    public const int AutoMatchConfidence = 90;

    /// <summary>Confidence at which "Confirm all suggested" accepts a row.</summary>
    public const int BulkConfidence = 70;

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "pos", "debit", "card", "purchase", "ach", "web", "id", "ref", "auth", "pending", "chk", "tst", "sq", "pp", "usd", "xx", "xxxx", "visa", "mastercard", "pymt", "txn", "trans", "des", "ppd", "ccd",
    };

    /// <summary>v5 one-sided transfer phrases (normalised). The last three only count for money in (a card payment received).</summary>
    private static readonly (string Phrase, bool InflowOnly)[] TransferKeywords =
    [
        ("online transfer", false), ("transfer to", false), ("transfer from", false), ("transfer", false), ("xfer", false),
        ("payment thank you", true), ("autopay payment", true), ("online pmt", true),
    ];

    /// <summary>Payments to people, not transfers between your own accounts.</summary>
    private static readonly string[] PeerToPeer = ["zelle", "venmo", "cash app", "cashapp", "square cash", "paypal"];

    /// <summary>(normalised keyword phrase, default category name, applies to inflows). Longer phrases first so "uber eats" wins over "uber".</summary>
    private static readonly (string Phrase, string Category, bool Inflow)[] Keywords =
    [
        ("direct deposit", "Income", true), ("direct dep", "Income", true), ("payroll", "Income", true), ("salary", "Income", true), ("paycheck", "Income", true),
        ("uber eats", "Fun", false), ("prime video", "Subscriptions", false), ("amazon prime", "Subscriptions", false), ("apple com bill", "Subscriptions", false),
        ("trader joe", "Groceries", false), ("whole foods", "Groceries", false), ("food lion", "Groceries", false), ("h e b", "Groceries", false), ("stop shop", "Groceries", false),
        ("state farm", "Insurance", false), ("rite aid", "Health", false), ("circle k", "Transportation", false), ("gas station", "Transportation", false),
        ("kroger", "Groceries", false), ("walmart", "Groceries", false), ("aldi", "Groceries", false), ("safeway", "Groceries", false), ("costco", "Groceries", false),
        ("publix", "Groceries", false), ("heb", "Groceries", false), ("wegmans", "Groceries", false), ("meijer", "Groceries", false), ("sprouts", "Groceries", false),
        ("winco", "Groceries", false), ("instacart", "Groceries", false), ("lidl", "Groceries", false), ("giant", "Groceries", false), ("grocery", "Groceries", false),
        ("netflix", "Subscriptions", false), ("spotify", "Subscriptions", false), ("hulu", "Subscriptions", false), ("disney", "Subscriptions", false), ("hbo", "Subscriptions", false),
        ("youtube", "Subscriptions", false), ("paramount", "Subscriptions", false), ("peacock", "Subscriptions", false), ("audible", "Subscriptions", false), ("icloud", "Subscriptions", false),
        ("patreon", "Subscriptions", false), ("adobe", "Subscriptions", false), ("microsoft 365", "Subscriptions", false), ("xbox", "Subscriptions", false), ("playstation", "Subscriptions", false), ("nintendo", "Subscriptions", false),
        ("shell", "Transportation", false), ("chevron", "Transportation", false), ("exxon", "Transportation", false), ("mobil", "Transportation", false), ("bp", "Transportation", false),
        ("marathon", "Transportation", false), ("speedway", "Transportation", false), ("wawa", "Transportation", false), ("sheetz", "Transportation", false), ("quiktrip", "Transportation", false),
        ("uber", "Transportation", false), ("lyft", "Transportation", false), ("parking", "Transportation", false), ("toll", "Transportation", false), ("fuel", "Transportation", false),
        ("electric", "Utilities", false), ("power", "Utilities", false), ("energy", "Utilities", false), ("water", "Utilities", false), ("comcast", "Utilities", false),
        ("xfinity", "Utilities", false), ("spectrum", "Utilities", false), ("verizon", "Utilities", false), ("t mobile", "Utilities", false), ("at t", "Utilities", false),
        ("internet", "Utilities", false), ("utility", "Utilities", false), ("utilities", "Utilities", false),
        ("insurance", "Insurance", false), ("geico", "Insurance", false), ("progressive", "Insurance", false), ("allstate", "Insurance", false), ("usaa", "Insurance", false),
        ("pharmacy", "Health", false), ("cvs", "Health", false), ("walgreens", "Health", false), ("dental", "Health", false), ("clinic", "Health", false),
        ("medical", "Health", false), ("hospital", "Health", false), ("doctor", "Health", false),
        ("restaurant", "Fun", false), ("starbucks", "Fun", false), ("mcdonald", "Fun", false), ("chipotle", "Fun", false), ("doordash", "Fun", false),
        ("grubhub", "Fun", false), ("coffee", "Fun", false), ("cafe", "Fun", false), ("pizza", "Fun", false), ("taco", "Fun", false),
        ("burger", "Fun", false), ("grill", "Fun", false), ("cinema", "Fun", false), ("amc", "Fun", false), ("steam", "Fun", false),
    ];

    /// <summary>
    /// <c>payee_norm</c>: lower-cased payee (or description) without punctuation, tokens containing digits (store numbers,
    /// card digits, dates) and common processor noise. "POS DEBIT CARD 1234 KROGER #123 10/02" → "kroger".
    /// </summary>
    public static string NormalisePayee(string? payee, string? description)
    {
        var source = string.IsNullOrWhiteSpace(payee) ? description : payee;
        var tokens = Fingerprint.Normalise(source).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Any(char.IsDigit) && !NoiseTokens.Contains(t));
        var result = string.Join(' ', tokens);
        return result.Length > 0 ? result : Fingerprint.Normalise(source);
    }

    /// <summary>Processor noise ("pos", "debit", "ach" …) that never identifies a payee.</summary>
    public static bool IsNoiseToken(string token) => NoiseTokens.Contains(token);

    /// <summary>
    /// v5: does the description read like a transfer between own accounts ("ONLINE TRANSFER TO SAV", "XFER", and for money in
    /// "PAYMENT THANK YOU" / "AUTOPAY PAYMENT" / "ONLINE PMT")? Zelle, Venmo, Cash App and PayPal never count.
    /// </summary>
    public static bool IsTransferKeyword(string? description, string? payee, bool isOutflow)
    {
        var haystack = $" {Fingerprint.Normalise(description)} {Fingerprint.Normalise(payee)} ";
        if (haystack.Trim().Length == 0 || PeerToPeer.Any(p => haystack.Contains($" {p} ", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var (phrase, inflowOnly) in TransferKeywords)
        {
            if (inflowOnly && isOutflow)
            {
                continue;
            }

            if (haystack.Contains($" {phrase} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>v5 manual pairing: other account, opposite sign, ≤ 5 days apart, |amount| within 1 % (at least 1 cent).</summary>
    public static bool IsTransferPeer(MatchTransaction a, MatchTransaction b)
    {
        if (a.Id == b.Id || a.AmountCents == 0 || b.AmountCents == 0 || Math.Sign(a.AmountCents) == Math.Sign(b.AmountCents))
        {
            return false;
        }

        if (a.AccountId is { } x && b.AccountId is { } y && x == y)
        {
            return false;
        }

        if (Math.Abs(a.PostedOn.DayNumber - b.PostedOn.DayNumber) > ManualTransferWindowDays)
        {
            return false;
        }

        var tolerance = Math.Max(1, (long)Math.Round(Math.Max(a.Magnitude, b.Magnitude) * 0.01, MidpointRounding.AwayFromZero));
        return Math.Abs(a.Magnitude - b.Magnitude) <= tolerance;
    }

    /// <summary>
    /// v5 "Match to a bill or income" ranking, 0–100: <c>50·amount closeness + 30·date closeness + 20·payee rule hit</c>, or null when
    /// the sign does not fit the kind or the amount is off by more than max(50 %, $10) without a rule naming the item.
    /// </summary>
    public static int? CandidateScore(Kind kind, long expectedCents, DateOnly dueDate, MatchTransaction tx, int windowDays, bool ruleHit)
    {
        if (!SignMatchesKind(kind, tx.IsOutflow))
        {
            return null;
        }

        var days = Math.Abs(dueDate.DayNumber - tx.PostedOn.DayNumber);
        if (days > Math.Max(0, windowDays))
        {
            return null;
        }

        var allowed = Math.Max(1000, (long)Math.Round(Math.Abs(expectedCents) * 0.5, MidpointRounding.AwayFromZero));
        var diff = Math.Abs(Math.Abs(expectedCents) - tx.Magnitude);
        if (diff > allowed && !ruleHit)
        {
            return null;
        }

        var amount = Math.Max(0.0, 1.0 - ((double)diff / allowed));
        var date = windowDays <= 0 ? 1.0 : Math.Max(0.0, 1.0 - ((double)days / (windowDays + 1)));
        var score = (50 * amount) + (30 * date) + (ruleHit ? 20 : 0);
        return Math.Clamp((int)Math.Round(score, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>
    /// Recurring: max(2 % of the expected amount, $1.00). One-off (<paramref name="once"/>, v4): max(15 %, $10.00). Amount varies
    /// (<paramref name="varies"/>, v6): max(35 %, $15.00). In cents.
    /// </summary>
    public static long Tolerance(long expectedCents, bool once = false, bool varies = false) =>
        varies ? Math.Max(1500, (long)Math.Round(Math.Abs(expectedCents) * 0.35, MidpointRounding.AwayFromZero))
        : once ? Math.Max(1000, (long)Math.Round(Math.Abs(expectedCents) * 0.15, MidpointRounding.AwayFromZero))
        : Math.Max(100, (long)Math.Round(Math.Abs(expectedCents) * 0.02, MidpointRounding.AwayFromZero));

    public static bool AmountMatches(long expectedCents, long actualMagnitude, bool once = false, bool varies = false) =>
        Math.Abs(Math.Abs(expectedCents) - actualMagnitude) <= Tolerance(expectedCents, once, varies);

    /// <summary>Money out pays a bill, subscription or (v7) transfer; money in pays income.</summary>
    public static bool SignMatchesKind(Kind kind, bool isOutflow) => kind.IsMoneyOut() == isOutflow;

    /// <summary>Default category name for a description, or null. Income keywords apply to inflows only, the rest to outflows only.</summary>
    public static string? KeywordCategory(string? description, bool isOutflow)
    {
        var haystack = $" {Fingerprint.Normalise(description)} ";
        if (haystack.Length <= 2)
        {
            return null;
        }

        foreach (var (phrase, category, inflow) in Keywords)
        {
            if (inflow == isOutflow)
            {
                continue;
            }

            if (haystack.Contains($" {phrase} ", StringComparison.Ordinal))
            {
                return category;
            }
        }

        return null;
    }

    /// <summary>Transactions that form a transfer pair: both accounts mapped and different, amounts opposite, ≤ 3 days apart. Each transaction pairs at most once.</summary>
    public static IReadOnlySet<Guid> DetectTransfers(IEnumerable<MatchTransaction> transactions, IReadOnlySet<Guid> mappedAccountIds) =>
        DetectTransferPairs(transactions, mappedAccountIds).SelectMany(p => new[] { p.A, p.B }).ToHashSet();

    /// <summary>The pairs behind <see cref="DetectTransfers"/>: (earlier, later) by posted date, then id.</summary>
    public static IReadOnlyList<(Guid A, Guid B)> DetectTransferPairs(IEnumerable<MatchTransaction> transactions, IReadOnlySet<Guid> mappedAccountIds)
    {
        var candidates = transactions
            .Where(t => t.AccountId is { } a && mappedAccountIds.Contains(a) && t.AmountCents != 0)
            .DistinctBy(t => t.Id)
            .OrderBy(t => t.PostedOn).ThenBy(t => t.Id)
            .ToList();
        var paired = new HashSet<Guid>();
        var pairs = new List<(Guid, Guid)>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var a = candidates[i];
            if (paired.Contains(a.Id))
            {
                continue;
            }

            for (var j = i + 1; j < candidates.Count; j++)
            {
                var b = candidates[j];
                if (paired.Contains(b.Id) || b.AccountId == a.AccountId)
                {
                    continue;
                }

                if (b.PostedOn.DayNumber - a.PostedOn.DayNumber > TransferWindowDays)
                {
                    break;
                }

                if (a.AmountCents == -b.AmountCents)
                {
                    paired.Add(a.Id);
                    paired.Add(b.Id);
                    pairs.Add((a.Id, b.Id));
                    break;
                }
            }
        }

        return pairs;
    }

    /// <summary>Best unpaid occurrence for one transaction (closest due date, then closest amount), or null.</summary>
    public static MatchableOccurrence? FindOccurrence(MatchTransaction tx, IEnumerable<MatchableOccurrence> unpaid) =>
        unpaid
            .Where(o => SignMatchesKind(o.Kind, tx.IsOutflow))
            .Where(o => Math.Abs(o.DueDate.DayNumber - tx.PostedOn.DayNumber) <= o.WindowDays)
            .Where(o => AmountMatches(o.AmountCents, tx.Magnitude, o.Once, o.AmountVaries))
            .OrderBy(o => Math.Abs(o.DueDate.DayNumber - tx.PostedOn.DayNumber))
            .ThenBy(o => Math.Abs(Math.Abs(o.AmountCents) - tx.Magnitude))
            .ThenBy(o => o.ItemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>Suggestion for one transaction without transfer detection (that needs the whole batch; see <see cref="SuggestAll"/>).</summary>
    public static Suggestion Suggest(
        MatchTransaction tx,
        IEnumerable<MatchableOccurrence> unpaid,
        IReadOnlyDictionary<string, PayeeRule> rulesByNorm,
        IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        var norm = NormalisePayee(tx.Payee, tx.Description);
        rulesByNorm.TryGetValue(norm, out var rule);

        if (FindOccurrence(tx, unpaid) is { } occurrence)
        {
            var confidence = RecurringConfidence + (rule?.ItemId == occurrence.ItemId ? RuleBonus : 0);
            return Suggestion.Occurrence(occurrence.ItemId, occurrence.DueDate, confidence);
        }

        return SuggestCategory(tx, rule, categoriesByName);
    }

    /// <summary>Does a learned rule name this item for this transaction's payee (the auto-match precondition)?</summary>
    public static bool RuleMatchesItem(MatchTransaction tx, Guid itemId, IReadOnlyDictionary<string, PayeeRule> rulesByNorm) =>
        rulesByNorm.TryGetValue(NormalisePayee(tx.Payee, tx.Description), out var rule) && rule.ItemId == itemId;

    /// <summary>
    /// Suggestions for a batch. Each unpaid occurrence is handed out once (two rent debits do not both match the same
    /// due date); transfers are detected across <paramref name="transactions"/> plus <paramref name="otherPending"/>
    /// (already-pending rows that may be the other leg).
    /// </summary>
    public static IReadOnlyDictionary<Guid, Suggestion> SuggestAll(
        IReadOnlyList<MatchTransaction> transactions,
        IEnumerable<MatchableOccurrence> unpaid,
        IEnumerable<PayeeRule> rules,
        IReadOnlyDictionary<string, Guid> categoriesByName,
        IReadOnlySet<Guid> mappedAccountIds,
        IEnumerable<MatchTransaction>? otherPending = null)
    {
        var rulesByNorm = rules.GroupBy(r => r.PayeeNorm, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var available = unpaid.ToList();
        var transfers = DetectTransfers(transactions.Concat(otherPending ?? []).DistinctBy(t => t.Id), mappedAccountIds);
        var result = new Dictionary<Guid, Suggestion>();

        foreach (var tx in transactions.OrderBy(t => t.PostedOn).ThenBy(t => t.Id))
        {
            var norm = NormalisePayee(tx.Payee, tx.Description);
            rulesByNorm.TryGetValue(norm, out var rule);

            if (FindOccurrence(tx, available) is { } occurrence)
            {
                available.Remove(occurrence);
                var confidence = RecurringConfidence + (rule?.ItemId == occurrence.ItemId ? RuleBonus : 0);
                result[tx.Id] = Suggestion.Occurrence(occurrence.ItemId, occurrence.DueDate, confidence);
                continue;
            }

            if (transfers.Contains(tx.Id))
            {
                result[tx.Id] = Suggestion.Transfer;
                continue;
            }

            result[tx.Id] = SuggestCategory(tx, rule, categoriesByName);
        }

        return result;
    }

    /// <summary>
    /// v6: what the built-in engine suggests when there is no occurrence match (the fuzzy matcher found none, or only categories are
    /// wanted): learned transfer payee (85) → learned category (70 + hits) → transfer keyword (60) → category keyword (40) → none.
    /// </summary>
    public static Suggestion SuggestWithoutOccurrence(MatchTransaction tx, IReadOnlyDictionary<string, PayeeRule> rulesByNorm, IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        rulesByNorm.TryGetValue(NormalisePayee(tx.Payee, tx.Description), out var rule);
        return SuggestCategory(tx, rule, categoriesByName);
    }

    /// <summary>
    /// After recurring and pair-transfer checks: learned transfer payee (85) → learned category (70 + hits) → transfer keyword (60)
    /// → category keyword (40) → none.
    /// </summary>
    private static Suggestion SuggestCategory(MatchTransaction tx, PayeeRule? rule, IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        if (rule is { IsTransfer: true })
        {
            return Suggestion.TransferAt(TransferRuleConfidence);
        }

        if (rule?.CategoryId is { } ruleCategory)
        {
            return Suggestion.Category(ruleCategory, RuleConfidence + Math.Clamp(rule.Hits, 0, RuleMaxHits));
        }

        if (IsTransferKeyword(tx.Description, tx.Payee, tx.IsOutflow))
        {
            return Suggestion.TransferAt(TransferKeywordConfidence);
        }

        if (KeywordCategory(tx.Description, tx.IsOutflow) is { } name && categoriesByName.TryGetValue(name, out var categoryId))
        {
            return Suggestion.Category(categoryId, KeywordConfidence);
        }

        if (tx.Payee is not null && KeywordCategory(tx.Payee, tx.IsOutflow) is { } payeeName && categoriesByName.TryGetValue(payeeName, out var payeeCategory))
        {
            return Suggestion.Category(payeeCategory, KeywordConfidence);
        }

        return Suggestion.None;
    }
}
