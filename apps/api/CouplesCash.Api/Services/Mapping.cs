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
        i.PayeeHint,
        RecurrenceEngine.MonthlyEquivalent(i.AmountCents, i.Frequency),
        RecurrenceEngine.NextDue(i.Rule, today),
        i.CreatedAt,
        i.UpdatedAt);

    /// <summary><paramref name="today"/> decides planned vs. overdue (v4 <c>status</c>); <c>varianceCents = paid − planned</c> when paid.</summary>
    public static OccurrenceDto ToOccurrence(this RecurringItemRow i, DateOnly dueDate, PaymentRow? paid, DateOnly today) => new(
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
        paid is null ? null : new PaidInfo(paid.PaidAt, paid.PaidBy, paid.AmountCents, paid.Auto, paid.Source),
        WeekEngine.Variance(paid?.AmountCents, i.AmountCents),
        WeekEngine.Status(dueDate, paid is not null, today));

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
        new(p.RemindersEnabled, p.DaysBefore.Select(d => (int)d).ToList(), p.HourLocal, p.ActivityEnabled, p.WeekSummaryEnabled, subscriptions.Select(s => s.ToDto()).ToList());

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

public static class MappingV3
{
    public static ExternalAccountDto ToDto(this ExternalAccountRow e) =>
        new(e.Id, e.ExternalId, e.OrgName, e.Name, e.Currency, e.BalanceCents, e.AvailableCents, e.BalanceAt, e.AccountId, e.AccountName, e.UpdatedAt);

    public static CsvImportHistoryDto ToDto(this CsvImportRow c) =>
        new(c.Id, c.Filename, c.RowCount, c.ImportedCount, c.DuplicateCount, c.CreatedAt, c.CreatedByName ?? "");

    public static InboxItemDto ToDto(this ImportedTransactionRow t) => new(
        t.Id,
        t.Source,
        t.AccountId,
        t.AccountName,
        t.PostedOn,
        t.AmountCents,
        t.Description,
        t.Payee,
        t.Memo,
        t.Pending,
        t.Status,
        Suggestion(t),
        t.Status == "pending" ? null : new InboxConfirmedDto(t.ConfirmedExpenseId, t.ConfirmedItemId, t.ConfirmedDueDate, t.ReviewedBy, t.ReviewedByName, t.ReviewedAt, t.ConfirmedAuto, t.ConfirmedCategoryName, t.ConfirmedItemName),
        t.CreatedAt);

    private static SuggestionDto? Suggestion(ImportedTransactionRow t)
    {
        if (t is { SuggestedItemId: { } itemId, SuggestedDueDate: { } dueDate, SuggestedItemName: { } itemName })
        {
            var kind = Enum.TryParse<Kind>(t.SuggestedItemKind, ignoreCase: true, out var parsed) ? parsed : Kind.Bill;
            return new OccurrenceSuggestionDto(itemId, itemName, kind, dueDate, t.Confidence);
        }

        if (t.SuggestedTransfer)
        {
            return new TransferSuggestionDto(t.Confidence);
        }

        if (t is { SuggestedCategoryId: { } categoryId, SuggestedCategoryName: { } categoryName })
        {
            return new CategorySuggestionDto(categoryId, categoryName, t.Confidence);
        }

        return null;
    }
}
