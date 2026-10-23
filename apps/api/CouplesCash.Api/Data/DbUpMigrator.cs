using System.Reflection;
using DbUp;
using DbUp.Engine.Output;

namespace CouplesCash.Api.Data;

/// <summary>Runs the embedded <c>Migrations/*.sql</c> scripts with DbUp (journaled in <c>public.schema_versions</c>).</summary>
public sealed class DbUpMigrator(string connectionString, ILogger<DbUpMigrator> logger)
{
    public bool Run()
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .JournalToPostgresqlTable("public", "schema_versions")
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromMinutes(5))
            .LogTo(new LoggerAdapter(logger))
            .Build();

        var pending = upgrader.GetScriptsToExecute();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date");
            return true;
        }

        logger.LogInformation("Applying {Count} migration(s): {Scripts}", pending.Count, string.Join(", ", pending.Select(s => s.Name)));
        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            logger.LogError(result.Error, "Migration {Script} failed", result.ErrorScript?.Name);
            return false;
        }

        logger.LogInformation("Migrations applied successfully");
        return true;
    }

    private sealed class LoggerAdapter(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);

        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);

        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);

        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);

        public void LogError(string format, params object[] args) => logger.LogError(format, args);

        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
