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
    long? WeeklySpendCents);

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
    DateTime UpdatedAt);

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
    DateTime UpdatedAt);

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
    DateTime UpdatedAt);

public sealed record BankLinkDto(
    Guid Id,
    string Provider,
    string Name,
    DateTime CreatedAt,
    DateTime? LastSyncedAt,
    string? LastError,
    bool SyncEnabled,
    IReadOnlyList<string> BridgeErrors,
    IReadOnlyList<ExternalAccountDto> Accounts);

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

public sealed record InboxItemDto(
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
    DateTime CreatedAt);

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
    WeekBudgetDto Budget);
