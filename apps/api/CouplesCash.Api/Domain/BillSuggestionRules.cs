using System.Security.Cryptography;
using System.Text;

namespace CouplesCash.Api.Domain;

/// <summary>A regular payment the matcher found (<c>POST /v1/detect-recurring</c>), in API terms.</summary>
public sealed record DetectedBill(
    string Name,
    string BankName,
    Kind Kind,
    Frequency Frequency,
    long AmountCents,
    bool AmountVaries,
    DateOnly NextDue,
    int Score,
    IReadOnlyList<Guid> TransactionIds)
{
    public byte[] Fingerprint => BillSuggestionRules.Fingerprint(BankName, Frequency);
}

/// <summary>A stored suggestion as the upsert plan sees it.</summary>
public sealed record StoredBillSuggestion(Guid Id, byte[] Fingerprint, string Status);

/// <summary>What one detection run changes: open suggestions to insert/refresh, and stale open ones to remove.</summary>
public sealed record BillSuggestionPlan(IReadOnlyList<DetectedBill> Upserts, IReadOnlyList<Guid> RemoveOpen);

/// <summary>
/// Found bills (PLAN-V6 §5), pure: the dedupe fingerprint, the upsert plan (dismissed and accepted fingerprints never come back), and
/// how an accepted suggestion becomes an item whose occurrences cover the transactions it was found from.
/// </summary>
public static class BillSuggestionRules
{
    public const string Open = "open";
    public const string Accepted = "accepted";
    public const string Dismissed = "dismissed";

    /// <summary><c>sha256(normalised bank name || '|' || frequency)</c> — "NETFLIX.COM 866…" and "Netflix" monthly are the same suggestion.</summary>
    public static byte[] Fingerprint(string bankName, Frequency frequency) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{BankNames.Key(bankName)}|{frequency.ToDb()}"));

    /// <summary>
    /// Detected bills whose fingerprint is new or still open are upserted (open); dismissed or accepted fingerprints are left alone (never
    /// resurrected); open suggestions the matcher no longer reports are removed. Duplicate fingerprints in one run keep the best score.
    /// </summary>
    public static BillSuggestionPlan Plan(IReadOnlyList<StoredBillSuggestion> stored, IReadOnlyList<DetectedBill> detected)
    {
        var byFingerprint = stored.ToDictionary(s => Convert.ToHexString(s.Fingerprint), StringComparer.Ordinal);
        var upserts = new List<DetectedBill>();
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bill in detected.OrderByDescending(d => d.Score).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = Convert.ToHexString(bill.Fingerprint);
            if (!kept.Add(key))
            {
                continue;
            }

            if (byFingerprint.TryGetValue(key, out var existing) && existing.Status != Open)
            {
                continue;
            }

            upserts.Add(bill);
        }

        var remove = stored.Where(s => s.Status == Open && !kept.Contains(Convert.ToHexString(s.Fingerprint))).Select(s => s.Id).ToList();
        return new BillSuggestionPlan(upserts, remove);
    }

    /// <summary>
    /// The anchor date of an accepted suggestion: <paramref name="nextDue"/> stepped back whole periods until it is on or before the
    /// earliest sample transaction, so the past payments are occurrences that can be matched. Month-based steps keep the day of month
    /// (a step that would clamp, e.g. the 31st into a 30-day month, keeps going back to a month that has the day).
    /// </summary>
    public static DateOnly AnchorFor(Frequency frequency, DateOnly nextDue, DateOnly? earliestSample)
    {
        if (earliestSample is not { } earliest || earliest >= nextDue || frequency == Frequency.Once)
        {
            return nextDue;
        }

        var months = frequency switch
        {
            Frequency.Monthly => 1,
            Frequency.Quarterly => 3,
            Frequency.Yearly => 12,
            _ => 0,
        };

        if (months == 0)
        {
            var step = frequency == Frequency.Weekly ? 7 : 14;
            var periods = (int)Math.Ceiling((nextDue.DayNumber - earliest.DayNumber) / (double)step);
            return nextDue.AddDays(-step * periods);
        }

        for (var n = 1; n <= 240; n++)
        {
            var candidate = nextDue.AddMonths(-months * n);
            if (candidate <= earliest && candidate.Day == nextDue.Day)
            {
                return candidate;
            }
        }

        return earliest;
    }

    /// <summary>How far from an occurrence a found-bill sample may be to count as paying it (half a period, capped).</summary>
    public static int MatchWindowDays(Frequency frequency) => frequency switch
    {
        Frequency.Weekly => 3,
        Frequency.Biweekly => 6,
        Frequency.Monthly => 10,
        Frequency.Quarterly => 15,
        Frequency.Yearly => 20,
        _ => 7,
    };

    /// <summary>The occurrence of <paramref name="rule"/> closest to <paramref name="postedOn"/> within the window (earlier wins a tie), or null.</summary>
    public static DateOnly? NearestOccurrence(Recurrence rule, DateOnly postedOn, int windowDays) =>
        RecurrenceEngine.Expand(rule, postedOn.AddDays(-windowDays), postedOn.AddDays(windowDays))
            .Select(d => (Date: d, Distance: Math.Abs(d.DayNumber - postedOn.DayNumber)))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Date)
            .Select(x => (DateOnly?)x.Date)
            .FirstOrDefault();

    /// <summary>Parses the matcher's frequency ("monthly"); found bills are never yearly or once.</summary>
    public static Frequency? ParseFrequency(string? value) =>
        Enum.TryParse<Frequency>(value, ignoreCase: true, out var f) && Enum.IsDefined(f) && f != Frequency.Once ? f : null;

    public static Kind? ParseKind(string? value) =>
        Enum.TryParse<Kind>(value, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null;
}
