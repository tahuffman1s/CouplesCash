namespace CouplesCash.Api.Domain;

public enum AccountType
{
    Checking,
    Savings,
    Credit,
    Cash,
    Investment,
    Loan,
}

public enum Kind
{
    Bill,
    Subscription,
    Income,
}

public enum Frequency
{
    Weekly,
    Biweekly,
    Monthly,
    Quarterly,
    Yearly,
    Once,
}

/// <summary>How often the household gets paid; drives the pay-period view.</summary>
public enum PayPeriod
{
    Weekly,
    Biweekly,
    Bimonthly,
    Monthly,
}

public static class EnumExtensions
{
    /// <summary>Lower-case name used in the database CHECK constraints and in JSON.</summary>
    public static string ToDb<T>(this T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>Credit cards and loans are stored as positive "amount owed" and count against net worth.</summary>
    public static bool IsLiability(this AccountType type) => type is AccountType.Credit or AccountType.Loan;

    /// <summary>Bills and subscriptions are money going out; income is money coming in.</summary>
    public static bool IsOutflow(this Kind kind) => kind is Kind.Bill or Kind.Subscription;

    /// <summary>Checking and cash are the money you can actually spend before payday.</summary>
    public static bool IsSpendable(this AccountType type) => type is AccountType.Checking or AccountType.Cash;
}
