using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

/// <summary>
/// Ready-to-show English sentences for the activity feed, without the actor ("marked Rent paid · $1,850.00");
/// the web prefixes the actor's name. Plain text, no markdown: entity names are carried verbatim and the
/// ids in <c>data</c> let the UI link them.
/// </summary>
public static class ActivitySummaries
{
    public static string Bold(string name) => name;

    public static string Money(long cents, string currency) => Domain.Money.Format(cents, currency);

    // Accounts
    public static string AccountCreated(AccountRow a, string currency) => $"added account {Bold(a.Name)} · {Money(a.BalanceCents, currency)}";

    public static string AccountUpdated(AccountRow a) => $"updated account {Bold(a.Name)}";

    public static string AccountBalance(AccountRow a, string currency) => $"set {Bold(a.Name)} balance to {Money(a.BalanceCents, currency)}";

    public static string AccountDeleted(AccountRow a) => $"archived account {Bold(a.Name)}";

    // Recurring
    public static string RecurringCreated(RecurringItemRow i, string currency) =>
        $"added {KindName(i.Kind)} {Bold(i.Name)} · {Money(i.AmountCents, currency)} {FrequencyName(i.Frequency)}";

    public static string RecurringUpdated(RecurringItemRow i) => $"updated {KindName(i.Kind)} {Bold(i.Name)}";

    public static string RecurringDeleted(RecurringItemRow i) => $"removed {KindName(i.Kind)} {Bold(i.Name)}";

    // Occurrences
    public static string OccurrencePaid(string name, long amountCents, string currency) => $"marked {Bold(name)} paid · {Money(amountCents, currency)}";

    public static string OccurrenceUnpaid(string name) => $"marked {Bold(name)} unpaid";

    public static string OccurrenceAutopaid(string name, long amountCents, string currency) => $"{Bold(name)} auto-paid · {Money(amountCents, currency)}";

    // Expenses
    public static string ExpenseCreated(ExpenseRow e, string currency) => $"added expense {Bold(e.Name)} · {Money(e.AmountCents, currency)}";

    public static string ExpenseUpdated(ExpenseRow e, string currency) => $"updated expense {Bold(e.Name)} · {Money(e.AmountCents, currency)}";

    public static string ExpenseDeleted(ExpenseRow e, string currency) => $"removed expense {Bold(e.Name)} · {Money(e.AmountCents, currency)}";

    // Budgets
    public static string BudgetSet(string categoryName, long amountCents, string currency) => $"set {Bold(categoryName)} budget to {Money(amountCents, currency)}";

    public static string BudgetCleared(string categoryName) => $"cleared {Bold(categoryName)} budget";

    // To-pay
    public static string TodoCreated(TodoRow t, string currency) => $"added to-pay {Bold(t.Title)}{AmountSuffix(t.AmountCents, currency)}";

    public static string TodoDone(TodoRow t) => $"checked off {Bold(t.Title)}";

    public static string TodoUndone(TodoRow t) => $"unchecked {Bold(t.Title)}";

    public static string TodoDeleted(TodoRow t) => $"removed to-pay {Bold(t.Title)}";

    // Goals
    public static string GoalCreated(GoalRow g, string currency) => $"created goal {Bold(g.Name)} · {Money(g.TargetCents, currency)}";

    public static string GoalUpdated(GoalRow g) => $"updated goal {Bold(g.Name)}";

    public static string GoalContributed(GoalRow g, long amountCents, string currency) =>
        amountCents >= 0
            ? $"added {Money(amountCents, currency)} to {Bold(g.Name)}"
            : $"took {Money(-amountCents, currency)} from {Bold(g.Name)}";

    public static string GoalDeleted(GoalRow g) => $"removed goal {Bold(g.Name)}";

    // Couple
    public static string CoupleJoined() => "joined the household";

    public static string CoupleLeft() => "left the household";

    public static string CoupleRenamed(string name) => $"renamed the household to {Bold(name)}";

    public static string CoupleSettings(IReadOnlyCollection<string> changedFields) =>
        changedFields.Count == 0 ? "updated household settings" : $"updated household settings ({string.Join(", ", changedFields)})";

    // Categories
    public static string CategoryCreated(CategoryRow c) => $"added category {Bold(c.Name)}";

    public static string CategoryUpdated(CategoryRow c) => $"updated category {Bold(c.Name)}";

    public static string CategoryDeleted(CategoryRow c) => $"removed category {Bold(c.Name)}";

    // Users
    public static string UserRenamed(string displayName) => $"changed their name to {Bold(displayName)}";

    public static string KindName(Kind kind) => kind switch
    {
        Kind.Bill => "bill",
        Kind.Subscription => "subscription",
        Kind.Income => "income",
        _ => "item",
    };

    public static string FrequencyName(Frequency frequency) => frequency switch
    {
        Frequency.Weekly => "weekly",
        Frequency.Biweekly => "every two weeks",
        Frequency.Monthly => "monthly",
        Frequency.Quarterly => "quarterly",
        Frequency.Yearly => "yearly",
        Frequency.Once => "once",
        _ => "",
    };

    private static string AmountSuffix(long? cents, string currency) => cents is { } c ? $" · {Money(c, currency)}" : "";
}
