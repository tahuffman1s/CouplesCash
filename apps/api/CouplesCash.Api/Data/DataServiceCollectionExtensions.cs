using CouplesCash.Api.Auth;
using Npgsql;

namespace CouplesCash.Api.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddCouplesCashData(this IServiceCollection services, string connectionString)
    {
        DapperConfig.Configure();

        services.AddSingleton(sp =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>());
            return builder.Build();
        });

        services.AddSingleton(sp => new DbUpMigrator(connectionString, sp.GetRequiredService<ILogger<DbUpMigrator>>()));

        services.AddSingleton<UserRepository>();
        services.AddSingleton<HouseholdRepository>();
        services.AddSingleton<CoupleCodeRepository>();
        services.AddSingleton<RefreshTokenRepository>();
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<RefreshTokenRepository>());
        services.AddSingleton<AccountRepository>();
        services.AddSingleton<CategoryRepository>();
        services.AddSingleton<RecurringRepository>();
        services.AddSingleton<ActivityRepository>();
        services.AddSingleton<ExpenseRepository>();
        services.AddSingleton<BudgetRepository>();
        services.AddSingleton<TodoRepository>();
        services.AddSingleton<GoalRepository>();
        services.AddSingleton<PushRepository>();
        services.AddSingleton<WebAuthnRepository>();
        services.AddSingleton<BankRepository>();
        services.AddSingleton<ImportRepository>();

        return services;
    }
}
