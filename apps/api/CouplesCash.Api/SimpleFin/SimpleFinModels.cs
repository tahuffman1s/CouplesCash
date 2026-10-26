namespace CouplesCash.Api.SimpleFin;

/// <summary><c>SimpleFin__BaseUrl</c>: when set, the claim URL and the access URL are rewritten to this scheme/host/port (fake server in tests and the smoke test).</summary>
public sealed class SimpleFinOptions
{
    public const string SectionName = "SimpleFin";

    public string? BaseUrl { get; set; }

    public static SimpleFinOptions Read(IConfiguration configuration)
    {
        var value = configuration.GetSection(SectionName)["BaseUrl"];
        return new SimpleFinOptions { BaseUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/') };
    }
}

/// <summary>Why a Bridge call failed; maps to the contract's 400 / 502 / 503 answers.</summary>
public enum SimpleFinError
{
    /// <summary>The setup token is not base64 / not a URL, or the Bridge answered 403 (already claimed or expired).</summary>
    InvalidToken,

    /// <summary>Network failure, timeout or a 5xx from the Bridge.</summary>
    Unreachable,

    /// <summary>403 on /accounts: the access URL was revoked or is wrong.</summary>
    AccessDenied,

    /// <summary>402: the SimpleFIN subscription lapsed.</summary>
    PaymentRequired,

    /// <summary>A 2xx body that is not protocol-shaped JSON.</summary>
    BadResponse,
}

public sealed class SimpleFinException(SimpleFinError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public SimpleFinError Error { get; } = error;
}

/// <summary>One parsed <c>GET /accounts</c> response. Amounts are already integer cents.</summary>
public sealed record SimpleFinAccountSet(IReadOnlyList<string> Errors, IReadOnlyList<SimpleFinAccount> Accounts)
{
    /// <summary>
    /// Adds an older history response to this (newer) one: balances and account details stay this response's, the older
    /// transactions are appended where their id is new, accounts only the older one has are kept, and errors are combined.
    /// </summary>
    public SimpleFinAccountSet WithHistory(SimpleFinAccountSet older)
    {
        var olderById = older.Accounts.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var accounts = Accounts.Select(a =>
        {
            if (!olderById.TryGetValue(a.Id, out var history))
            {
                return a;
            }

            var ids = a.Transactions.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            return a with { Transactions = [.. a.Transactions, .. history.Transactions.Where(t => ids.Add(t.Id))] };
        }).ToList();
        var known = Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        accounts.AddRange(older.Accounts.Where(a => known.Add(a.Id)));
        return new SimpleFinAccountSet([.. Errors.Concat(older.Errors).Distinct(StringComparer.Ordinal)], accounts);
    }
}

public sealed record SimpleFinAccount(
    string Id,
    string Name,
    string Currency,
    long BalanceCents,
    long? AvailableCents,
    DateTime BalanceAt,
    string OrgName,
    string? OrgDomain,
    IReadOnlyList<SimpleFinTransaction> Transactions);

/// <summary><paramref name="AmountCents"/> is signed: negative = money out (deposits are positive in the protocol).</summary>
public sealed record SimpleFinTransaction(
    string Id,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string? Memo,
    bool Pending);
