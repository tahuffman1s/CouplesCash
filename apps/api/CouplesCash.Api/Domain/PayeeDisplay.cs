using System.Text.RegularExpressions;

namespace CouplesCash.Api.Domain;

/// <summary>
/// Pure, unit-tested: a bank's card wording turned into a name people read ("Debit Purchase -visa Chick-fil-a #034columbia Mo 10/24 Card
/// 8023" → "Chick-fil-a"). Drops the processor prefix, everything from the purchase date on, card / phone / store numbers, the state code
/// and the city (also when the bank glued it onto the merchant), a payment processor's tag ("Sq *", "Tst*") and a brand's order detail
/// ("Instacart*aldi …" → "Instacart"). Cities come from <see cref="LearnCities"/>: the word the bank writes before the state on its rows.
/// All-caps wording is title-cased. Never returns empty: when nothing readable is left the original is kept.
/// </summary>
public static partial class PayeeDisplay
{
    private static readonly string[] Prefixes =
    [
        "debit purchase -visa", "debit purchase - visa", "debit purchase visa", "debit purchase", "debit card purchase", "pos purchase",
        "pos debit", "checkcard", "check card", "purchase authorized on", "purchase", "web authorized pmt", "recurring debit purchase",
        "recurring payment", "electronic withdrawal", "ach debit", "ach withdrawal", "visa", "pos",
    ];

    /// <summary>Tags of payment processors in front of the merchant ("SQ *BLUE BOTTLE" is Blue Bottle).</summary>
    private static readonly HashSet<string> Processors = new(StringComparer.OrdinalIgnoreCase)
    {
        "sq", "tst", "sp", "pp", "paypal", "py", "spo", "in", "dd", "ckc", "toast", "clover", "pmt", "fs", "sqc",
    };

    private static readonly HashSet<string> States = new(StringComparer.OrdinalIgnoreCase)
    {
        "al", "ak", "az", "ar", "ca", "co", "ct", "de", "dc", "fl", "ga", "hi", "id", "il", "in", "ia", "ks", "ky", "la", "me", "md", "ma",
        "mi", "mn", "ms", "mo", "mt", "ne", "nv", "nh", "nj", "nm", "ny", "nc", "nd", "oh", "ok", "or", "pa", "ri", "sc", "sd", "tn", "tx",
        "ut", "vt", "va", "wa", "wv", "wi", "wy",
    };

    /// <summary>Cities named on <paramref name="descriptions"/>: the word right before a state code ("Break Time 3160 Columbia Mo 10/23").</summary>
    public static IReadOnlyList<string> LearnCities(IEnumerable<string?> descriptions)
    {
        var cities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var description in descriptions)
        {
            var tokens = Tokens(StripPrefix(description ?? ""));
            for (var i = 1; i < tokens.Count; i++)
            {
                if (States.Contains(tokens[i]) && OnlyNoiseAfter(tokens, i + 1) && tokens[i - 1].Length >= 4 && tokens[i - 1].All(char.IsLetter)
                    && i - 1 > 0)
                {
                    cities.Add(tokens[i - 1]);
                }
            }
        }

        // a merchant the bank glued a city onto ("Discountcolumbia") is not a city itself
        return cities.Where(c => !cities.Any(d => d.Length < c.Length && c.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.Length)
            .ToList();
    }

    public static string Clean(string? description, IReadOnlyCollection<string>? cities = null)
    {
        var original = (description ?? "").Trim();
        if (original.Length == 0)
        {
            return original;
        }

        var text = StripPrefix(original);
        text = Date().Split(text)[0];                 // everything from the purchase date on ("10/24 Card 8023")
        text = CardNumber().Replace(text, " ");
        text = Phone().Replace(text, " ");          // "800-672-4399fl" (state glued on)

        var tokens = Tokens(text);

        // "SQ *THE CROSSING" → The Crossing; "INSTACART*ALDI INSTACART.CO CA" → Instacart
        var star = tokens.FindIndex(t => t.Contains('*'));
        if (star >= 0)
        {
            var parts = tokens[star].Split('*', 2);
            var brand = parts[0].Trim();
            var rest = Capitalize(parts[1].Trim());
            var before = star;
            if (brand.Length == 0 && star > 0 && Processors.Contains(tokens[star - 1]))
            {
                before = star - 1; // "Sq *the …"
            }

            if (Processors.Contains(brand) || brand.Length == 0)
            {
                tokens = [.. tokens.Take(before), .. rest.Length > 0 ? new[] { rest } : [], .. tokens.Skip(star + 1)];
            }
            else
            {
                tokens = [.. tokens.Take(star), brand];
            }
        }

        // the state code and what follows it
        for (var i = tokens.Count - 1; i >= 1; i--)
        {
            if (States.Contains(tokens[i]) && OnlyNoiseAfter(tokens, i + 1))
            {
                tokens = tokens.Take(i).ToList();
                break;
            }
        }

        // the city: its own word(s) at the end, or glued onto the last word ("#034columbia", "Discountcolumbia")
        foreach (var city in cities ?? [])
        {
            while (tokens.Count > 1 && tokens[^1].Equals(city, StringComparison.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }

            if (tokens.Count > 0 && tokens[^1].Length > city.Length && tokens[^1].EndsWith(city, StringComparison.OrdinalIgnoreCase))
            {
                tokens[^1] = tokens[^1][..^city.Length];
            }

            // a city cut short by the bank ("Andy's - Columbicolumbia" → "Andy's - Columbi")
            if (tokens.Count > 1 && tokens[^1].Length >= 3 && city.StartsWith(tokens[^1], StringComparison.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
        }

        // store numbers and loose punctuation at the end ("#034", "3160", "-")
        while (tokens.Count > 1 && (StoreNumber().IsMatch(tokens[^1]) || tokens[^1].All(c => !char.IsLetterOrDigit(c))))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var name = string.Join(' ', tokens).Trim(' ', '-', '*', ',', '.');
        name = Possessive().Replace(name, "$1's");  // "Trader Joe S" → "Trader Joe's"
        if (name.Count(char.IsLetter) < 2)
        {
            return original;
        }

        // all caps: each word capitalised once ("NETFLIX.COM" → "Netflix.com", "CHICK-FIL-A" → "Chick-fil-a")
        return name.Any(char.IsLower) ? name : string.Join(' ', name.ToLowerInvariant().Split(' ').Select(Capitalize));
    }

    private static string StripPrefix(string text)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in Prefixes)
            {
                if (text.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) || text.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..].TrimStart(' ', '-');
                    changed = true;
                }
            }
        }

        return text;
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static List<string> Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Only digits, dates and card words after <paramref name="from"/> (a description cut short: "Columbia Mo 1").</summary>
    private static bool OnlyNoiseAfter(List<string> tokens, int from) =>
        tokens.Skip(from).All(t => t.All(c => char.IsDigit(c) || c == '/' || c == '#') || t.Equals("card", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s\d{1,2}/\d{1,2}(/\d{2,4})?\b.*$")]
    private static partial Regex Date();

    [GeneratedRegex(@"\bcard\s*#?\s*\d+\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex CardNumber();

    [GeneratedRegex(@"\d{3}[-.]\d{3}[-.]\d{4}[a-z]{0,2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^#?\d+$")]
    private static partial Regex StoreNumber();

    [GeneratedRegex(@"\b([A-Za-z]{2,}) S\b")]
    private static partial Regex Possessive();
}
