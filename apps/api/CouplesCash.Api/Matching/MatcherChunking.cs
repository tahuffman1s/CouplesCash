namespace CouplesCash.Api.Matching;

/// <summary>
/// Splits one logical <c>/v1/match</c> call into requests the service accepts (≤ <c>ChunkSize</c> transactions, ≤ <c>MaxCandidates</c>
/// candidates) and merges the answers back into one. Pure.
/// </summary>
public static class MatcherChunking
{
    /// <summary>
    /// Transactions sorted by date are cut into chunks of at most <paramref name="maxTransactions"/>; each chunk carries only the
    /// candidates due within ±<paramref name="windowDays"/> of one of its transactions. A chunk still over
    /// <paramref name="maxCandidates"/> is halved until it fits (a single transaction keeps its nearest candidates).
    /// </summary>
    public static IReadOnlyList<MatcherMatchRequest> Split(MatcherMatchRequest request, int maxTransactions, int maxCandidates, int windowDays)
    {
        maxTransactions = Math.Max(1, maxTransactions);
        maxCandidates = Math.Max(1, maxCandidates);
        var sorted = request.Transactions.OrderBy(t => t.PostedOn).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var candidates = request.Candidates.OrderBy(c => c.DueDate).ToList();
        var result = new List<MatcherMatchRequest>();

        for (var i = 0; i < sorted.Count; i += maxTransactions)
        {
            Add(sorted.GetRange(i, Math.Min(maxTransactions, sorted.Count - i)));
        }

        return result;

        void Add(List<MatcherTransaction> chunk)
        {
            var near = candidates.Where(c => chunk.Any(t => Math.Abs(t.PostedOn.DayNumber - c.DueDate.DayNumber) <= windowDays)).ToList();
            if (near.Count > maxCandidates && chunk.Count > 1)
            {
                var half = chunk.Count / 2;
                Add(chunk.GetRange(0, half));
                Add(chunk.GetRange(half, chunk.Count - half));
                return;
            }

            if (near.Count > maxCandidates)
            {
                var posted = chunk[0].PostedOn.DayNumber;
                near = near.OrderBy(c => Math.Abs(c.DueDate.DayNumber - posted)).Take(maxCandidates).OrderBy(c => c.DueDate).ToList();
            }

            result.Add(request with { Transactions = chunk, Candidates = near });
        }
    }

    /// <summary>
    /// One answer from several: one match per transaction and per occurrence (the best score wins when two chunks claimed the same
    /// occurrence), categories only for transactions left without a match, one category per transaction.
    /// </summary>
    public static MatcherMatchResponse Merge(IReadOnlyList<MatcherMatchResponse> responses)
    {
        if (responses.Count == 1)
        {
            return responses[0];
        }

        var matches = new List<MatcherMatch>();
        var takenTx = new HashSet<string>(StringComparer.Ordinal);
        var takenOccurrence = new HashSet<(string, DateOnly)>();
        foreach (var match in responses.SelectMany(r => r.Matches).OrderByDescending(m => m.Score).ThenBy(m => m.TransactionId, StringComparer.Ordinal))
        {
            if (takenTx.Contains(match.TransactionId) || !takenOccurrence.Add((match.ItemId, match.DueDate)))
            {
                continue;
            }

            takenTx.Add(match.TransactionId);
            matches.Add(match);
        }

        var categories = responses.SelectMany(r => r.Categories)
            .Where(c => !takenTx.Contains(c.TransactionId))
            .GroupBy(c => c.TransactionId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .ToList();

        var first = responses.Count > 0 ? responses[0] : new MatcherMatchResponse("fuzzy", "", [], []);
        return new MatcherMatchResponse(first.Engine, first.Version, matches, categories);
    }
}
