using System.Text.Json;
using System.Text.Json.Serialization;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Contracts;

public sealed record UserDto(Guid Id, string Email, string DisplayName, DateTime CreatedAt);

public sealed record MemberDto(Guid Id, string DisplayName, string Email, DateTime JoinedAt);

public sealed record HouseholdDto(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    IReadOnlyList<MemberDto> Members,
    string Currency,
    int WeekStart,
    string Timezone,
    Guid? DefaultAccountId,
    PayPeriod PayPeriod,
    DateOnly? PayAnchorDate,
    long? WeeklySpendCents,
    bool AutoIgnoreTransfers,
    AutoMatchLevel AutoMatchLevel,
    int PaydayWeekday);

public sealed record AuthResult(
    UserDto User,
    HouseholdDto? Household,
    string AccessToken,
    int AccessTokenExpiresIn,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

public sealed record MeResponse(UserDto User, HouseholdDto? Household);

public sealed record CoupleCodeResponse(string Code, string Payload, DateTime ExpiresAt, HouseholdDto Household);

public sealed record AccountDto(
    Guid Id,
    string Name,
    string Institution,
    AccountType Type,
    long BalanceCents,
    string Color,
    string Icon,
    string? Note,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long? AvailableCents,
    long PendingOutCents,
    bool Linked,
    AccountRole Role,
    Guid? OwnerUserId,
    string? OwnerName);

public sealed record BalanceSnapshotDto(long BalanceCents, DateTime RecordedAt, Guid RecordedBy);

public sealed record CategoryDto(Guid Id, string Name, string Color, string Icon, int SortOrder, bool IsDefault);

public sealed record RecurringItemDto(
    Guid Id,
    string Name,
    Kind Kind,
    long AmountCents,
    Guid? CategoryId,
    Guid? AccountId,
    Frequency Frequency,
    DateOnly AnchorDate,
    DateOnly? EndDate,
    bool AutoPay,
    string? Notes,
    string? Url,
    string? PayeeHint,
    long MonthlyEquivalentCents,
    DateOnly? NextDueDate,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> BankNames,
    IReadOnlyList<string> BankNamesLearned,
    bool AmountVaries,
    Guid? FromAccountId,
    Guid? ToAccountId,
    string? FromAccountName,
    string? ToAccountName);

/// <summary><paramref name="Source"/>: manual (user tap) | autopay (scheduler) | bank (inbox confirm or auto-match). <paramref name="Auto"/> stays true for autopay and auto-match.</summary>
public sealed record PaidInfo(DateTime PaidAt, Guid? PaidBy, long AmountCents, bool Auto, string Source);

public sealed record OccurrenceDto(
    Guid ItemId,
    string Name,
    Kind Kind,
    long AmountCents,
    DateOnly DueDate,
    bool AutoPay,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    Guid? AccountId,
    string? AccountName,
    PaidInfo? Paid,
    long? VarianceCents,
    OccurrenceStatus Status);

public sealed record CalendarResponse(DateOnly From, DateOnly To, IReadOnlyList<OccurrenceDto> Occurrences, IReadOnlyList<ExpenseDto> Expenses);

public sealed record CategoryBreakdown(Guid? CategoryId, string Name, string Color, long AmountCents, int Count);

public sealed record KindBreakdown(Kind Kind, long AmountCents, int Count);

public sealed record SummaryDto(
    string Month,
    long NetWorthCents,
    long AssetsCents,
    long LiabilitiesCents,
    long MonthlyBillsCents,
    long MonthlySubscriptionsCents,
    long MonthlyIncomeCents,
    long DueThisMonthCents,
    long PaidThisMonthCents,
    long RemainingThisMonthCents,
    IReadOnlyList<CategoryBreakdown> ByCategory,
    IReadOnlyList<KindBreakdown> ByKind,
    IReadOnlyList<OccurrenceDto> Upcoming,
    IReadOnlyList<AccountDto> Accounts);

public sealed record HealthResponse(string Status, string Db);

// ---------------------------------------------------------------------------------------------
// v2
// ---------------------------------------------------------------------------------------------

public sealed record ActivityEventDto(
    Guid Id,
    string Kind,
    string Summary,
    Guid? ActorId,
    string? ActorName,
    string EntityType,
    Guid? EntityId,
    JsonElement Data,
    DateTime CreatedAt);

public sealed record ActivityPage(IReadOnlyList<ActivityEventDto> Events, DateTime? NextBefore);

public sealed record ExpenseDto(
    Guid Id,
    string Name,
    long AmountCents,
    Guid? CategoryId,
    Guid? AccountId,
    DateOnly SpentOn,
    string? Note,
    bool Deducted,
    Guid CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A monthly budget row (v2). <c>GET /v1/budgets?month=</c> and <c>PUT ?period=monthly</c>.</summary>
public sealed record BudgetRowDto(
    Guid CategoryId,
    string Name,
    string Color,
    string Icon,
    long? BudgetCents,
    long RecurringCents,
    long ExpensesCents,
    long SpentCents,
    long? RemainingCents,
    int? Percent);

public sealed record BudgetsResponse(string Month, IReadOnlyList<BudgetRowDto> Rows, long TotalBudgetCents, long TotalSpentCents);

public sealed record PayPeriodViewDto(
    PayPeriod Period,
    DateOnly Start,
    DateOnly End,
    DateOnly NextPayday,
    int DaysLeft,
    long ExpectedIncomeCents,
    long DueCents,
    long PaidCents,
    long RemainingCents,
    long SpendableCents,
    IReadOnlyList<OccurrenceDto> Occurrences,
    long ExpensesCents);

public sealed record TodoDto(
    Guid Id,
    string Title,
    long? AmountCents,
    DateOnly? DueDate,
    int SortOrder,
    DateTime? DoneAt,
    Guid? DoneBy,
    string? DoneByName,
    Guid CreatedBy,
    DateTime CreatedAt);

public sealed record GoalHistoryPoint(DateTime At, long Value);

public sealed record GoalDto(
    Guid Id,
    string Name,
    long TargetCents,
    DateOnly? TargetDate,
    Guid? AccountId,
    string? AccountName,
    string Color,
    string Icon,
    long CurrentCents,
    int Percent,
    long? MonthlyPaceCents,
    DateOnly? ProjectedDate,
    long? MonthlyNeededCents,
    IReadOnlyList<GoalHistoryPoint> History,
    DateTime CreatedAt);

public sealed record PushSubscriptionDto(Guid Id, string UserAgent, DateTime CreatedAt, DateTime? LastUsedAt);

public sealed record PushPrefsDto(
    bool RemindersEnabled,
    IReadOnlyList<int> DaysBefore,
    int HourLocal,
    bool ActivityEnabled,
    bool WeekSummaryEnabled,
    IReadOnlyList<PushSubscriptionDto> Subscriptions);

public sealed record VapidPublicKeyResponse(string PublicKey);

public sealed record PushSubscriptionCreated(Guid Id);

public sealed record PasskeyDto(Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt, bool BackedUp);

public sealed record PasskeyOptionsResponse(Guid ChallengeId, JsonElement Options);

public sealed record PasskeyConfigResponse(bool Enabled, string? RpId);

public sealed record MonthReportDto(
    string Month,
    SummaryDto Summary,
    long? PreviousNetWorthCents,
    long? NetWorthDeltaCents,
    long ExpensesTotalCents,
    IReadOnlyList<ExpenseDto> TopExpenses,
    IReadOnlyList<BudgetRowDto> Budgets,
    IReadOnlyList<OccurrenceDto> AutoPaid,
    int PaidCount,
    int UnpaidCount,
    DateTime GeneratedAt);

// ---------------------------------------------------------------------------------------------
// v3
// ---------------------------------------------------------------------------------------------

public sealed record ExternalAccountDto(
    Guid Id,
    string ExternalId,
    string OrgName,
    string Name,
    string Currency,
    long BalanceCents,
    long? AvailableCents,
    DateTime BalanceAt,
    Guid? AccountId,
    string? AccountName,
    DateTime UpdatedAt,
    FeedStatsDto? LastFeed);

/// <summary>v7 feed diagnostics: what the last Bridge response contained for one external account.</summary>
public sealed record FeedStatsDto(int Pending, int Posted, bool AvailableReported, DateTime At);

public sealed record BankLinkDto(
    Guid Id,
    string Provider,
    string Name,
    DateTime CreatedAt,
    DateTime? LastSyncedAt,
    string? LastError,
    bool SyncEnabled,
    IReadOnlyList<string> BridgeErrors,
    IReadOnlyList<ExternalAccountDto> Accounts,
    bool NeedsReconnect);

/// <summary>v7 Plaid: whether this server has Plaid keys (and bank sync's DATA_KEY), and which Plaid environment it talks to.</summary>
public sealed record PlaidStatusDto(bool Configured, string Environment, bool OAuthRedirect);

public sealed record PlaidLinkTokenDto(string LinkToken, DateTime Expiration);

public sealed record SyncResultDto(Guid LinkId, int NewTransactions, int UpdatedAccounts, int AutoMatched, IReadOnlyList<string> Errors, DateTime SyncedAt);

/// <summary>Discriminated on <c>kind</c>: category | occurrence | transfer (null = no suggestion).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CategorySuggestionDto), "category")]
[JsonDerivedType(typeof(OccurrenceSuggestionDto), "occurrence")]
[JsonDerivedType(typeof(TransferSuggestionDto), "transfer")]
public abstract record SuggestionDto(int Confidence);

public sealed record CategorySuggestionDto(Guid CategoryId, string CategoryName, int Confidence) : SuggestionDto(Confidence);

public sealed record OccurrenceSuggestionDto(Guid ItemId, string ItemName, Kind ItemKind, DateOnly DueDate, int Confidence) : SuggestionDto(Confidence);

public sealed record TransferSuggestionDto(int Confidence) : SuggestionDto(Confidence);

public sealed record InboxConfirmedDto(Guid? ExpenseId, Guid? ItemId, DateOnly? DueDate, Guid? By, string? ByName, DateTime? At, bool Auto, string? CategoryName, string? ItemName);

/// <summary>v5: set when the row is ignored as a transfer. <paramref name="PeerId"/> null = one-sided.</summary>
public sealed record InboxTransferDto(Guid? PeerId, Guid? PeerAccountId, string? PeerAccountName, bool Auto);

/// <summary>v5: set when the row is confirmed as a bill/income payment.</summary>
public sealed record InboxMatchDto(Guid ItemId, string ItemName, Kind ItemKind, DateOnly DueDate);

/// <summary>v5: set when the row is confirmed as an expense (or is a manual expense in an account list).</summary>
public sealed record InboxExpenseDto(Guid ExpenseId, Guid? CategoryId, string? CategoryName);

public record InboxItemDto(
    Guid Id,
    string Source,
    Guid? AccountId,
    string? AccountName,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string? Memo,
    bool Pending,
    string Status,
    SuggestionDto? Suggestion,
    InboxConfirmedDto? Confirmed,
    DateTime CreatedAt,
    string? IgnoreReason,
    InboxTransferDto? Transfer,
    InboxMatchDto? Match,
    InboxExpenseDto? Expense,
    int? MatchScore,
    MatchDetailDto? MatchDetail);

/// <summary>v6: how the stored suggestion (or automatic match) was reached. <paramref name="Engine"/>: fuzzy | builtin.</summary>
public sealed record MatchDetailDto(int Name, int Amount, int Date, string Reason, string Engine);

public sealed record InboxPage(IReadOnlyList<InboxItemDto> Items, string? NextBefore);

public sealed record InboxSummaryDto(int Pending, int Suggested, DateTime? LastSyncedAt, int LinkCount);

public sealed record BulkResultDto(int Confirmed, int Ignored, int Skipped);

public sealed record CsvImportResultDto(Guid Id, int RowCount, int Imported, int Duplicates, int Skipped, int InboxPending);

public sealed record CsvImportHistoryDto(Guid Id, string Filename, int RowCount, int Imported, int Duplicates, DateTime CreatedAt, string CreatedByName);

// ---------------------------------------------------------------------------------------------
// v4
// ---------------------------------------------------------------------------------------------

/// <summary>One day of the week view: planned occurrences (any kind), unplanned expenses, and their sums (planned amounts).</summary>
public sealed record WeekDayDto(
    DateOnly Date,
    IReadOnlyList<OccurrenceDto> Planned,
    IReadOnlyList<ExpenseDto> Expenses,
    long PlannedOutCents,
    long PlannedInCents,
    long UnplannedCents);

/// <summary>A weekly budget row. <paramref name="CategoryId"/> null = the overall target. <paramref name="VarianceCents"/> = spent − budget (+ = over).</summary>
public sealed record WeekBudgetRowDto(
    Guid? CategoryId,
    string Name,
    string Color,
    string Icon,
    long BudgetCents,
    long SpentCents,
    long VarianceCents,
    int Percent);

public sealed record WeekBudgetHistoryPoint(DateOnly WeekStart, long BudgetCents, long SpentCents, long VarianceCents);

/// <summary><c>WeekView.budget</c> and <c>GET /v1/budgets/week</c>. Totals cover the category rows only; history is oldest first, viewed week last.</summary>
public sealed record WeekBudgetDto(
    WeekBudgetRowDto? Overall,
    IReadOnlyList<WeekBudgetRowDto> Categories,
    long TotalBudgetCents,
    long TotalSpentCents,
    IReadOnlyList<WeekBudgetHistoryPoint> History);

public sealed record WeekViewDto(
    DateOnly Start,
    DateOnly End,
    int WeekStart,
    bool IsCurrent,
    bool IsPast,
    DateOnly Today,
    long SpendableNowCents,
    long? StartingBalanceCents,
    long IncomeExpectedCents,
    long IncomeReceivedCents,
    long PlannedOutCents,
    long PaidOutCents,
    long RemainingOutCents,
    long UnplannedCents,
    long? LeftAtEndCents,
    bool Projected,
    IReadOnlyList<WeekDayDto> Days,
    IReadOnlyList<OccurrenceDto> Overdue,
    WeekBudgetDto Budget,
    long PendingOutCents);

// ---------------------------------------------------------------------------------------------
// v5
// ---------------------------------------------------------------------------------------------

/// <summary><c>GET /v1/accounts/{id}/transactions</c> row: an InboxItem plus <paramref name="Kind"/> (bank | manual; manual id = expense id, source 'manual').</summary>
public sealed record AccountTransactionDto(
    Guid Id,
    string Source,
    Guid? AccountId,
    string? AccountName,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string? Memo,
    bool Pending,
    string Status,
    SuggestionDto? Suggestion,
    InboxConfirmedDto? Confirmed,
    DateTime CreatedAt,
    string? IgnoreReason,
    InboxTransferDto? Transfer,
    InboxMatchDto? Match,
    InboxExpenseDto? Expense,
    int? MatchScore,
    MatchDetailDto? MatchDetail,
    string Kind)
    : InboxItemDto(Id, Source, AccountId, AccountName, PostedOn, AmountCents, Description, Payee, Memo, Pending, Status, Suggestion, Confirmed, CreatedAt, IgnoreReason, Transfer, Match, Expense, MatchScore, MatchDetail);

public sealed record AccountTransactionTotalsDto(long InCents, long OutCents, long PendingOutCents, int Count);

public sealed record AccountTransactionsPage(IReadOnlyList<AccountTransactionDto> Items, string? NextBefore, AccountTransactionTotalsDto Totals);

public sealed record MatchCandidateDto(Guid ItemId, string ItemName, Kind Kind, DateOnly DueDate, long AmountCents, PaidInfo? Paid, bool PaidByThis, int Score);

public sealed record CandidatesResponse(IReadOnlyList<MatchCandidateDto> Occurrences, IReadOnlyList<InboxItemDto> TransferPeers);

public sealed record FromTransactionResponse(RecurringItemDto Item, InboxItemDto Transaction);

public sealed record BillPaymentTransactionDto(Guid Id, DateOnly PostedOn, long AmountCents, string Description, string? AccountName);

public sealed record BillPaymentDto(DateOnly DueDate, long PlannedCents, PaidInfo? Paid, long? VarianceCents, BillPaymentTransactionDto? Transaction, bool Earlier = false);

public sealed record TransferSideDto(Guid? AccountId, string Name, Guid TransactionId);

public sealed record TransferPairDto(Guid Id, DateOnly Date, long AmountCents, TransferSideDto From, TransferSideDto To, bool Auto);

public sealed record TransferFlowDto(Guid? FromAccountId, string FromName, Guid? ToAccountId, string ToName, long TotalCents, int Count);

public sealed record TransferNetDto(Guid? AccountId, string Name, long InCents, long OutCents, long NetCents);

public sealed record TransferReportDto(
    string Month,
    long TotalCents,
    IReadOnlyList<TransferFlowDto> Flows,
    IReadOnlyList<TransferNetDto> NetByAccount,
    IReadOnlyList<TransferPairDto> Pairs,
    IReadOnlyList<InboxItemDto> OneSided);

// ---------------------------------------------------------------------------------------------
// v6
// ---------------------------------------------------------------------------------------------

/// <summary>
/// <c>GET /v1/matcher/status</c>. <paramref name="Engine"/> is what the next run uses (fuzzy when the matcher is configured and reachable,
/// else builtin). <paramref name="Configured"/>, <paramref name="FoundBills"/> and <paramref name="Note"/> are additions to the contract shape.
/// </summary>
public sealed record MatcherStatusDto(
    string Engine,
    bool Reachable,
    string? Version,
    DateTime? LastRunAt,
    int LastAutoMatched,
    bool Configured,
    string? LastRunEngine,
    bool FoundBills,
    string? Note);

public sealed record RematchQueuedDto(bool Queued);

/// <summary>Pending transactions of one account. <paramref name="AccountId"/> is null for a bank account that is not mapped yet (or "Other").</summary>
public sealed record PendingGroupDto(
    Guid? AccountId,
    string AccountName,
    string Institution,
    long BalanceCents,
    long? AvailableCents,
    int PendingCount,
    long PendingOutCents,
    long PendingInCents,
    IReadOnlyList<InboxItemDto> Items);

public sealed record PendingViewDto(long TotalOutCents, long TotalInCents, int Count, IReadOnlyList<PendingGroupDto> Groups);

/// <summary><c>GET /v1/transactions</c>: <paramref name="Pending"/> complete on the first page (empty on later pages), <paramref name="Items"/> posted rows + manual expenses newest first.</summary>
public sealed record TransactionFeedDto(
    IReadOnlyList<AccountTransactionDto> Pending,
    IReadOnlyList<AccountTransactionDto> Items,
    string? NextBefore,
    int ToReviewCount);

public sealed record BillSuggestionDto(
    Guid Id,
    string Name,
    Kind Kind,
    long AmountCents,
    bool AmountVaries,
    Frequency Frequency,
    DateOnly NextDue,
    string BankName,
    int Score,
    int SampleCount,
    DateOnly LastSeen);

public sealed record BillSuggestionAcceptedDto(RecurringItemDto Item, int Matched);

// ---------------------------------------------------------------------------------------------
// v7
// ---------------------------------------------------------------------------------------------

/// <summary>One line of the Friday plan. <paramref name="Kind"/>: bill | subscription | income | transfer | oneoff.</summary>
public sealed record PlanLineDto(string Kind, OccurrenceDto Occurrence);

/// <summary>A ledger line: a bills-account transaction, or (<paramref name="ItemId"/> set) the bill / transfer / income it paid.</summary>
public sealed record PlanLedgerEntryDto(Guid TransactionId, string Kind, string Name, long AmountCents, bool Pending, bool Added, Guid? ItemId, DateOnly? DueDate);

public sealed record PlanLedgerDayDto(DateOnly Date, IReadOnlyList<PlanLedgerEntryDto> Entries);

/// <summary>
/// v7 the current pay week as a ledger: start of the week, each day through today, available now, what is still to come (plan lines by
/// due date), and the projected amount in savings (null without a savings account).
/// </summary>
public sealed record PlanLedgerDto(long StartCents, IReadOnlyList<PlanLedgerDayDto> Days, long AvailableCents, IReadOnlyList<PlanLineDto> Upcoming, long? ProjectedSavingsBalanceCents);

/// <summary>A payday checklist item. <paramref name="Key"/>: paycheck | fun | sweep.</summary>
public sealed record ChecklistItemDto(string Key, string Label, long AmountCents, bool Done, bool Auto, Guid? TransactionId, Guid? ItemId, DateOnly? DueDate);

/// <summary>
/// The savings sweep of a pay week. <paramref name="LeftCents"/> (addition) is the raw left-at-end it was frozen from (negative = nothing to
/// move); <paramref name="TransactionId"/> (addition) is the bank row that moved it.
/// </summary>
public sealed record SweepDto(DateOnly WeekStart, long PlannedCents, long? ActualCents, DateTime? DoneAt, bool Auto, long LeftCents, Guid? TransactionId);

public sealed record PlanBillsAccountDto(
    Guid Id,
    string Name,
    long AvailableCents,
    long BalanceCents,
    bool AvailableFromBank,
    IReadOnlyList<InboxItemDto> Pending,
    long PendingOutCents,
    long AddedCents,
    FeedStatsDto? Feed,
    DateTime? BalanceAt);

public sealed record PlanAccountDto(Guid Id, string Name, AccountRole Role, string? OwnerName, long BalanceCents, long? AvailableCents);

public sealed record PlanSetupDto(bool BanksLinked, bool RolesSet, bool PaycheckKnown, bool BillsAdded, bool PartnerJoined);

public sealed record PlanDto(
    DateOnly Start,
    DateOnly End,
    DateOnly NextPayday,
    bool IsCurrent,
    bool IsPayday,
    DateOnly Today,
    PlanBillsAccountDto? BillsAccount,
    IReadOnlyList<PlanAccountDto> Accounts,
    IReadOnlyList<PlanLineDto> Bills,
    IReadOnlyList<PlanLineDto> FunMoney,
    IReadOnlyList<PlanLineDto> OneOffs,
    IReadOnlyList<PlanLineDto> Income,
    long BillsOutCents,
    long FunOutCents,
    long OneOffOutCents,
    long IncomeInCents,
    long LeftAtEndCents,
    IReadOnlyList<PlanLineDto> SavingsBills,
    IReadOnlyList<ChecklistItemDto> Checklist,
    SweepDto? Sweep,
    int ToReviewCount,
    PlanSetupDto Setup,
    long PaidThisWeekCents = 0,
    PlanLedgerDto? Ledger = null);

public sealed record RecurringBulkResponse(IReadOnlyList<RecurringItemDto> Items);
