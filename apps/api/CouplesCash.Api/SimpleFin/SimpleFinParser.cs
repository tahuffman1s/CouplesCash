using System.Globalization;
using System.Text.Json;

namespace CouplesCash.Api.SimpleFin;

/// <summary>
/// Parses the SimpleFIN <c>/accounts</c> JSON (https://www.simplefin.org/protocol.html). Tolerates both the v1 shape the
/// Bridge serves today (<c>errors: string[]</c>, per-account <c>org</c>, <c>payee</c>/<c>memo</c>) and the v2 draft
/// (<c>errlist: {code,msg}[]</c>, top-level <c>connections</c>, <c>transacted_at</c>, <c>posted = 0</c> while pending).
/// Amounts are decimal strings and are converted to cents with banker's rounding; dates are unix seconds.
/// </summary>
public static class SimpleFinParser
{
    public static SimpleFinAccountSet Parse(string json, DateOnly today)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SimpleFinException(SimpleFinError.BadResponse, "The Bridge returned a response that is not JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SimpleFinException(SimpleFinError.BadResponse, "The Bridge returned a response that is not a JSON object.");
            }

            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
            {
                errors.AddRange(errorsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (root.TryGetProperty("errlist", out var errlistEl) && errlistEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errlistEl.EnumerateArray())
                {
                    var msg = item.ValueKind == JsonValueKind.Object ? Str(item, "msg") : item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(msg) && !errors.Contains(msg))
                    {
                        errors.Add(msg);
                    }
                }
            }

            var connections = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("connections", out var connEl) && connEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var conn in connEl.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    var id = Str(conn, "conn_id");
                    var name = Str(conn, "name");
                    if (id is not null && name is not null)
                    {
                        connections[id] = name;
                    }
                }
            }

            var accounts = new List<SimpleFinAccount>();
            if (root.TryGetProperty("accounts", out var accountsEl) && accountsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accountsEl.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
                {
                    accounts.Add(ParseAccount(account, connections, today));
                }
            }

            return new SimpleFinAccountSet(errors, accounts);
        }
    }

    /// <summary>"1234.56" → 123456, "-12.345" → -1234 (half to even), "1,850.00" → 185000. Also accepts JSON numbers.</summary>
    public static long ParseCents(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ParseCents(value.GetString()!),
        JsonValueKind.Number => ToCents(value.GetDecimal()),
        _ => throw new SimpleFinException(SimpleFinError.BadResponse, $"Unexpected amount token {value.ValueKind}."),
    };

    public static long ParseCents(string amount)
    {
        var cleaned = amount.Trim().Replace(",", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal);
        if (!decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new SimpleFinException(SimpleFinError.BadResponse, $"Amount '{amount}' is not a decimal string.");
        }

        return ToCents(parsed);
    }

    public static long ToCents(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.ToEven);

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private static SimpleFinAccount ParseAccount(JsonElement account, IReadOnlyDictionary<string, string> connections, DateOnly today)
    {
        var id = Str(account, "id") ?? throw new SimpleFinException(SimpleFinError.BadResponse, "An account has no id.");
        var name = Str(account, "name") ?? id;
        var currency = Str(account, "currency") is { Length: > 0 } c ? c : "USD";
        var balance = account.TryGetProperty("balance", out var balanceEl) && balanceEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? ParseCents(balanceEl) : 0;
        long? available = account.TryGetProperty("available-balance", out var availEl) && availEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? ParseCents(availEl) : null;
        var balanceAt = Unix(account, "balance-date") is { } ts && ts > 0 ? FromUnix(ts) : DateTime.UtcNow;

        string orgName = "";
        string? orgDomain = null;
        if (account.TryGetProperty("org", out var org) && org.ValueKind == JsonValueKind.Object)
        {
            orgName = Str(org, "name") ?? Str(org, "domain") ?? "";
            orgDomain = Str(org, "domain");
        }
        else if (Str(account, "conn_id") is { } connId && connections.TryGetValue(connId, out var connName))
        {
            orgName = connName;
        }

        var transactions = new List<SimpleFinTransaction>();
        if (account.TryGetProperty("transactions", out var txEl) && txEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tx in txEl.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
            {
                transactions.Add(ParseTransaction(tx, today));
            }
        }

        return new SimpleFinAccount(id, name, currency, balance, available, balanceAt, orgName, orgDomain, transactions);
    }

    private static SimpleFinTransaction ParseTransaction(JsonElement tx, DateOnly today)
    {
        var id = Str(tx, "id") ?? throw new SimpleFinException(SimpleFinError.BadResponse, "A transaction has no id.");
        var amount = tx.TryGetProperty("amount", out var amountEl) ? ParseCents(amountEl) : throw new SimpleFinException(SimpleFinError.BadResponse, $"Transaction {id} has no amount.");
        var pending = tx.TryGetProperty("pending", out var pendingEl) && pendingEl.ValueKind == JsonValueKind.True;

        var posted = Unix(tx, "posted");
        var transactedAt = Unix(tx, "transacted_at");
        DateOnly postedOn;
        if (posted is > 0)
        {
            postedOn = DateOnly.FromDateTime(FromUnix(posted.Value));
        }
        else
        {
            // v2: posted is 0 while the transaction is pending.
            pending = true;
            postedOn = transactedAt is > 0 ? DateOnly.FromDateTime(FromUnix(transactedAt.Value)) : today;
        }

        return new SimpleFinTransaction(
            id,
            postedOn,
            amount,
            Str(tx, "description") ?? "",
            Blank(Str(tx, "payee")),
            Blank(Str(tx, "memo")),
            pending);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) ? el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        } : null;

    private static long? Unix(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)el.GetDouble(),
            JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
