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

    /// <summary>v7: money moved between two of the household's accounts (fun money, savings). Never spending, budgets or income.</summary>
    Transfer,
}

/// <summary>
/// v7 account roles (PLAN-V7 "Accounts have roles"): <c>bills</c> (exactly one; the only account in the weekly math), <c>savings</c>
/// (balance + savings bills, not in the math), <c>fun</c> (personal fun money; its transactions never go to To review), <c>other</c>.
/// </summary>
public enum AccountRole
{
    Bills,
    Savings,
    Fun,
    Other,
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

/// <summary>Which budget a <c>budgets</c> row belongs to (v4). The two periods are independent rows per category.</summary>
public enum BudgetPeriod
{
    Monthly,
    Weekly,
}

/// <summary>Where an occurrence stands today (v4): unpaid and not yet due, paid (any source), or unpaid and past due.</summary>
public enum OccurrenceStatus
{
    Planned,
    Cleared,
    Overdue,
}

/// <summary>The two weekly summary pushes (v4): the evening before the week starts, and on the week's 5th day.</summary>
public enum WeekSummaryKind
{
    Ahead,
    Left,

    /// <summary>v7: the single payday push (replaces ahead / left).</summary>
    Payday,

    /// <summary>v7: the sweep-day push ("move $X to savings today").</summary>
    Sweep,
}

/// <summary>v6 household setting: off = suggestions only; bills = auto-apply bill/income/one-off matches and transfers; all = also auto-categorise expenses.</summary>
public enum AutoMatchLevel
{
    Off,
    Bills,
    All,
}

public static class EnumExtensions
{
    /// <summary>Lower-case name used in the database CHECK constraints and in JSON.</summary>
    public static string ToDb<T>(this T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>Credit cards and loans are stored as positive "amount owed" and count against net worth.</summary>
    public static bool IsLiability(this AccountType type) => type is AccountType.Credit or AccountType.Loan;

    /// <summary>Bills and subscriptions are money going out (spending); income is money coming in. v7 transfers are neither.</summary>
    public static bool IsOutflow(this Kind kind) => kind is Kind.Bill or Kind.Subscription;

    /// <summary>v7: the bank amount of a payment is negative (money leaves the account): bills, subscriptions and transfers (from-account side).</summary>
    public static bool IsMoneyOut(this Kind kind) => kind is Kind.Bill or Kind.Subscription or Kind.Transfer;

    /// <summary>Checking and cash are the money you can actually spend before payday.</summary>
    public static bool IsSpendable(this AccountType type) => type is AccountType.Checking or AccountType.Cash;
}
