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

public sealed record RenameHouseholdRequest(string Name);

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
