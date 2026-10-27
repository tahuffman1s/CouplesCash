namespace CouplesCash.Api.Domain;

/// <summary>A transaction added by hand that the bank has not shown yet (signed amount).</summary>
public sealed record ManualOpen(Guid Id, DateOnly PostedOn, long AmountCents, string Description, DateTime CreatedAt);

/// <summary>A bank row of the same account that might be the bank's copy of a manual one (signed amount).</summary>
public sealed record ManualCandidate(Guid Id, DateOnly PostedOn, long AmountCents, string Description, string? Payee);

/// <summary>
/// Pure, unit-tested: which bank rows are the bank's own copies of transactions added by hand. Same direction, the bank row dated
/// from <see cref="DaysBefore"/> days before to <see cref="DaysAfter"/> days after the manual date, and the amount within $1.00 — or
/// within max(25 %, $5) (a tip, a fuel hold) when the descriptions agree (<see cref="PendingReconciler.Similarity"/>). Best first, one-to-one.
/// </summary>
public static class ManualMatcher
{
    public const int DaysBefore = 3;

    public const int DaysAfter = 7;

    /// <summary>Without a matching description the amounts must be this close.</summary>
    public const long PlainToleranceCents = 100;

    public static double? Score(ManualOpen manual, ManualCandidate bank)
    {
        var days = bank.PostedOn.DayNumber - manual.PostedOn.DayNumber;
        if (days < -DaysBefore || days > DaysAfter || manual.AmountCents == 0 || Math.Sign(manual.AmountCents) != Math.Sign(bank.AmountCents))
        {
            return null;
        }

        var diff = Math.Abs(Math.Abs(bank.AmountCents) - Math.Abs(manual.AmountCents));
        var similar = PendingReconciler.Similarity(manual.Description, null, bank.Description, bank.Payee) is not null;
        var tolerance = similar ? PendingReconciler.Tolerance(manual.AmountCents) : PlainToleranceCents;
        if (diff > tolerance)
        {
            return null;
        }

        var amount = 1.0 - ((double)diff / (tolerance + 1));
        var date = 1.0 - (Math.Abs(days) / (double)(DaysAfter + 1));
        return Math.Round((similar ? 0.3 : 0) + (0.5 * amount) + (0.2 * date), 6);
    }

    /// <summary>Pairs (manual, bank), best score first; each manual row and each bank row is used at most once.</summary>
    public static IReadOnlyList<(Guid ManualId, Guid BankId)> Match(IReadOnlyList<ManualOpen> manual, IReadOnlyList<ManualCandidate> bank)
    {
        var pairs = (
            from m in manual
            from b in bank
            let score = Score(m, b)
            where score is not null
            select (Manual: m, Bank: b, Score: score!.Value))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Bank.PostedOn.DayNumber - x.Manual.PostedOn.DayNumber))
            .ThenBy(x => x.Manual.CreatedAt)
            .ThenBy(x => x.Manual.Id)
            .ThenBy(x => x.Bank.Id)
            .ToList();

        var usedManual = new HashSet<Guid>();
        var usedBank = new HashSet<Guid>();
        var result = new List<(Guid, Guid)>();
        foreach (var (m, b, _) in pairs)
        {
            if (usedManual.Contains(m.Id) || usedBank.Contains(b.Id))
            {
                continue;
            }

            usedManual.Add(m.Id);
            usedBank.Add(b.Id);
            result.Add((m.Id, b.Id));
        }

        return result;
    }
}
