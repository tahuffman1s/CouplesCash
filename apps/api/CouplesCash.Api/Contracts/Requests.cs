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
    Optional<DateOnly?> PayAnchorDate);

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
    string? Url);

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

public sealed record PushPrefsRequest(bool RemindersEnabled, int[]? DaysBefore, int HourLocal, bool ActivityEnabled);

public sealed record PasskeyOptionsRequest(string Name);

public sealed record PasskeyRegisterRequest(Guid ChallengeId, string Name, JsonElement Credential);

public sealed record PasskeyLoginRequest(Guid ChallengeId, JsonElement Credential);
