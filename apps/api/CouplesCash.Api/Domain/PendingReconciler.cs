namespace CouplesCash.Api.Domain;

/// <summary>A stored <c>imported_transactions</c> row of one external account, as the reconciler sees it.</summary>
/// <param name="ExternalId">The provider's transaction id (SimpleFIN ids are unique per account).</param>
/// <param name="Status">pending | confirmed | ignored (review status, not the bank's pending flag).</param>
/// <param name="IgnoreReason">manual | transfer | dropped | superseded, or null.</param>
public sealed record ReconcileExisting(
    Guid Id,
    string? ExternalId,
    bool Pending,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string Status,
    string? IgnoreReason,
    DateTime CreatedAt)
{
    public bool IsHidden => IgnoreReason is PendingReconciler.Dropped or PendingReconciler.Superseded;

    public bool IsReviewed => Status != "pending";
}

/// <summary>
/// A transaction in this Bridge response for the same account. <paramref name="AmountCents"/> is signed. <paramref name="ReplacesExternalId"/>:
/// the provider says this posted transaction is the pending one with that id (Plaid's <c>pending_transaction_id</c>).
/// </summary>
public sealed record ReconcileIncoming(string ExternalId, bool Pending, DateOnly PostedOn, long AmountCents, string Description, string? Payee, string? ReplacesExternalId = null);

/// <summary>An incoming transaction whose id is already stored. <paramref name="Posts"/> = the stored row was pending and this one is posted.</summary>
public sealed record SameIdUpdate(Guid ExistingId, ReconcileIncoming Incoming, bool Posts, bool AmountChanged);

/// <summary>A posted transaction with a new id that replaces the absent pending row <paramref name="ExistingId"/> in place.</summary>
public sealed record PendingReplacement(Guid ExistingId, ReconcileIncoming Incoming, double Score, bool AmountChanged);

/// <summary>
/// The posted version of an absent pending row was already stored as its own row (the bank showed both for a while).
/// <paramref name="HiddenId"/> becomes <c>ignored/superseded</c>. When <paramref name="ApplyToSurvivor"/> the survivor (the
/// reviewed pending row) takes the posted amount/date/description and becomes posted.
/// </summary>
public sealed record SupersededPair(Guid HiddenId, Guid SurvivorId, bool ApplyToSurvivor, ReconcileIncoming Posted);

public sealed record ReconcileResult(
    IReadOnlyList<SameIdUpdate> SameId,
    IReadOnlyList<PendingReplacement> Replacements,
    IReadOnlyList<SupersededPair> Superseded,
    IReadOnlyList<Guid> Dropped,
    IReadOnlyList<ReconcileIncoming> New);

/// <summary>
/// PLAN-V5 §1, pure and unit-tested: reconciles one external account's stored rows with one successful Bridge response.
/// <list type="bullet">
/// <item><b>Same id</b> → refresh in place (pending → posted, amount may change).</item>
/// <item><b>New posted id</b> → replaces a stored <c>pending</c> row that is <b>absent</b> from the response when the posted date is in
/// [pending − 1, pending + 7], |amount| is within max(25 %, $5) of the pending amount (same sign), and the descriptions are similar
/// (token overlap ≥ 0.5 or the same first significant token). Best score wins; each pending row is used once.</item>
/// <item><b>Absent pending</b> whose posted version is already a separate stored row → superseded (see <see cref="SupersededPair"/>).</item>
/// <item><b>Absent pending</b> dated inside the fetch window and more than <see cref="DropAfterDays"/> days old → dropped
/// (only when <c>judgeAbsence</c>: the account was in the response and the Bridge reported no errors).</item>
/// <item>Everything else in the response is <b>new</b>.</item>
/// </list>
/// </summary>
public static class PendingReconciler
{
    public const string Dropped = "dropped";
    public const string Superseded = "superseded";

    /// <summary>A posted transaction may be dated one day before its authorization …</summary>
    public const int DaysBefore = 1;

    /// <summary>… and up to a week after it.</summary>
    public const int DaysAfter = 7;

    /// <summary>A pending row absent from the feed is "dropped" once it is older than this many days.</summary>
    public const int DropAfterDays = 5;

    /// <summary>Tolerance floor: $5.00.</summary>
    public const long MinToleranceCents = 500;

    /// <summary>Tolerance: 25 % of the pending amount.</summary>
    public const double TolerancePercent = 0.25;

    public const double MinOverlap = 0.5;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "at", "on", "in", "to", "for", "llc", "inc", "co", "corp", "ltd", "www", "com", "store",
    };

    public static ReconcileResult Reconcile(
        IReadOnlyList<ReconcileExisting> existing,
        IReadOnlyList<ReconcileIncoming> incoming,
        DateOnly windowStart,
        DateOnly today,
        bool judgeAbsence)
    {
        var byExternal = new Dictionary<string, ReconcileExisting>(StringComparer.Ordinal);
        foreach (var row in existing.Where(r => r.ExternalId is not null).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id))
        {
            byExternal.TryAdd(row.ExternalId!, row);
        }

        var incomingIds = new HashSet<string>(incoming.Select(i => i.ExternalId), StringComparer.Ordinal);
        var sameId = new List<SameIdUpdate>();
        var unmatched = new List<ReconcileIncoming>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tx in incoming)
        {
            if (!seen.Add(tx.ExternalId))
            {
                continue; // the Bridge repeated an id; the first one wins
            }

            if (byExternal.TryGetValue(tx.ExternalId, out var row))
            {
                var posts = row.Pending && !tx.Pending;
                sameId.Add(new SameIdUpdate(row.Id, tx, posts, posts && row.AmountCents != tx.AmountCents));
            }
            else
            {
                unmatched.Add(tx);
            }
        }

        // Absent pending rows: candidates for replacement, supersession or dropping.
        var absent = existing
            .Where(r => r.Pending && !r.IsHidden && (r.ExternalId is null || !incomingIds.Contains(r.ExternalId)))
            .ToList();

        // 1. Replacement: new posted ids × absent pending rows, best score first, one-to-one.
        var replacements = new List<PendingReplacement>();
        var usedPending = new HashSet<Guid>();
        var usedIncoming = new HashSet<ReconcileIncoming>(ReferenceEqualityComparer.Instance);

        // 1a. The provider names the pending row it replaces: that pairing is certain, whatever the amount or the description.
        foreach (var tx in unmatched.Where(t => !t.Pending && t.ReplacesExternalId is not null))
        {
            var pending = absent.FirstOrDefault(p => p.ExternalId == tx.ReplacesExternalId && !usedPending.Contains(p.Id));
            if (pending is null)
            {
                continue;
            }

            usedPending.Add(pending.Id);
            usedIncoming.Add(tx);
            replacements.Add(new PendingReplacement(pending.Id, tx, 1.0, pending.AmountCents != tx.AmountCents));
        }

        var pairs = (
            from tx in unmatched.Where(t => !t.Pending && !usedIncoming.Contains(t))
            from p in absent.Where(p => !usedPending.Contains(p.Id))
            let score = Score(p.PostedOn, p.AmountCents, p.Description, p.Payee, tx.PostedOn, tx.AmountCents, tx.Description, tx.Payee)
            where score is not null
            select (Pending: p, Tx: tx, Score: score!.Value))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Tx.PostedOn.DayNumber - x.Pending.PostedOn.DayNumber))
            .ThenBy(x => x.Pending.CreatedAt)
            .ThenBy(x => x.Pending.Id)
            .ThenBy(x => x.Tx.ExternalId, StringComparer.Ordinal)
            .ToList();

        foreach (var (pending, tx, score) in pairs)
        {
            if (usedPending.Contains(pending.Id) || usedIncoming.Contains(tx))
            {
                continue;
            }

            usedPending.Add(pending.Id);
            usedIncoming.Add(tx);
            replacements.Add(new PendingReplacement(pending.Id, tx, score, pending.AmountCents != tx.AmountCents));
        }

        // 2. Supersession: the posted version is already stored (present in this response, created after the pending row).
        var superseded = new List<SupersededPair>();
        var usedDuplicates = new HashSet<Guid>();
        var presentPosted = sameId
            .Select(s => (Row: existing.First(r => r.Id == s.ExistingId), s.Incoming))
            .Where(x => !x.Row.Pending && !x.Incoming.Pending && !x.Row.IsHidden)
            .ToList();
        var leftover = absent.Where(p => !usedPending.Contains(p.Id)).ToList();
        var dupPairs = (
            from p in leftover
            from d in presentPosted
            where d.Row.CreatedAt > p.CreatedAt
            let score = Score(p.PostedOn, p.AmountCents, p.Description, p.Payee, d.Incoming.PostedOn, d.Incoming.AmountCents, d.Incoming.Description, d.Incoming.Payee)
            where score is not null
            select (Pending: p, Dup: d, Score: score!.Value))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Pending.Id)
            .ThenBy(x => x.Dup.Row.Id)
            .ToList();

        var keptPending = new HashSet<Guid>();
        foreach (var (pending, dup, _) in dupPairs)
        {
            if (usedPending.Contains(pending.Id) || keptPending.Contains(pending.Id) || usedDuplicates.Contains(dup.Row.Id))
            {
                continue;
            }

            usedDuplicates.Add(dup.Row.Id);
            if (!pending.IsReviewed)
            {
                // Nobody touched the authorization: hide it; the posted row lives on.
                usedPending.Add(pending.Id);
                superseded.Add(new SupersededPair(pending.Id, dup.Row.Id, ApplyToSurvivor: false, dup.Incoming));
            }
            else if (!dup.Row.IsReviewed)
            {
                // The authorization carries the review: it absorbs the posted values and the duplicate is hidden.
                usedPending.Add(pending.Id);
                superseded.Add(new SupersededPair(dup.Row.Id, pending.Id, ApplyToSurvivor: true, dup.Incoming));
            }
            else
            {
                // Both reviewed: leave both alone, but never drop (and reverse) the authorization.
                keptPending.Add(pending.Id);
            }
        }

        // 3. Dropped: still absent, inside the fetched window, older than five days.
        var dropped = judgeAbsence
            ? absent
                .Where(p => !usedPending.Contains(p.Id) && !keptPending.Contains(p.Id))
                .Where(p => p.PostedOn >= windowStart && p.PostedOn <= today && today.DayNumber - p.PostedOn.DayNumber > DropAfterDays)
                .OrderBy(p => p.PostedOn).ThenBy(p => p.Id)
                .Select(p => p.Id)
                .ToList()
            : [];

        var fresh = unmatched.Where(t => !usedIncoming.Contains(t)).ToList();
        return new ReconcileResult(sameId, replacements, superseded, dropped, fresh);
    }

    /// <summary>max(25 % of the pending amount, $5.00), in cents.</summary>
    public static long Tolerance(long pendingCents) =>
        Math.Max(MinToleranceCents, (long)Math.Round(Math.Abs(pendingCents) * TolerancePercent, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Match score in (0, 1] of a posted transaction against a pending one, or null when it is not eligible
    /// (date window, same sign, amount tolerance, description similarity). Weights: description 0.5, amount 0.3, date 0.2.
    /// </summary>
    public static double? Score(
        DateOnly pendingOn, long pendingCents, string pendingDescription, string? pendingPayee,
        DateOnly postedOn, long postedCents, string postedDescription, string? postedPayee)
    {
        var days = postedOn.DayNumber - pendingOn.DayNumber;
        if (days < -DaysBefore || days > DaysAfter)
        {
            return null;
        }

        if (pendingCents == 0 || postedCents == 0 || Math.Sign(pendingCents) != Math.Sign(postedCents))
        {
            return null;
        }

        var tolerance = Tolerance(pendingCents);
        var diff = Math.Abs(Math.Abs(postedCents) - Math.Abs(pendingCents));
        if (diff > tolerance)
        {
            return null;
        }

        var similarity = Similarity(pendingDescription, pendingPayee, postedDescription, postedPayee);
        if (similarity is null)
        {
            return null;
        }

        var amountCloseness = 1.0 - ((double)diff / (tolerance + 1));
        var dateCloseness = 1.0 - (Math.Abs(days) / (double)(DaysAfter + 1));
        return Math.Round((0.5 * similarity.Value) + (0.3 * amountCloseness) + (0.2 * dateCloseness), 6);
    }

    /// <summary>
    /// Description similarity in [0, 1]: the overlap coefficient of significant tokens (|A ∩ B| / min(|A|, |B|)), or 0.5 when only the
    /// first significant token agrees; null below <see cref="MinOverlap"/>. Matching payees count as 1.
    /// </summary>
    public static double? Similarity(string? descriptionA, string? payeeA, string? descriptionB, string? payeeB)
    {
        if (!string.IsNullOrWhiteSpace(payeeA) && !string.IsNullOrWhiteSpace(payeeB)
            && TransactionMatcher.NormalisePayee(payeeA, null) == TransactionMatcher.NormalisePayee(payeeB, null))
        {
            return 1.0;
        }

        var a = Tokens(descriptionA);
        var b = Tokens(descriptionB);
        if (a.Count == 0 || b.Count == 0)
        {
            return null;
        }

        var overlap = a.Distinct().Count(b.Contains) / (double)Math.Min(a.Distinct().Count(), b.Distinct().Count());
        if (overlap >= MinOverlap)
        {
            return overlap;
        }

        return a[0] == b[0] ? MinOverlap : null;
    }

    /// <summary>Significant tokens in order: normalised, no digits, no processor noise, no stop words.</summary>
    public static IReadOnlyList<string> Tokens(string? description) =>
        Fingerprint.Normalise(description)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !t.Any(char.IsDigit) && !TransactionMatcher.IsNoiseToken(t) && !StopWords.Contains(t))
            .ToList();
}
