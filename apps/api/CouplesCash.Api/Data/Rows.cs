using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Data;

public sealed class UserRow
{
    public Guid Id { get; init; }

    public string Email { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string PasswordHash { get; init; } = "";

    public DateTime CreatedAt { get; init; }
}

public sealed class HouseholdRow
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public DateTime CreatedAt { get; init; }
}

public sealed class MemberRow
{
    public Guid UserId { get; init; }

    public string DisplayName { get; init; } = "";

    public string Email { get; init; } = "";

    public DateTime JoinedAt { get; init; }
}

public sealed class CoupleCodeRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime ExpiresAt { get; init; }
}

public sealed class AccountRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Name { get; init; } = "";

    public string Institution { get; init; } = "";

    public AccountType Type { get; init; }

    public long BalanceCents { get; init; }

    public string Color { get; init; } = "";

    public string Icon { get; init; } = "";

    public string? Note { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public DateTime? ArchivedAt { get; init; }
}

public sealed class SnapshotRow
{
    public long BalanceCents { get; init; }

    public DateTime RecordedAt { get; init; }

    public Guid RecordedBy { get; init; }
}

public sealed class CategoryRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Name { get; init; } = "";

    public string Color { get; init; } = "";

    public string Icon { get; init; } = "";

    public int SortOrder { get; init; }

    public bool IsDefault { get; init; }
}

public sealed class RecurringItemRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Name { get; init; } = "";

    public Kind Kind { get; init; }

    public long AmountCents { get; init; }

    public Guid? CategoryId { get; init; }

    public Guid? AccountId { get; init; }

    public Frequency Frequency { get; init; }

    public DateOnly AnchorDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public bool AutoPay { get; init; }

    public string? Notes { get; init; }

    public string? Url { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public DateTime? ArchivedAt { get; init; }

    // Populated by the joined queries.
    public string? CategoryName { get; init; }

    public string? CategoryColor { get; init; }

    public string? AccountName { get; init; }

    public Recurrence Rule => new(Frequency, AnchorDate, EndDate);
}

public sealed class PaymentRow
{
    public Guid RecurringItemId { get; init; }

    public DateOnly DueDate { get; init; }

    public DateTime PaidAt { get; init; }

    public Guid PaidBy { get; init; }

    public long AmountCents { get; init; }
}
