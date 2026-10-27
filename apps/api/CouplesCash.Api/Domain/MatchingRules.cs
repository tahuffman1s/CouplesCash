using System.Globalization;

namespace CouplesCash.Api.Domain;

/// <summary>
/// How a stored suggestion was reached (<c>imported_transactions.match_detail</c>, <c>InboxItem.matchDetail</c>): component scores
/// 0–100, a human reason ("name 91% · amount exact · 8 days early") and the engine that produced it.
/// </summary>
public sealed record MatchDetail(int Name, int Amount, int Date, string Reason, string Engine)
{
    public const string Fuzzy = "fuzzy";
    public const string Builtin = "builtin";
}

/// <summary>
/// v6 matching policy shared by the fuzzy matcher path and the built-in fallback (PLAN-V6 §2, MATCHER-CONTRACT "Scoring"). Pure.
/// </summary>
public static class MatchingRules
{
    /// <summary>Candidate occurrences are loaded ±10 days around every transaction (the widest window: amountVaries).</summary>
    public const int CandidateWindowDays = TransactionMatcher.VariesWindowDays;

    /// <summary>Combined score the matcher needs to auto-apply an occurrence match (sent as <c>options.autoThreshold</c>).</summary>
    public const int AutoThreshold = 85;

    /// <summary>Matches below this are not returned (sent as <c>options.suggestThreshold</c>).</summary>
    public const int SuggestThreshold = 55;

    /// <summary>Date score at the edge of the window (contract: linear 100 → 60).</summary>
    public const int DateEdgeScore = 60;

    /// <summary>Amount score at the edge of the tolerance (contract: linear 100 → 70 inside, then −4 per % outside).</summary>
    public const int AmountInsideFloor = 70;

    public static bool AutoAppliesOccurrences(AutoMatchLevel level) => level != AutoMatchLevel.Off;

    public static bool AutoAppliesCategories(AutoMatchLevel level) => level == AutoMatchLevel.All;

    /// <summary>Transfers are auto-ignored only when the household allows it (v5 setting) and automatic matching is not off (v6 level).</summary>
    public static bool AutoIgnoresTransfers(AutoMatchLevel level, bool autoIgnoreTransfers) => autoIgnoreTransfers && level != AutoMatchLevel.Off;

    /// <summary>The built-in auto rule (v3): an occurrence suggestion ≥ 90 whose item a learned payee rule (or a bank name) names.</summary>
    public static bool BuiltinAuto(AutoMatchLevel level, Suggestion suggestion, bool ruleNamesItem) =>
        AutoAppliesOccurrences(level)
        && suggestion.Kind == SuggestionKind.Occurrence
        && suggestion.Confidence >= TransactionMatcher.AutoMatchConfidence
        && ruleNamesItem;

    /// <summary>The window of due dates to load for a batch: [earliest − 10, latest + 10].</summary>
    public static (DateOnly From, DateOnly To) CandidateRange(IEnumerable<DateOnly> postedDates)
    {
        var dates = postedDates.ToList();
        if (dates.Count == 0)
        {
            throw new ArgumentException("At least one date is required.", nameof(postedDates));
        }

        return (dates.Min().AddDays(-CandidateWindowDays), dates.Max().AddDays(CandidateWindowDays));
    }

    // ---------------------------------------------------------------------------------------------
    // Built-in detail (so a fallback suggestion explains itself the same way the matcher does)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Component scores for a built-in occurrence suggestion: name = 100 when a learned rule / bank name names the item, else the best
    /// significant-token overlap with the item's names; amount and date follow the contract formulas with the built-in tolerances.
    /// </summary>
    public static MatchDetail BuiltinOccurrenceDetail(MatchTransaction tx, MatchableOccurrence occurrence, IEnumerable<string> names, bool ruleNamesItem)
    {
        var name = ruleNamesItem ? 100 : NameOverlap(TransactionMatcher.NormalisePayee(tx.Payee, tx.Description), names);
        var allowed = TransactionMatcher.Tolerance(occurrence.AmountCents, occurrence.Once, occurrence.AmountVaries);
        var expected = Math.Abs(occurrence.AmountCents);
        var diff = Math.Abs(tx.Magnitude - expected);
        var amount = AmountScore(diff, allowed, expected);
        var days = tx.PostedOn.DayNumber - occurrence.DueDate.DayNumber;
        var date = DateScore(days, occurrence.WindowDays);
        var reason = Reason(name, diff, allowed, expected, days) + (ruleNamesItem ? " · learned payee" : "");
        return new MatchDetail(name, amount, date, reason, MatchDetail.Builtin);
    }

    /// <summary>A built-in category suggestion: learned rule (70 + hits) or keyword (40).</summary>
    public static MatchDetail BuiltinCategoryDetail(Suggestion suggestion, PayeeRule? rule) =>
        rule?.CategoryId is not null && rule.CategoryId == suggestion.CategoryId
            ? new MatchDetail(100, 0, 0, $"payee {rule.PayeeNorm} ({Plural(rule.Hits, "confirmation")})", MatchDetail.Builtin)
            : new MatchDetail(suggestion.Confidence, 0, 0, "keyword", MatchDetail.Builtin);

    /// <summary>Contract amount score: exact 100, inside the tolerance linear 100 → 70, outside max(0, 70 − 4·excess %).</summary>
    public static int AmountScore(long diff, long allowed, long expected)
    {
        if (diff == 0)
        {
            return 100;
        }

        if (diff <= allowed)
        {
            return RoundHalfUp(100 - ((100.0 - AmountInsideFloor) * diff / Math.Max(1, allowed)));
        }

        var excessPct = (diff - allowed) * 100.0 / Math.Max(1, expected);
        return Math.Max(0, RoundHalfUp(AmountInsideFloor - (4 * excessPct)));
    }

    /// <summary>Contract date score: 100 on the due date, linear to 60 at the window edge, 0 outside.</summary>
    public static int DateScore(int days, int window)
    {
        var distance = Math.Abs(days);
        if (distance > window)
        {
            return 0;
        }

        return window <= 0 ? 100 : RoundHalfUp(100 - ((100.0 - DateEdgeScore) * distance / window));
    }

    /// <summary>"name 100% · amount off by $5.80 · 2 days early" — the same phrases the matcher uses.</summary>
    public static string Reason(int name, long diff, long allowed, long expected, int days)
    {
        var parts = new List<string> { $"name {name}%" };
        if (diff == 0)
        {
            parts.Add("amount exact");
        }
        else if (diff <= allowed)
        {
            parts.Add($"amount off by {Dollars(diff)}");
        }
        else
        {
            parts.Add($"amount off by {Dollars(diff)} ({RoundHalfUp(diff * 100.0 / Math.Max(1, expected))}%)");
        }

        parts.Add(days switch
        {
            0 => "on the due date",
            < 0 => $"{Plural(-days, "day")} early",
            _ => $"{Plural(days, "day")} late",
        });
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Built-in auto-match by name: one of the item's names (item name, bank names, learned names) appears <b>whole</b> in the
    /// transaction — every significant token of the name is in its payee or its description ("Oakwood Property" in "ACH DEBIT OAKWOOD
    /// PROPERTY MGMT RENT", "NETFLIX COM" in "NETFLIX.COM 866-579-7172"). Amount and date must still match the occurrence.
    /// </summary>
    public static bool NameContained(string? payee, string? description, IEnumerable<string> names)
    {
        var texts = TransactionMatcher.PayeeKeys(payee, description).Select(Tokens).Where(t => t.Count > 0).ToList();
        if (texts.Count == 0)
        {
            return false;
        }

        foreach (var name in names)
        {
            var tokens = Tokens(TransactionMatcher.NormalisePayee(name, null));
            if (tokens.Count > 0 && texts.Any(t => tokens.All(t.Contains)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Best overlap coefficient (×100) between the transaction's significant tokens and any name's.</summary>
    public static int NameOverlap(string transactionNorm, IEnumerable<string> names)
    {
        var tokens = Tokens(transactionNorm);
        if (tokens.Count == 0)
        {
            return 0;
        }

        var best = 0;
        foreach (var name in names)
        {
            var other = Tokens(TransactionMatcher.NormalisePayee(name, null));
            if (other.Count == 0)
            {
                continue;
            }

            var shared = tokens.Count(other.Contains);
            best = Math.Max(best, RoundHalfUp(shared * 100.0 / Math.Min(tokens.Count, other.Count)));
        }

        return Math.Min(100, best);
    }

    public static int RoundHalfUp(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static HashSet<string> Tokens(string norm) =>
        norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet(StringComparer.Ordinal);

    private static string Dollars(long cents) => string.Create(CultureInfo.InvariantCulture, $"${cents / 100:N0}.{cents % 100:00}");

    private static string Plural(int n, string word) => n == 1 ? $"{n} {word}" : $"{n} {word}s";
}
