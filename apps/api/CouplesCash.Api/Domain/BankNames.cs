namespace CouplesCash.Api.Domain;

/// <summary>
/// v6 "Bank shows it as" names of a recurring item (PLAN-V6 §2), pure and unit-tested. Users type up to
/// <see cref="MaxNames"/> names (2–80 characters, trimmed, case-insensitive duplicates dropped); confirmed matches append the
/// transaction's normalised payee to a separate learned list (deduped against both lists, at most <see cref="MaxLearned"/>,
/// the oldest learned name evicted first, user names never touched).
/// </summary>
public static class BankNames
{
    public const int MaxNames = 10;
    public const int MinLength = 2;
    public const int MaxLength = 80;
    public const int MaxLearned = 10;

    /// <summary>Trimmed, case-insensitively deduped (first spelling wins), blank entries kept so validation can name them.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string?>? names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names ?? [])
        {
            var name = (raw ?? "").Trim();
            if (name.Length > 0 && !seen.Add(name))
            {
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    /// <summary>Validation message for a request's <c>bankNames</c>, or null when it is fine (null = not sent = fine).</summary>
    public static string? Validate(IEnumerable<string?>? names)
    {
        if (names is null)
        {
            return null;
        }

        var cleaned = Clean(names);
        if (cleaned.Count > MaxNames)
        {
            return $"At most {MaxNames} bank names.";
        }

        return cleaned.Any(n => n.Length is < MinLength or > MaxLength)
            ? $"Each bank name must be {MinLength}–{MaxLength} characters."
            : null;
    }

    /// <summary>
    /// The user list a write stores: <paramref name="bankNames"/> when sent; otherwise the legacy v4 <paramref name="payeeHint"/>
    /// as the only name (blank / null hint = no names, so a v4 PUT without a hint still clears it).
    /// </summary>
    public static IReadOnlyList<string> FromRequest(IEnumerable<string?>? bankNames, string? payeeHint)
    {
        if (bankNames is not null)
        {
            return Clean(bankNames).Where(n => n.Length > 0).Take(MaxNames).ToList();
        }

        var hint = payeeHint?.Trim();
        return string.IsNullOrEmpty(hint) ? [] : [Truncate(hint, MaxLength)];
    }

    /// <summary>The key two names are compared by: the bank-transaction payee normaliser ("JIFFY LUBE #42" ≡ "Jiffy Lube").</summary>
    public static string Key(string? name)
    {
        var norm = TransactionMatcher.NormalisePayee(name, null);
        return norm.Length > 0 ? norm : (name ?? "").Trim().ToLowerInvariant();
    }

    /// <summary>What a confirmed transaction teaches: its normalised payee (or description), upper-cased like a bank statement; null when too short.</summary>
    public static string? LearnedForm(string? payee, string? description)
    {
        var norm = TransactionMatcher.NormalisePayee(payee, description).Trim();
        if (norm.Length < MinLength)
        {
            return null;
        }

        return Truncate(norm.ToUpperInvariant(), MaxLength);
    }

    /// <summary>
    /// Appends <paramref name="candidate"/> to the learned list unless an equal name (by <see cref="Key"/>) is already in either list.
    /// Keeps at most <see cref="MaxLearned"/> learned names by evicting the oldest. Returns null when nothing changes.
    /// </summary>
    public static IReadOnlyList<string>? Learn(IReadOnlyList<string> userNames, IReadOnlyList<string> learned, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var key = Key(candidate);
        if (key.Length == 0 || userNames.Concat(learned).Any(n => Key(n) == key))
        {
            return null;
        }

        var next = learned.Append(candidate.Trim()).ToList();
        if (next.Count > MaxLearned)
        {
            next.RemoveRange(0, next.Count - MaxLearned);
        }

        return next;
    }

    /// <summary>Every name the matcher should compare a transaction with: the item name, then user names, then learned names (deduped).</summary>
    public static IReadOnlyList<string> AllNames(string itemName, IEnumerable<string> userNames, IEnumerable<string> learned)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { itemName }.Concat(userNames).Concat(learned))
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
