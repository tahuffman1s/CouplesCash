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
    public static string BudgetSet(string categoryName, long amountCents, string currency, BudgetPeriod period = BudgetPeriod.Monthly) =>
        $"set {Bold(categoryName)} {PeriodName(period)}budget to {Money(amountCents, currency)}";

    public static string BudgetCleared(string categoryName, BudgetPeriod period = BudgetPeriod.Monthly) => $"cleared {Bold(categoryName)} {PeriodName(period)}budget";

    /// <summary>Monthly budgets keep their v2 wording; weekly ones say so.</summary>
    private static string PeriodName(BudgetPeriod period) => period == BudgetPeriod.Weekly ? "weekly " : "";

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

    public static string HouseholdReset() => "reset the household's data and started over";

    public static string CoupleRenamed(string name) => $"renamed the household to {Bold(name)}";

    public static string CoupleSettings(IReadOnlyCollection<string> changedFields) =>
        changedFields.Count == 0 ? "updated household settings" : $"updated household settings ({string.Join(", ", changedFields)})";

    // Categories
    public static string CategoryCreated(CategoryRow c) => $"added category {Bold(c.Name)}";

    public static string CategoryUpdated(CategoryRow c) => $"updated category {Bold(c.Name)}";

    public static string CategoryDeleted(CategoryRow c) => $"removed category {Bold(c.Name)}";

    // Banks / inbox / imports (v3)
    public static string BankLinked(string name) => $"linked bank {Bold(name)}";

    public static string BankUnlinked(string name) => $"unlinked bank {Bold(name)}";

    public static string BankSynced(string name, int newTransactions, int autoMatched) =>
        $"synced {Bold(name)} · {Plural(newTransactions, "new transaction")}{(autoMatched > 0 ? $", {autoMatched} auto-matched" : "")}";

    public static string BankError(string name, string error) => $"could not sync {Bold(name)}: {error}";

    public static string BankMapped(string externalName, string accountName) => $"mapped {Bold(externalName)} to {Bold(accountName)}";

    public static string BankUnmapped(string externalName) => $"stopped tracking {Bold(externalName)}";

    public static string ImportCsv(string filename, int imported, int duplicates) =>
        $"imported {Bold(filename)} · {Plural(imported, "transaction")}{(duplicates > 0 ? $", {Plural(duplicates, "duplicate")} skipped" : "")}";

    public static string TransactionConfirmedExpense(string name, long amountCents, string currency) => $"confirmed {Bold(name)} · {Money(amountCents, currency)}";

    public static string TransactionConfirmedOccurrence(string itemName, long amountCents, string currency) => $"matched {Bold(itemName)} · {Money(amountCents, currency)}";

    public static string TransactionAutomatched(string itemName, long amountCents, string currency) => $"matched {Bold(itemName)} · {Money(amountCents, currency)} (auto)";

    public static string TransactionAutocategorized(string name, string categoryName, long amountCents, string currency) =>
        $"categorized {Bold(name)} · {Money(amountCents, currency)} as {Bold(categoryName)} (auto)";

    public static string BillSuggestionDismissed(string name) => $"dismissed the found bill {Bold(name)}";

    public static string BillSuggestionAccepted(RecurringItemRow i, int matched, string currency) =>
        $"added found {KindName(i.Kind)} {Bold(i.Name)} · {Money(i.AmountCents, currency)} {FrequencyName(i.Frequency)}{(matched > 0 ? $" and matched {Plural(matched, "past payment")}" : "")}";

    public static string BankNameForgotten(RecurringItemRow i, string name) => $"forgot the bank name {Bold(name)} of {Bold(i.Name)}";

    public static string TransactionIgnored(string name, long amountCents, string currency) => $"ignored {Bold(name)} · {Money(amountCents, currency)}";

    public static string TransactionRestored(string name, long amountCents, string currency) => $"restored {Bold(name)} · {Money(amountCents, currency)} to the inbox";

    // Transactions added by hand
    public static string TransactionAdded(string name, long amountCents, string currency) => $"added {Bold(name)} · {Money(amountCents, currency)} (waiting for the bank)";

    public static string TransactionAddedRemoved(string name, long amountCents, string currency) => $"removed {Bold(name)} · {Money(amountCents, currency)}";

    public static string TransactionAddedReplaced(string name, string bankName) => $"{Bold(name)} showed up at the bank as {Bold(bankName)}";

    // v5: pending lifecycle, matching anywhere, transfers
    public static string TransactionPosted(string name, long fromCents, long toCents, string currency) =>
        $"{Bold(name)} posted · {Money(Math.Abs(fromCents), currency)} → {Money(Math.Abs(toCents), currency)}";

    public static string TransactionDropped(string name, long amountCents, string currency) => $"{Bold(name)} · {Money(amountCents, currency)} authorization dropped by the bank";

    public static string TransactionSuperseded(string name, long amountCents, string currency) => $"{Bold(name)} · {Money(amountCents, currency)} replaced by its posted transaction";

    public static string TransactionUnmatched(string name, long amountCents, string currency) => $"unmatched {Bold(name)} · {Money(amountCents, currency)}";

    public static string TransferAuto(int count) => $"ignored {Plural(count, "transfer")} (auto)";

    public static string TransferPaired(string? fromName, string? toName, long amountCents, string currency) =>
        $"marked a transfer {Bold(fromName ?? "Other account")} → {Bold(toName ?? "Other account")} · {Money(amountCents, currency)}";

    public static string TransferOneSided(string name, long amountCents, string currency) => $"marked {Bold(name)} · {Money(amountCents, currency)} as a transfer";

    public static string TransferUnpaired(string name, long amountCents, string currency) => $"marked {Bold(name)} · {Money(amountCents, currency)} as not a transfer";

    public static string RecurringFromTransaction(RecurringItemRow i, string currency) =>
        $"created {KindName(i.Kind)} {Bold(i.Name)} · {Money(i.AmountCents, currency)} {FrequencyName(i.Frequency)} from a bank transaction";

    // v7: the Friday routine
    public static string AccountRoleSet(AccountRow a, AccountRole role, string? ownerName) =>
        $"set {Bold(a.Name)} as {RoleName(role)}{(ownerName is null ? "" : $" ({Bold(ownerName)})")}";

    public static string RoleName(AccountRole role) => role switch
    {
        AccountRole.Bills => "the bills account",
        AccountRole.Savings => "savings",
        AccountRole.Fun => "fun money",
        _ => "another account",
    };

    public static string RecurringBulkCreated(int count) => $"added {Plural(count, "bill")}";

    public static string TransferPlannedPaid(string name, long amountCents, string currency) => $"{Bold(name)} sent · {Money(amountCents, currency)} (matched from the bank)";

    public static string SweepPlanned(long plannedCents, long leftCents, string currency) =>
        plannedCents > 0 ? $"planned to move {Money(plannedCents, currency)} to savings" : $"nothing to move to savings this week ({Money(leftCents, currency)})";

    public static string SweepDone(long cents, string currency) => $"moved {Money(cents, currency)} to savings";

    public static string SweepUndone() => "unticked the savings move";

    public static string SweepAuto(long cents, string currency) => $"{Money(cents, currency)} moved to savings (seen in the bank)";

    public static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    // Users
    public static string UserRenamed(string displayName) => $"changed their name to {Bold(displayName)}";

    public static string KindName(Kind kind) => kind switch
    {
        Kind.Bill => "bill",
        Kind.Subscription => "subscription",
        Kind.Income => "income",
        Kind.Transfer => "transfer",
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
