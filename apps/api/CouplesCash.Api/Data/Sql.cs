using Dapper;

namespace CouplesCash.Api.Data;

internal static class Sql
{
    /// <summary>Shorthand for a cancellable Dapper command.</summary>
    public static CommandDefinition Cmd(string sql, object? parameters, CancellationToken ct) =>
        new(sql, parameters, cancellationToken: ct);
}
