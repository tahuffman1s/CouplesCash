using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;

namespace CouplesCash.Api.Services;

/// <summary>
/// Expands a household's active recurring items into dated occurrences, joined with payments. The v4 <c>status</c>
/// (planned / cleared / overdue) is judged against <c>today</c>: UTC today unless the caller passes the household-local date.
/// </summary>
public sealed class OccurrenceService(RecurringRepository items, TimeProvider time)
{
    public async Task<List<OccurrenceDto>> ListAsync(Guid householdId, DateOnly from, DateOnly to, CancellationToken ct, DateOnly? today = null)
    {
        var rows = await items.ListAsync(householdId, null, ct);
        return await ListAsync(householdId, rows, from, to, ct, today);
    }

    public async Task<List<OccurrenceDto>> ListAsync(Guid householdId, IReadOnlyList<RecurringItemRow> rows, DateOnly from, DateOnly to, CancellationToken ct, DateOnly? today = null)
    {
        if (to < from || rows.Count == 0)
        {
            return [];
        }

        var payments = await items.ListPaymentsAsync(householdId, from, to, ct);
        var paid = payments.ToDictionary(p => (p.RecurringItemId, p.DueDate));
        var asOf = today ?? time.Today();

        var result = new List<OccurrenceDto>();
        foreach (var item in rows)
        {
            foreach (var due in RecurrenceEngine.Expand(item.Rule, from, to))
            {
                result.Add(item.ToOccurrence(due, paid.GetValueOrDefault((item.Id, due)), asOf));
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
