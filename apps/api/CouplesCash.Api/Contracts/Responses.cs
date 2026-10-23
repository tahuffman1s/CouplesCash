using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Contracts;

public sealed record UserDto(Guid Id, string Email, string DisplayName, DateTime CreatedAt);

public sealed record MemberDto(Guid Id, string DisplayName, string Email, DateTime JoinedAt);

public sealed record HouseholdDto(Guid Id, string Name, DateTime CreatedAt, IReadOnlyList<MemberDto> Members);

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
    long MonthlyEquivalentCents,
    DateOnly? NextDueDate,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PaidInfo(DateTime PaidAt, Guid PaidBy, long AmountCents);

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
    PaidInfo? Paid);

public sealed record CalendarResponse(DateOnly From, DateOnly To, IReadOnlyList<OccurrenceDto> Occurrences);

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
