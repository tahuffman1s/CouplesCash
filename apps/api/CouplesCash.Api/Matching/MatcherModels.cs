namespace CouplesCash.Api.Matching;

// Wire shapes of docs/MATCHER-CONTRACT.md (camelCase JSON, ids as strings, money in cents, dates YYYY-MM-DD).

public sealed record MatcherMatchOptions(string AutoLevel, int AutoThreshold, int SuggestThreshold);

public sealed record MatcherTransaction(
    string Id,
    string? AccountId,
    DateOnly PostedOn,
    long AmountCents,
    string Description,
    string? Payee,
    string? Memo,
    bool Pending);

public sealed record MatcherCandidate(
    string ItemId,
    DateOnly DueDate,
    string Kind,
    string Frequency,
    long AmountCents,
    bool AmountVaries,
    IReadOnlyList<string> Names,
    string? AccountId,
    bool Paid);

public sealed record MatcherCategoryRule(string Payee, string CategoryId, int Hits);

public sealed record MatcherMatchRequest(
    DateOnly Today,
    MatcherMatchOptions Options,
    IReadOnlyList<MatcherTransaction> Transactions,
    IReadOnlyList<MatcherCandidate> Candidates,
    IReadOnlyList<MatcherCategoryRule> CategoryRules);

public sealed record MatcherMatch(
    string TransactionId,
    string ItemId,
    DateOnly DueDate,
    int Score,
    int NameScore,
    int AmountScore,
    int DateScore,
    bool Auto,
    string Reason);

public sealed record MatcherCategoryMatch(string TransactionId, string CategoryId, int Score, bool Auto, string Reason);

public sealed record MatcherMatchResponse(string Engine, string Version, IReadOnlyList<MatcherMatch> Matches, IReadOnlyList<MatcherCategoryMatch> Categories);

public sealed record MatcherDetectTransaction(string Id, DateOnly PostedOn, long AmountCents, string Description, string? Payee);

public sealed record MatcherDetectRequest(DateOnly Today, IReadOnlyList<MatcherDetectTransaction> Transactions, IReadOnlyList<string> KnownNames);

public sealed record MatcherRecurringSuggestion(
    string Name,
    string BankName,
    string Kind,
    string Frequency,
    long AmountCents,
    bool AmountVaries,
    DateOnly NextDue,
    int Score,
    IReadOnlyList<string> TransactionIds);

public sealed record MatcherDetectResponse(IReadOnlyList<MatcherRecurringSuggestion> Suggestions);

public sealed record MatcherHealth(string Status, string Version);

/// <summary>Why a matcher call did not produce a usable answer; every kind means "use the built-in matcher".</summary>
public enum MatcherFailure
{
    Disabled,
    Unauthorized,
    PayloadTooLarge,
    Invalid,
    ServerError,
    Timeout,
    Unreachable,
    BadResponse,
}

/// <summary>A failed matcher call. The message never contains request data (descriptions, names).</summary>
public sealed class MatcherException(MatcherFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MatcherFailure Failure { get; } = failure;
}
