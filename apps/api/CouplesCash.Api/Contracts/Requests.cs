using System.Text.Json;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Contracts;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record UpdateMeRequest(string? DisplayName);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CoupleCodeRequest(string? Name);

public sealed record JoinRequest(string Code);

/// <summary>PATCH /v1/couple: every field optional; nullable settings are cleared by sending <c>null</c>.</summary>
public sealed record HouseholdSettingsRequest(
    Optional<string?> Name,
    Optional<string?> Currency,
    Optional<int?> WeekStart,
    Optional<string?> Timezone,
    Optional<Guid?> DefaultAccountId,
    Optional<PayPeriod?> PayPeriod,
    Optional<DateOnly?> PayAnchorDate,
    Optional<long?> WeeklySpendCents,
    Optional<bool?> AutoIgnoreTransfers);

public sealed record AccountRequest(
    string Name,
    string Institution,
    AccountType? Type,
    long BalanceCents,
    string? Color,
    string? Icon,
    string? Note);

public sealed record BalanceRequest(long BalanceCents);

public sealed record CategoryRequest(string Name, string Color, string Icon, int? SortOrder);

public sealed record RecurringRequest(
    string Name,
    Kind? Kind,
    long AmountCents,
    Guid? CategoryId,
    Guid? AccountId,
    Frequency? Frequency,
    DateOnly? AnchorDate,
    DateOnly? EndDate,
    bool? AutoPay,
    string? Notes,
    string? Url,
    string? PayeeHint);

public sealed record MarkPaidRequest(long? AmountCents);

public sealed record ExpenseRequest(
    string Name,
    long AmountCents,
    Guid? CategoryId,
    Guid? AccountId,
    DateOnly? SpentOn,
    string? Note,
    bool? Deduct);

public sealed record BudgetRequest(long AmountCents);

public sealed record TodoRequest(string Title, long? AmountCents, DateOnly? DueDate, int? SortOrder);

public sealed record TodoOrderRequest(Guid[] Ids);

public sealed record GoalRequest(
    string Name,
    long TargetCents,
    DateOnly? TargetDate,
    Guid? AccountId,
    string? Color,
    string? Icon);

public sealed record ContributeRequest(long AmountCents);

public sealed record PushKeys(string P256dh, string Auth);

public sealed record PushSubscriptionRequest(string Endpoint, PushKeys? Keys, string? UserAgent);

public sealed record PushUnsubscribeRequest(string? Endpoint, Guid? Id);

/// <summary><paramref name="WeekSummaryEnabled"/> (v4) defaults to true when omitted so v2/v3 clients keep working.</summary>
public sealed record PushPrefsRequest(bool RemindersEnabled, int[]? DaysBefore, int HourLocal, bool ActivityEnabled, bool? WeekSummaryEnabled = true);

public sealed record PasskeyOptionsRequest(string Name);

public sealed record PasskeyRegisterRequest(Guid ChallengeId, string Name, JsonElement Credential);

public sealed record PasskeyLoginRequest(Guid ChallengeId, JsonElement Credential);

// ---------------------------------------------------------------------------------------------
// v3
// ---------------------------------------------------------------------------------------------

public sealed record BankClaimRequest(string SetupToken, string? Name);

public sealed record BankLinkPatchRequest(string? Name, bool? SyncEnabled);

public sealed record ExternalAccountCreateSpec(string Name, AccountType? Type, string? Institution);

/// <summary><c>{ accountId }</c> maps (null unmaps) or <c>{ create: {…} }</c> creates the CouplesCash account and maps it.</summary>
public sealed record ExternalAccountMapRequest(Guid? AccountId, ExternalAccountCreateSpec? Create);

public enum ConfirmAs
{
    Expense,
    Occurrence,
}

public sealed record InboxConfirmRequest(ConfirmAs? As, Guid? CategoryId, string? Name, Guid? ItemId, DateOnly? DueDate);

public sealed record InboxBulkRequest(string Action, Guid[]? Ids)
{
    public const string ConfirmSuggested = "confirm-suggested";
    public const string Ignore = "ignore";
}

public sealed record CsvRowRequest(DateOnly? PostedOn, long AmountCents, string Description, string? Payee, string? Memo);

public sealed record CsvImportRequest(Guid? AccountId, string Filename, CsvRowRequest[]? Rows);

// ---------------------------------------------------------------------------------------------
// v5
// ---------------------------------------------------------------------------------------------

/// <summary><c>POST /v1/inbox/{id}/match</c> and the payment link from the occurrence side.</summary>
public sealed record InboxMatchRequest(Guid? ItemId, DateOnly? DueDate);

/// <summary><c>POST /v1/inbox/{id}/transfer</c>: <paramref name="PeerId"/> null = one-sided (the other account is not linked).</summary>
public sealed record InboxTransferRequest(Guid? PeerId);

/// <summary><c>POST /v1/recurring/from-transaction</c>.</summary>
public sealed record FromTransactionRequest(Guid? TransactionId, string? Name, Kind? Kind, Frequency? Frequency);

/// <summary><c>POST /v1/recurring/{id}/occurrences/{date}/link</c>.</summary>
public sealed record LinkTransactionRequest(Guid? TransactionId);
