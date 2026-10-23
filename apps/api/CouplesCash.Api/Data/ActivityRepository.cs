using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class ActivityRepository(NpgsqlDataSource db)
{
    /// <summary>Appends an event inside the caller's transaction. <paramref name="dataJson"/> must be a JSON object.</summary>
    public Task<int> InsertAsync(
        NpgsqlConnection conn,
        Guid id,
        Guid householdId,
        Guid? actorId,
        string kind,
        string entityType,
        Guid? entityId,
        string summary,
        string dataJson,
        DateTime now,
        CancellationToken ct) =>
        conn.ExecuteAsync(Cmd(
            """
            INSERT INTO activity_events (id, household_id, actor_id, kind, entity_type, entity_id, summary, data, created_at)
            VALUES (@Id, @HouseholdId, @ActorId, @Kind, @EntityType, @EntityId, @Summary, @Data::jsonb, @Now)
            """,
            new
            {
                Id = id,
                HouseholdId = householdId,
                ActorId = actorId,
                Kind = kind,
                EntityType = entityType,
                EntityId = entityId,
                Summary = summary,
                Data = dataJson,
                Now = now,
            }, ct));

    /// <summary>Newest first, keyset-paginated on (created_at, id). Returns one extra row so the caller knows whether more exist.</summary>
    public async Task<IReadOnlyList<ActivityEventRow>> ListAsync(Guid householdId, DateTime? before, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ActivityEventRow>(Cmd(
            """
            SELECT e.id, e.household_id, e.actor_id, u.display_name AS actor_name, e.kind, e.entity_type, e.entity_id,
                   e.summary, e.data::text AS data, e.created_at
            FROM activity_events e
            LEFT JOIN users u ON u.id = e.actor_id
            WHERE e.household_id = @HouseholdId AND (@Before::timestamptz IS NULL OR e.created_at < @Before)
            ORDER BY e.created_at DESC, e.id DESC
            LIMIT @Limit
            """,
            new { HouseholdId = householdId, Before = before, Limit = limit + 1 }, ct));
        return rows.ToList();
    }
}
