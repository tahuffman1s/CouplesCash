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

    public string Currency { get; init; } = "USD";

    public int WeekStart { get; init; }

    public string Timezone { get; init; } = "UTC";

    public Guid? DefaultAccountId { get; init; }

    public PayPeriod PayPeriod { get; init; } = PayPeriod.Monthly;

    public DateOnly? PayAnchorDate { get; init; }

    /// <summary>Overall weekly spending target (v4); null = none.</summary>
    public long? WeeklySpendCents { get; init; }

    /// <summary>v5: ignore detected transfer pairs and learned transfer payees automatically (default on).</summary>
    public bool AutoIgnoreTransfers { get; init; } = true;
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

    // v5, computed by the account queries.

    /// <summary>The mapped external account's available balance (Bridge <c>available-balance</c>); null when unmapped or not reported.</summary>
    public long? AvailableCents { get; init; }

    /// <summary>Σ |amount| of bank rows on this account that are still pending at the bank (review status pending or confirmed).</summary>
    public long PendingOutCents { get; init; }

    /// <summary>Mapped to an external (bank) account.</summary>
    public bool Linked { get; init; }
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

    /// <summary>"Bank shows it as" (v4): seeds a payee rule so the first matching bank transaction auto-applies.</summary>
    public string? PayeeHint { get; init; }

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

    public Guid? PaidBy { get; init; }

    public long AmountCents { get; init; }

    public bool Auto { get; init; }

    /// <summary>manual | autopay | bank</summary>
    public string Source { get; init; } = PaymentSource.Manual;
}

public static class PaymentSource
{
    public const string Manual = "manual";
    public const string Autopay = "autopay";
    public const string Bank = "bank";
}

// ---------------------------------------------------------------------------------------------
// v2
// ---------------------------------------------------------------------------------------------

public sealed class ActivityEventRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public Guid? ActorId { get; init; }

    public string? ActorName { get; init; }

    public string Kind { get; init; } = "";

    public string EntityType { get; init; } = "";

    public Guid? EntityId { get; init; }

    public string Summary { get; init; } = "";

    public string Data { get; init; } = "{}";

    public DateTime CreatedAt { get; init; }
}

public sealed class ExpenseRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Name { get; init; } = "";

    public long AmountCents { get; init; }

    public Guid? CategoryId { get; init; }

    public Guid? AccountId { get; init; }

    public DateOnly SpentOn { get; init; }

    public string? Note { get; init; }

    public bool Deducted { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public sealed class BudgetRow
{
    public Guid HouseholdId { get; init; }

    public Guid CategoryId { get; init; }

    /// <summary>monthly | weekly (v4).</summary>
    public BudgetPeriod Period { get; init; } = BudgetPeriod.Monthly;

    public long AmountCents { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public sealed class TodoRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Title { get; init; } = "";

    public long? AmountCents { get; init; }

    public DateOnly? DueDate { get; init; }

    public int SortOrder { get; init; }

    public DateTime? DoneAt { get; init; }

    public Guid? DoneBy { get; init; }

    public string? DoneByName { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public sealed class GoalRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Name { get; init; } = "";

    public long TargetCents { get; init; }

    public DateOnly? TargetDate { get; init; }

    public Guid? AccountId { get; init; }

    public long SavedCents { get; init; }

    public string Color { get; init; } = "";

    public string Icon { get; init; } = "";

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public DateTime? ArchivedAt { get; init; }

    // Joined from accounts (null for manual goals).
    public string? AccountName { get; init; }

    public long? AccountBalanceCents { get; init; }

    public bool IsLinked => AccountId is not null;

    public long CurrentCents => IsLinked ? AccountBalanceCents ?? 0 : SavedCents;
}

public sealed class ContributionRow
{
    public Guid Id { get; init; }

    public Guid GoalId { get; init; }

    public long AmountCents { get; init; }

    public Guid ContributedBy { get; init; }

    public DateTime CreatedAt { get; init; }
}

public sealed class AccountSnapshotPointRow
{
    public Guid AccountId { get; init; }

    public long BalanceCents { get; init; }

    public DateTime RecordedAt { get; init; }
}

public sealed class PushSubscriptionRow
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public string Endpoint { get; init; } = "";

    public string P256dh { get; init; } = "";

    public string Auth { get; init; } = "";

    public string? UserAgent { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? LastUsedAt { get; init; }

    public int Failures { get; init; }
}

public sealed class NotificationPrefsRow
{
    public Guid UserId { get; init; }

    public bool RemindersEnabled { get; init; } = true;

    public short[] DaysBefore { get; init; } = [1];

    public int HourLocal { get; init; } = 9;

    public bool ActivityEnabled { get; init; } = true;

    public bool WeekSummaryEnabled { get; init; } = true;

    public DateTime UpdatedAt { get; init; }

    public static NotificationPrefsRow Defaults(Guid userId, DateTime now) => new() { UserId = userId, UpdatedAt = now };
}

/// <summary>A user the scheduler may need to notify: their prefs (defaults when none saved) plus household context.</summary>
public sealed class ReminderUserRow
{
    public Guid UserId { get; init; }

    public Guid HouseholdId { get; init; }

    public string Timezone { get; init; } = "UTC";

    public string Currency { get; init; } = "USD";

    public bool RemindersEnabled { get; init; } = true;

    public short[] DaysBefore { get; init; } = [1];

    public int HourLocal { get; init; } = 9;

    /// <summary>Household week start (0 Sunday / 1 Monday) for the v4 week summaries.</summary>
    public int WeekStart { get; init; }

    public bool WeekSummaryEnabled { get; init; } = true;
}

public sealed class WebAuthnCredentialRow
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public byte[] CredentialId { get; init; } = [];

    public byte[] PublicKey { get; init; } = [];

    public long SignCount { get; init; }

    public Guid? Aaguid { get; init; }

    public string[] Transports { get; init; } = [];

    public string Name { get; init; } = "";

    public bool BackedUp { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? LastUsedAt { get; init; }
}

public sealed class WebAuthnChallengeRow
{
    public Guid Id { get; init; }

    public byte[] Challenge { get; init; } = [];

    public Guid? UserId { get; init; }

    public string Kind { get; init; } = "";

    public string Options { get; init; } = "{}";

    public DateTime ExpiresAt { get; init; }

    public DateTime CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------------------------
// v3
// ---------------------------------------------------------------------------------------------

public sealed class BankLinkRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Provider { get; init; } = "simplefin";

    public string Name { get; init; } = "";

    public byte[] AccessUrlEnc { get; init; } = [];

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? LastSyncedAt { get; init; }

    public DateTime? LastManualSyncAt { get; init; }

    /// <summary>Last sync attempt, successful or not; the scheduler backs off from it.</summary>
    public DateTime? LastAttemptAt { get; init; }

    public string? LastError { get; init; }

    public string[] BridgeErrors { get; init; } = [];

    public bool SyncEnabled { get; init; } = true;
}

public sealed class ExternalAccountRow
{
    public Guid Id { get; init; }

    public Guid BankLinkId { get; init; }

    public Guid HouseholdId { get; init; }

    public string ExternalId { get; init; } = "";

    public string OrgName { get; init; } = "";

    public string Name { get; init; } = "";

    public string Currency { get; init; } = "USD";

    /// <summary>Raw Bridge balance (credit/loan: negative when owed).</summary>
    public long BalanceCents { get; init; }

    public long? AvailableCents { get; init; }

    public DateTime BalanceAt { get; init; }

    public Guid? AccountId { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    // Joined from accounts.
    public string? AccountName { get; init; }
}

public sealed class ImportedTransactionRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public string Source { get; init; } = "";

    public Guid? ExternalAccountId { get; init; }

    public Guid? AccountId { get; init; }

    public string? ExternalId { get; init; }

    public DateOnly PostedOn { get; init; }

    /// <summary>Signed: negative = money out.</summary>
    public long AmountCents { get; init; }

    public string Description { get; init; } = "";

    public string? Payee { get; init; }

    public string? Memo { get; init; }

    public bool Pending { get; init; }

    public string Status { get; init; } = "pending";

    public Guid? SuggestedCategoryId { get; init; }

    public Guid? SuggestedItemId { get; init; }

    public DateOnly? SuggestedDueDate { get; init; }

    public bool SuggestedTransfer { get; init; }

    public int Confidence { get; init; }

    public Guid? ConfirmedExpenseId { get; init; }

    public Guid? ConfirmedItemId { get; init; }

    public DateOnly? ConfirmedDueDate { get; init; }

    public bool ConfirmedAuto { get; init; }

    public Guid? ReviewedBy { get; init; }

    public DateTime? ReviewedAt { get; init; }

    public DateTime CreatedAt { get; init; }

    // Joined.
    public string? AccountName { get; init; }

    public string? SuggestedCategoryName { get; init; }

    public string? SuggestedItemName { get; init; }

    public string? SuggestedItemKind { get; init; }

    public string? ReviewedByName { get; init; }

    public string? ConfirmedCategoryName { get; init; }

    public string? ConfirmedItemName { get; init; }

    // v5

    /// <summary>manual | transfer | dropped | superseded (only when <see cref="Status"/> is ignored).</summary>
    public string? IgnoreReason { get; init; }

    public Guid? TransferPeerId { get; init; }

    public bool TransferAuto { get; init; }

    public DateTime? LastSeenAt { get; init; }

    public string? ConfirmedItemKind { get; init; }

    public Guid? ConfirmedCategoryId { get; init; }

    public Guid? PeerAccountId { get; init; }

    public string? PeerAccountName { get; init; }

    /// <summary>Account transaction lists: bank (an imported row) or manual (a typed expense on the account; id = expense id).</summary>
    public string Kind { get; init; } = "bank";

    public bool IsHidden => IgnoreReason is "dropped" or "superseded";

    public bool IsTransfer => Status == "ignored" && IgnoreReason == "transfer";

    public bool IsPending => Status == "pending";

    public bool IsOutflow => AmountCents < 0;

    public long Magnitude => Math.Abs(AmountCents);
}

public sealed class PayeeRuleRow
{
    public Guid HouseholdId { get; init; }

    public string PayeeNorm { get; init; } = "";

    public Guid? CategoryId { get; init; }

    public Guid? ItemId { get; init; }

    public int Hits { get; init; }

    /// <summary>v5: learned from a one-sided "Mark as transfer".</summary>
    public bool IsTransfer { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public sealed class CsvImportRow
{
    public Guid Id { get; init; }

    public Guid HouseholdId { get; init; }

    public Guid? AccountId { get; init; }

    public string Filename { get; init; } = "";

    public int RowCount { get; init; }

    public int ImportedCount { get; init; }

    public int DuplicateCount { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTime CreatedAt { get; init; }

    public string? CreatedByName { get; init; }
}
