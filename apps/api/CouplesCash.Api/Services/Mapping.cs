using System.Text.Json;
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
        paid is null ? null : new PaidInfo(paid.PaidAt, paid.PaidBy, paid.AmountCents, paid.Auto));

    // ---------------------------------------------------------------------------------------------
    // v2
    // ---------------------------------------------------------------------------------------------

    public static ExpenseDto ToDto(this ExpenseRow e) =>
        new(e.Id, e.Name, e.AmountCents, e.CategoryId, e.AccountId, e.SpentOn, e.Note, e.Deducted, e.CreatedBy, e.CreatedAt, e.UpdatedAt);

    public static TodoDto ToDto(this TodoRow t) =>
        new(t.Id, t.Title, t.AmountCents, t.DueDate, t.SortOrder, t.DoneAt, t.DoneBy, t.DoneByName, t.CreatedBy, t.CreatedAt);

    public static ActivityEventDto ToDto(this ActivityEventRow e) =>
        new(e.Id, e.Kind, e.Summary, e.ActorId, e.ActorName, e.EntityType, e.EntityId, ParseData(e.Data), e.CreatedAt);

    public static PasskeyDto ToDto(this WebAuthnCredentialRow c) => new(c.Id, c.Name, c.CreatedAt, c.LastUsedAt, c.BackedUp);

    public static PushSubscriptionDto ToDto(this PushSubscriptionRow s) => new(s.Id, s.UserAgent ?? "", s.CreatedAt, s.LastUsedAt);

    public static PushPrefsDto ToDto(this NotificationPrefsRow p, IEnumerable<PushSubscriptionRow> subscriptions) =>
        new(p.RemindersEnabled, p.DaysBefore.Select(d => (int)d).ToList(), p.HourLocal, p.ActivityEnabled, subscriptions.Select(s => s.ToDto()).ToList());

    private static JsonElement ParseData(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
