using System.Text.Json;
using System.Text.Json.Serialization;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Data;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

// ---------------------------------------------------------------------------------------------
// Logging: Serilog to console; human-readable in Development, compact JSON everywhere else.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (environment.IsDevelopment())
    {
        logger.WriteTo.Console();
    }
    else
    {
        logger.WriteTo.Console(new CompactJsonFormatter());
    }
});

using var startupLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Startup");

// ---------------------------------------------------------------------------------------------
// Kestrel: listen on 8080 unless told otherwise; 64 KB request bodies are plenty for this API.
// ---------------------------------------------------------------------------------------------
if (string.IsNullOrWhiteSpace(configuration["urls"]) && string.IsNullOrWhiteSpace(configuration["ASPNETCORE_HTTP_PORTS"]) && string.IsNullOrWhiteSpace(configuration["http_ports"]))
{
    builder.WebHost.UseUrls("http://+:8080");
}

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

// ---------------------------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------------------------
var connectionString = configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings__Default is required, e.g. Host=db;Database=couplescash;Username=couplescash;Password=...");
}

var jwt = JwtSecretGuard.Resolve(configuration, environment, startupLogger);
var apiOptions = configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
apiOptions.AllowedOrigins = ApiOptions.ReadAllowedOrigins(configuration);
var rateLimiting = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();

builder.Services.AddSingleton(Options.Create(jwt));
builder.Services.AddSingleton(apiOptions);
builder.Services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------------------------
// JSON: camelCase, enums as camelCase strings, DateOnly as YYYY-MM-DD (STJ default).
// ---------------------------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

// ---------------------------------------------------------------------------------------------
// Data, auth, domain services, validation
// ---------------------------------------------------------------------------------------------
builder.Services.AddCouplesCashData(connectionString);

builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddSingleton<CoupleCodeService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthSessionService>();
builder.Services.AddSingleton<OccurrenceService>();
builder.Services.AddCouplesCashValidators();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub", "email", "name", "hh" as-is
        options.TokenValidationParameters = jwt.CreateValidationParameters();
    });
builder.Services.AddAuthorization();

// Nothing here needs persisted Data Protection keys (no cookies, no antiforgery), and the container
// runs with a read-only root FS, so keep the key ring in memory instead of writing to ~/.aspnet.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

// ---------------------------------------------------------------------------------------------
// HTTP plumbing: problem details, exception handling, rate limiting, forwarded headers, CORS, docs
// ---------------------------------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// Surface body/parameter binding failures as exceptions so GlobalExceptionHandler can shape them as problem+json.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddCouplesCashRateLimiting(rateLimiting);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // The API only ever sits behind the web (BFF) container / reverse proxy on a private network,
    // so we trust X-Forwarded-* from any upstream. Never publish this port directly.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

if (apiOptions.AllowedOrigins.Length > 0)
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(apiOptions.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var docsEnabled = apiOptions.EnableDocs || environment.IsDevelopment();
if (docsEnabled)
{
    builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<OpenApiBearerTransformer>());
}

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Migrations: `dotnet CouplesCash.Api.dll --migrate` migrates and exits; otherwise run on startup.
// ---------------------------------------------------------------------------------------------
var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
if (migrateOnly || database.MigrateOnStartup)
{
    var migrator = app.Services.GetRequiredService<DbUpMigrator>();
    var ok = migrator.Run();
    if (migrateOnly)
    {
        await app.DisposeAsync();
        return ok ? 0 : 1;
    }

    if (!ok)
    {
        throw new InvalidOperationException("Database migration failed; refusing to start. See log for details.");
    }
}

// ---------------------------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------------------------
app.UseForwardedHeaders();

// Request logging sits outside the exception handler so it records the status the client actually
// received (a JSON binding failure is a 400, not a 500) and never double-logs handled exceptions.
app.UseSerilogRequestLogging(options => options.GetLevel = static (httpContext, _, exception) =>
    exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
        ? Serilog.Events.LogEventLevel.Error
        : Serilog.Events.LogEventLevel.Information);

app.UseExceptionHandler();
app.UseStatusCodePages();

if (apiOptions.AllowedOrigins.Length > 0)
{
    app.UseCors();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (docsEnabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("CouplesCash API")
        .AddPreferredSecuritySchemes(OpenApiBearerTransformer.SchemeName)
        .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl));
    app.Logger.LogInformation("API docs enabled at /scalar (OpenAPI: /openapi/v1.json)");
}

app.MapCouplesCash();

await app.RunAsync();
return 0;
