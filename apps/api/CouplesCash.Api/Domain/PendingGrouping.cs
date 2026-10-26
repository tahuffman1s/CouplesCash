namespace CouplesCash.Api.Domain;

/// <summary>
/// A place pending transactions can belong to: a CouplesCash account (<paramref name="AccountId"/> set) or, for rows of a bank account
/// that is not mapped yet, the external account (<paramref name="AccountId"/> null, <paramref name="ExternalAccountId"/> set).
/// </summary>
public sealed record PendingAccount(Guid? AccountId, Guid? ExternalAccountId, string Name, string Institution, long BalanceCents, long? AvailableCents, bool Linked);

/// <summary>One pending transaction: <paramref name="Item"/> is whatever the caller shows (a DTO); amount is signed.</summary>
public sealed record PendingEntry<T>(T Item, Guid? AccountId, Guid? ExternalAccountId, long AmountCents);

public sealed record PendingBucket<T>(PendingAccount Account, IReadOnlyList<T> Items, int Count, long OutCents, long InCents);

/// <summary>
/// <c>GET /v1/pending</c> grouping (PLAN-V6 §3), pure: rows go to their mapped account, else to their external account, else to an
/// "Other" bucket. Linked accounts are listed even without pending rows; accounts with pending rows come first, by pending outflow
/// descending, then by name.
/// </summary>
public static class PendingGrouping
{
    public const string OtherName = "Other";

    public static IReadOnlyList<PendingBucket<T>> Group<T>(IReadOnlyList<PendingAccount> accounts, IReadOnlyList<PendingEntry<T>> entries)
    {
        var byAccount = accounts.Where(a => a.AccountId is not null).GroupBy(a => a.AccountId!.Value).ToDictionary(g => g.Key, g => g.First());
        var byExternal = accounts.Where(a => a.AccountId is null && a.ExternalAccountId is not null)
            .GroupBy(a => a.ExternalAccountId!.Value).ToDictionary(g => g.Key, g => g.First());
        var other = new PendingAccount(null, null, OtherName, "", 0, null, false);

        var buckets = new Dictionary<PendingAccount, List<PendingEntry<T>>>(ReferenceEqualityComparer.Instance);
        foreach (var account in accounts.Where(a => a.Linked))
        {
            buckets.TryAdd(account, []);
        }

        foreach (var entry in entries)
        {
            var home = entry.AccountId is { } accountId && byAccount.TryGetValue(accountId, out var mapped) ? mapped
                : entry.ExternalAccountId is { } externalId && byExternal.TryGetValue(externalId, out var external) ? external
                : other;
            if (!buckets.TryGetValue(home, out var list))
            {
                list = [];
                buckets[home] = list;
            }

            list.Add(entry);
        }

        return buckets
            .Select(b => new PendingBucket<T>(
                b.Key,
                b.Value.Select(e => e.Item).ToList(),
                b.Value.Count,
                b.Value.Where(e => e.AmountCents < 0).Sum(e => -e.AmountCents),
                b.Value.Where(e => e.AmountCents > 0).Sum(e => e.AmountCents)))
            .OrderBy(b => b.Count == 0 ? 1 : 0)
            .ThenByDescending(b => b.OutCents)
            .ThenBy(b => ReferenceEquals(b.Account, other) ? 1 : 0)
            .ThenBy(b => b.Account.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
