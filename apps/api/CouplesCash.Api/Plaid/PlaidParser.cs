using System.Globalization;
using System.Text.Json;
using CouplesCash.Api.SimpleFin;

namespace CouplesCash.Api.Plaid;

/// <summary>A Plaid error body: <c>{ error_type, error_code, error_message, display_message }</c>.</summary>
public sealed record PlaidErrorBody(string Type, string Code, string Message, string? DisplayMessage);

/// <summary>One page of <c>/transactions/get</c>: the Item's accounts (with cached balances) and up to <c>count</c> transactions.</summary>
public sealed record PlaidTransactionsPage(IReadOnlyList<JsonElement> Accounts, IReadOnlyList<JsonElement> Transactions, int Total);

/// <summary>
/// Pure, unit-tested: turns Plaid responses into the feed shape the sync already reconciles (<see cref="SimpleFinAccountSet"/>), so
/// Plaid rows go through the same pending lifecycle, matching and plan as SimpleFIN rows. Conventions converted here:
/// <list type="bullet">
/// <item>Plaid amounts are positive for money <b>out</b>; ours are negative for money out.</item>
/// <item>Credit / loan <c>current</c> is the positive amount owed; the SimpleFIN shape carries owed as a negative balance.</item>
/// <item>A pending transaction is dated by <c>authorized_date</c> (else <c>date</c>), never after today; a posted one by <c>date</c>.</item>
/// <item>The description is the bank's own wording (<c>original_description</c>) when Plaid sends it, else <c>name</c>; the payee is
/// <c>merchant_name</c>. A posted transaction's <c>pending_transaction_id</c> names the pending row it replaces.</item>
/// <item><c>personal_finance_category</c> (detailed, else primary) and its <c>confidence_level</c> ride along as the row's provider category.</item>
/// </list>
/// </summary>
public static class PlaidParser
{
    public static PlaidErrorBody? ParseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "error_code") is not { } code)
            {
                return null;
            }

            return new PlaidErrorBody(Str(root, "error_type") ?? "", code, Str(root, "error_message") ?? "", Str(root, "display_message"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PlaidTransactionsPage ParseTransactionsPage(JsonElement root) => new(
        Array(root, "accounts"),
        Array(root, "transactions"),
        root.TryGetProperty("total_transactions", out var total) && total.TryGetInt32(out var n) ? n : 0);

    /// <summary><c>/item/get</c> → <c>status.transactions.last_successful_update</c>: when Plaid last fetched from the bank.</summary>
    public static DateTime? LastSuccessfulUpdate(JsonElement itemGet)
    {
        if (itemGet.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("transactions", out var tx) && tx.ValueKind == JsonValueKind.Object
            && Str(tx, "last_successful_update") is { } value
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return at.UtcDateTime;
        }

        return null;
    }

    /// <summary><c>/item/get</c> → <c>item.institution_name</c> when Plaid includes it.</summary>
    public static string? InstitutionName(JsonElement itemGet) =>
        itemGet.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object ? Str(item, "institution_name") : null;

    /// <summary>
    /// The feed for one Item: every account (balances from Plaid's cache, dated <paramref name="balanceAt"/>) with its transactions.
    /// <paramref name="errors"/> become the link's "bridge errors" (and stop absent pending rows from being judged dropped).
    /// </summary>
    public static SimpleFinAccountSet ToAccountSet(
        IReadOnlyList<JsonElement> accounts,
        IReadOnlyList<JsonElement> transactions,
        string orgName,
        DateTime balanceAt,
        DateOnly today,
        IReadOnlyList<string> errors)
    {
        var byAccount = new Dictionary<string, List<SimpleFinTransaction>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tx in transactions)
        {
            if (ParseTransaction(tx, today) is not { } parsed || Str(tx, "account_id") is not { } accountId || !seen.Add(parsed.Id))
            {
                continue;
            }

            if (!byAccount.TryGetValue(accountId, out var list))
            {
                byAccount[accountId] = list = [];
            }

            list.Add(parsed);
        }

        var result = new List<SimpleFinAccount>();
        foreach (var account in accounts)
        {
            if (Str(account, "account_id") is not { } id)
            {
                continue;
            }

            var type = Str(account, "type") ?? "depository";
            var owed = type is "credit" or "loan";
            account.TryGetProperty("balances", out var balances);
            var current = Cents(balances, "current") ?? 0;
            var available = owed ? null : Cents(balances, "available");
            var currency = Str(balances, "iso_currency_code") ?? Str(balances, "unofficial_currency_code") ?? "USD";
            result.Add(new SimpleFinAccount(
                id,
                DisplayName(account),
                currency,
                owed ? -current : current,
                available,
                balanceAt,
                orgName,
                null,
                byAccount.TryGetValue(id, out var txs) ? txs : []));
        }

        return new SimpleFinAccountSet(errors, result);
    }

    /// <summary>"Plaid Checking ··0000" (name, else official name, plus the mask when Plaid sends one).</summary>
    public static string DisplayName(JsonElement account)
    {
        var name = Str(account, "name") ?? Str(account, "official_name") ?? "Account";
        return Str(account, "mask") is { } mask && !name.Contains(mask, StringComparison.Ordinal) ? $"{name} ··{mask}" : name;
    }

    public static SimpleFinTransaction? ParseTransaction(JsonElement tx, DateOnly today)
    {
        if (Str(tx, "transaction_id") is not { } id || Cents(tx, "amount") is not { } plaidCents)
        {
            return null;
        }

        var pending = tx.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
        var date = Date(tx, "date");
        var dated = pending ? Date(tx, "authorized_date") ?? date : date ?? Date(tx, "authorized_date");
        if (dated is not { } postedOn)
        {
            return null;
        }

        if (pending && postedOn > today)
        {
            postedOn = today;
        }

        var description = Str(tx, "original_description") ?? Str(tx, "name") ?? Str(tx, "merchant_name") ?? "Transaction";
        var payee = Str(tx, "merchant_name");
        tx.TryGetProperty("personal_finance_category", out var category);
        return new SimpleFinTransaction(
            id,
            postedOn,
            -plaidCents,
            description,
            payee is not null && !string.Equals(payee, description, StringComparison.OrdinalIgnoreCase) ? payee : null,
            null,
            pending,
            pending ? null : Str(tx, "pending_transaction_id"),
            Str(category, "detailed") ?? Str(category, "primary"),
            Str(category, "confidence_level"));
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().Select(e => e.Clone()).ToList() : [];

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    /// <summary>A JSON number of dollars → cents (decimal, away from zero), or null.</summary>
    private static long? Cents(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero)
            : null;

    private static DateOnly? Date(JsonElement el, string name) =>
        Str(el, name) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
