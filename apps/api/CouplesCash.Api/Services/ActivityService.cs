using System.Text.Json;
using System.Text.Json.Serialization;
using CouplesCash.Api.Data;
using CouplesCash.Api.Push;
using Npgsql;

namespace CouplesCash.Api.Services;

/// <summary>
/// Writes <c>activity_events</c> rows in the caller's transaction and, once that transaction commits,
/// pushes "&lt;Actor&gt; &lt;summary&gt;" to the other member when they have partner-activity pushes on.
/// </summary>
public sealed class ActivityService(ActivityRepository activity, PushNotifier notifier, TimeProvider time, ILogger<ActivityService> logger)
{
    private static readonly JsonSerializerOptions DataJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Records an event in the unit of work and queues the partner push for after commit.</summary>
    public async Task<Guid> RecordAsync(
        UnitOfWork uow,
        Guid householdId,
        Guid? actorId,
        string kind,
        string entityType,
        Guid? entityId,
        string summary,
        object? data,
        CancellationToken ct,
        string? actorName = null)
    {
        var id = await RecordAsync(uow.Connection, uow.Transaction, householdId, actorId, kind, entityType, entityId, summary, data, ct);
        uow.AfterCommit(() => NotifyPartner(householdId, actorId, actorName, summary, kind, entityId, id));
        return id;
    }

    /// <summary>Records an event on an explicit connection/transaction without any push side effect (scheduler).</summary>
    public async Task<Guid> RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid householdId,
        Guid? actorId,
        string kind,
        string entityType,
        Guid? entityId,
        string summary,
        object? data,
        CancellationToken ct)
    {
        _ = transaction; // Npgsql runs the command inside the connection's open transaction.
        var id = Guid.CreateVersion7();
        await activity.InsertAsync(connection, id, householdId, actorId, kind, entityType, entityId, summary, SerializeData(data), time.GetUtcNow().UtcDateTime, ct);
        return id;
    }

    public static string SerializeData(object? data) => data is null ? "{}" : JsonSerializer.Serialize(data, DataJson);

    /// <summary>Summaries are already plain text; this strips any stray markdown emphasis defensively.</summary>
    public static string Plain(string summary) => summary.Replace("**", "", StringComparison.Ordinal);

    /// <summary>Where the partner lands when tapping the push, by entity type.</summary>
    public static string DeepLink(string entityType, Guid? entityId, string kind) => (entityType, entityId) switch
    {
        ("account", { } id) when kind != "account.deleted" => $"/accounts/{id}",
        ("account", _) => "/accounts",
        ("recurring", { } id) when kind != "recurring.deleted" => $"/bills/{id}",
        ("recurring", _) => "/bills",
        ("occurrence", _) => "/calendar",
        ("expense", { } id) when kind != "expense.deleted" => $"/expenses/{id}",
        ("expense", _) => "/bills?tab=expenses",
        ("budget", _) => "/insights",
        ("todo", _) => "/todos",
        ("goal", { } id) when kind != "goal.deleted" => $"/goals/{id}",
        ("goal", _) => "/goals",
        ("household", _) => "/couple",
        ("category", _) => "/insights",
        _ => "/activity",
    };

    private void NotifyPartner(Guid householdId, Guid? actorId, string? actorName, string summary, string kind, Guid? entityId, Guid eventId)
    {
        try
        {
            var body = string.IsNullOrWhiteSpace(actorName) ? Plain(summary) : $"{actorName} {Plain(summary)}";
            var entityType = kind.Split('.')[0] switch
            {
                "occurrence" => "occurrence",
                "couple" => "household",
                "user" => "household",
                var other => other,
            };
            notifier.ToHousehold(householdId, actorId, new PushPayload(
                Title: "CouplesCash",
                Body: body,
                Url: DeepLink(entityType, entityId, kind),
                Tag: $"activity-{eventId}"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not queue partner-activity push for event {EventId}", eventId);
        }
    }
}
