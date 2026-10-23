using Npgsql;

namespace CouplesCash.Api.Data;

/// <summary>
/// One connection + one transaction for a mutating request, so the data change and its activity event
/// commit (or roll back) together. Side effects that must only happen after a successful commit
/// (push notifications) are queued with <see cref="AfterCommit"/>.
/// </summary>
public sealed class UnitOfWork : IAsyncDisposable
{
    private readonly List<Action> _afterCommit = [];
    private bool _committed;

    private UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<UnitOfWork> BeginAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        var conn = await db.OpenConnectionAsync(ct);
        try
        {
            var tx = await conn.BeginTransactionAsync(ct);
            return new UnitOfWork(conn, tx);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs after <see cref="CommitAsync"/> succeeds. Never runs on rollback. Must not throw.</summary>
    public void AfterCommit(Action action) => _afterCommit.Add(action);

    public async Task CommitAsync(CancellationToken ct)
    {
        await Transaction.CommitAsync(ct);
        _committed = true;
        foreach (var action in _afterCommit)
        {
            action();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            try
            {
                await Transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // Already completed or the connection is broken; disposing below is all that is left.
            }
        }

        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

public static class UnitOfWorkExtensions
{
    public static Task<UnitOfWork> BeginUnitOfWorkAsync(this NpgsqlDataSource db, CancellationToken ct) => UnitOfWork.BeginAsync(db, ct);
}
