using System.Text;
using CouplesCash.Api.Auth;

namespace CouplesCash.Api.Infrastructure;

public static class JwtSecretGuard
{
    private const string DevelopmentSecret = "couplescash-development-only-secret-do-not-use-in-production!";

    /// <summary>
    /// Fails fast when <c>Jwt__Secret</c> is missing or shorter than 32 bytes. In Development a fixed dev secret is
    /// substituted with a loud warning so `dotnet run` just works.
    /// </summary>
    public static JwtOptions Resolve(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        var bytes = Encoding.UTF8.GetByteCount(options.Secret ?? "");
        if (bytes < JwtOptions.MinSecretBytes)
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Jwt__Secret must be set to at least {JwtOptions.MinSecretBytes} bytes (got {bytes}). " +
                    "Generate one with: openssl rand -base64 48");
            }

            logger.LogWarning("*** Jwt__Secret is missing or too short ({Bytes} bytes). Using the built-in DEVELOPMENT secret. Never run like this outside Development. ***", bytes);
            options.Secret = DevelopmentSecret;
        }

        if (options.AccessMinutes is < 1 or > 24 * 60)
        {
            throw new InvalidOperationException("Jwt__AccessMinutes must be between 1 and 1440.");
        }

        if (options.RefreshDays is < 1 or > 365)
        {
            throw new InvalidOperationException("Jwt__RefreshDays must be between 1 and 365.");
        }

        return options;
    }
}
