using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Services;

/// <summary>Expands a household's active recurring items into dated occurrences, joined with payments.</summary>
public sealed class OccurrenceService(RecurringRepository items)
{
    public async Task<List<OccurrenceDto>> ListAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await items.ListAsync(householdId, null, ct);
        return await ListAsync(householdId, rows, from, to, ct);
    }

    public async Task<List<OccurrenceDto>> ListAsync(Guid householdId, IReadOnlyList<RecurringItemRow> rows, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || rows.Count == 0)
        {
            return [];
        }

        var payments = await items.ListPaymentsAsync(householdId, from, to, ct);
        var paid = payments.ToDictionary(p => (p.RecurringItemId, p.DueDate));

        var result = new List<OccurrenceDto>();
        foreach (var item in rows)
        {
            foreach (var due in RecurrenceEngine.Expand(item.Rule, from, to))
            {
                result.Add(item.ToOccurrence(due, paid.GetValueOrDefault((item.Id, due))));
            }
        }

        result.Sort(static (a, b) =>
        {
            var byDate = a.DueDate.CompareTo(b.DueDate);
            return byDate != 0 ? byDate : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }
}
