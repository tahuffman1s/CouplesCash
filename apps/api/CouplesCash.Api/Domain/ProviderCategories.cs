namespace CouplesCash.Api.Domain;

/// <summary>A household category suggested from the bank feed's own category (with the confidence it gets and a reason).</summary>
public sealed record ProviderCategorySuggestion(Guid CategoryId, int Confidence, string Reason);

/// <summary>
/// Pure, unit-tested: Plaid's <c>personal_finance_category</c> (detailed codes such as <c>FOOD_AND_DRINK_COFFEE</c>, from its
/// merchant database) → one of the household's categories, by name. Each code prefix lists category names in order of preference;
/// the first one the household has wins (the defaults first, then common names people create: Dining, Shopping, Travel, …), so a
/// code with no matching category suggests nothing. Transfers and credit-card payments never get a category (transfer detection
/// handles them); money in only maps to Income.
/// Confidence: VERY_HIGH 82 (high enough to be applied automatically at auto-match level "all"), HIGH 74, MEDIUM 55; LOW /
/// UNKNOWN suggest nothing. Learned payee rules still come first.
/// </summary>
public static class ProviderCategories
{
    /// <summary>Plaid VERY_HIGH: applied automatically (level all, money out).</summary>
    public const int AutoConfidence = 82;

    private static readonly (string Prefix, string[] Names)[] Map =
    [
        ("TRANSFER_IN", []),
        ("TRANSFER_OUT", []),
        ("LOAN_PAYMENTS_CREDIT_CARD_PAYMENT", []),
        ("LOAN_PAYMENTS", ["Debt", "Loans"]),
        ("BANK_FEES", ["Fees", "Bank Fees"]),
        ("ENTERTAINMENT_TV_AND_MOVIES", ["Subscriptions", "Entertainment", "Fun"]),
        ("ENTERTAINMENT_MUSIC_AND_AUDIO", ["Subscriptions", "Entertainment", "Fun"]),
        ("ENTERTAINMENT", ["Entertainment", "Fun"]),
        ("FOOD_AND_DRINK_GROCERIES", ["Groceries"]),
        ("FOOD_AND_DRINK", ["Dining", "Restaurants", "Eating Out", "Food", "Fun"]),
        ("GENERAL_MERCHANDISE_PET_SUPPLIES", ["Kids & Pets", "Pets"]),
        ("GENERAL_MERCHANDISE", ["Shopping"]),
        ("HOME_IMPROVEMENT", ["Home", "Home Improvement", "Housing"]),
        ("MEDICAL", ["Health", "Medical"]),
        ("PERSONAL_CARE", ["Personal Care", "Health"]),
        ("GENERAL_SERVICES_INSURANCE", ["Insurance"]),
        ("GENERAL_SERVICES_CHILDCARE", ["Kids & Pets", "Childcare", "Kids"]),
        ("GENERAL_SERVICES_EDUCATION", ["Education"]),
        ("GENERAL_SERVICES_AUTOMOTIVE", ["Transportation", "Auto"]),
        ("GENERAL_SERVICES", ["Services"]),
        ("GOVERNMENT_AND_NON_PROFIT_DONATIONS", ["Donations", "Giving", "Charity"]),
        ("GOVERNMENT_AND_NON_PROFIT_TAX_PAYMENT", ["Taxes"]),
        ("GOVERNMENT_AND_NON_PROFIT", []),
        ("TRANSPORTATION", ["Transportation", "Gas", "Auto"]),
        ("TRAVEL", ["Travel", "Fun"]),
        ("RENT_AND_UTILITIES_RENT", ["Housing", "Rent"]),
        ("RENT_AND_UTILITIES", ["Utilities"]),
    ];

    public static int? Confidence(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "VERY_HIGH" => AutoConfidence,
        "HIGH" => 74,
        "MEDIUM" => 55,
        _ => null,
    };

    public static ProviderCategorySuggestion? Suggest(string? code, string? level, bool isOutflow, IReadOnlyDictionary<string, Guid> categoriesByName)
    {
        if (string.IsNullOrWhiteSpace(code) || Confidence(level) is not { } confidence)
        {
            return null;
        }

        var upper = code.Trim().ToUpperInvariant();
        string[] names;
        if (upper.StartsWith("INCOME", StringComparison.Ordinal))
        {
            names = isOutflow ? [] : ["Income"];
        }
        else if (!isOutflow)
        {
            return null; // refunds and reversals are not spending
        }
        else
        {
            names = Map.FirstOrDefault(m => upper.StartsWith(m.Prefix, StringComparison.Ordinal)).Names ?? [];
        }

        foreach (var name in names)
        {
            if (categoriesByName.TryGetValue(name, out var id))
            {
                return new ProviderCategorySuggestion(id, confidence, $"bank category: {Label(upper)} ({level!.Trim().ToLowerInvariant().Replace('_', ' ')})");
            }
        }

        return null;
    }

    /// <summary>"FOOD_AND_DRINK_COFFEE" → "food and drink coffee".</summary>
    public static string Label(string code) => code.Trim().ToLowerInvariant().Replace('_', ' ');
}
