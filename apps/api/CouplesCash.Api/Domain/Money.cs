using System.Globalization;

namespace CouplesCash.Api.Domain;

/// <summary>
/// Culture-independent money formatting for activity summaries and push bodies: <c>$1,850.00</c>,
/// <c>€12.50</c>, <c>-£3.00</c>, or <c>CHF 1,000.00</c> for currencies without a well-known symbol.
/// The API runs with invariant globalization, so no CultureInfo lookup by currency is attempted.
/// </summary>
public static class Money
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["CAD"] = "CA$",
        ["AUD"] = "A$",
    };

    public static string Format(long cents, string currency)
    {
        var sign = cents < 0 ? "-" : "";
        var magnitude = Math.Abs(cents) / 100m;
        var number = magnitude.ToString("N2", CultureInfo.InvariantCulture);
        var code = (currency ?? "USD").Trim().ToUpperInvariant();
        return Symbols.TryGetValue(code, out var symbol)
            ? $"{sign}{symbol}{number}"
            : $"{sign}{code} {number}";
    }
}
