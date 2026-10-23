using Dapper;
using Npgsql;
using static CouplesCash.Api.Data.Sql;

namespace CouplesCash.Api.Data;

public sealed class UserRepository(NpgsqlDataSource db)
{
    private const string Columns = "id, email, display_name, password_hash, created_at";

    public async Task<UserRow?> FindByEmailAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(Cmd(
            $"SELECT {Columns} FROM users WHERE email = @Email::citext",
            new { Email = email }, ct));
    }

    public async Task<UserRow?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(Cmd(
            $"SELECT {Columns} FROM users WHERE id = @Id",
            new { Id = id }, ct));
    }

    /// <summary>Returns null when the email is already registered (case-insensitive).</summary>
    public async Task<UserRow?> CreateAsync(string email, string displayName, string passwordHash, DateTime now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(Cmd(
            $"""
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@Id, @Email::citext, @DisplayName, @PasswordHash, @Now)
            ON CONFLICT (email) DO NOTHING
            RETURNING {Columns}
            """,
            new { Id = Guid.CreateVersion7(), Email = email, DisplayName = displayName, PasswordHash = passwordHash, Now = now }, ct));
    }

    public Task<UserRow?> UpdateDisplayNameAsync(NpgsqlConnection conn, Guid id, string displayName, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<UserRow>(Cmd(
            $"UPDATE users SET display_name = @DisplayName WHERE id = @Id RETURNING {Columns}",
            new { Id = id, DisplayName = displayName }, ct));

    public async Task<string?> DisplayNameAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>(Cmd("SELECT display_name FROM users WHERE id = @Id", new { Id = id }, ct));
    }

    public async Task UpdatePasswordHashAsync(Guid id, string passwordHash, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(Cmd(
            "UPDATE users SET password_hash = @PasswordHash WHERE id = @Id",
            new { Id = id, PasswordHash = passwordHash }, ct));
    }
}
