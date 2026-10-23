using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

public static class Mapping
{
    public static UserDto ToDto(this UserRow u) => new(u.Id, u.Email, u.DisplayName, u.CreatedAt);

    public static AccountDto ToDto(this AccountRow a) =>
        new(a.Id, a.Name, a.Institution, a.Type, a.BalanceCents, a.Color, a.Icon, a.Note, a.CreatedAt, a.UpdatedAt);

    public static BalanceSnapshotDto ToDto(this SnapshotRow s) => new(s.BalanceCents, s.RecordedAt, s.RecordedBy);

    public static CategoryDto ToDto(this CategoryRow c) => new(c.Id, c.Name, c.Color, c.Icon, c.SortOrder, c.IsDefault);

    public static RecurringItemDto ToDto(this RecurringItemRow i, DateOnly today) => new(
        i.Id,
        i.Name,
        i.Kind,
        i.AmountCents,
        i.CategoryId,
        i.AccountId,
        i.Frequency,
        i.AnchorDate,
        i.EndDate,
        i.AutoPay,
        i.Notes,
        i.Url,
        RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency),
        RecurrenceEngine.NextDue(i.Rule, today),
        i.CreatedAt,
        i.UpdatedAt);

    public static OccurrenceDto ToOccurrence(this RecurringItemRow i, DateOnly dueDate, PaymentRow? paid) => new(
        i.Id,
        i.Name,
        i.Kind,
        i.AmountCents,
        dueDate,
        i.AutoPay,
        i.CategoryId,
        i.CategoryName,
        i.CategoryColor,
        i.AccountId,
        i.AccountName,
        paid is null ? null : new PaidInfo(paid.PaidAt, paid.PaidBy, paid.AmountCents));
}
