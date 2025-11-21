using Azure.Monitor.OpenTelemetry.Exporter;
using DotNetEnv;
using MarketMakerWorkerService;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Services;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;

// Load environment variables from .env file in development

var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
{
    Env.Load(envPath);
    Console.WriteLine($"Loaded environment variables from: {envPath}");
}


var builder = Host.CreateApplicationBuilder(args);

// Load configuration with environment variable expansion (needed before Serilog)
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Manually expand environment variable placeholders in configuration
ExpandEnvironmentVariables(builder.Configuration);

// Configure Serilog for structured logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("Application", "MarketMakerWorkerService")
    .CreateLogger();

builder.Services.AddSerilog();

// Bind MarketMakerConfiguration from appsettings
builder.Services.Configure<MarketMakerConfiguration>(
    builder.Configuration.GetSection("MarketMaker"));

// Configure OpenTelemetry Metrics (Azure Application Insights)
var enableMetrics = builder.Configuration.GetValue<int>("MarketMaker:EnableMetrics", 0);

if (enableMetrics == 1)
{
    var metricsExportInterval = builder.Configuration.GetValue<int>("MarketMaker:MetricsExportIntervalMs", 60000);
    var appInsightsConnectionString = builder.Configuration.GetValue<string>("MarketMaker:ApplicationInsightsConnectionString");

    if (!string.IsNullOrEmpty(appInsightsConnectionString))
    {
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("MarketMaker");
                
                // Azure Monitor exporter (uses internal ~60s interval)
                metrics.AddAzureMonitorMetricExporter(options =>
                {
                    options.ConnectionString = appInsightsConnectionString;
                });
                
                // Console exporter for local visibility (uses configured interval)
                metrics.AddConsoleExporter((exporterOptions, metricReaderOptions) =>
                {
                    metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = metricsExportInterval;
                });
            });
        
        Log.Information("Metrics enabled: Azure App Insights + Console (console interval: {Interval}ms, Azure uses internal ~60s)", 
            metricsExportInterval);
    }
    else
    {
        Log.Warning("Azure Application Insights connection string not configured - metrics disabled");
    }
}
else
{
    Log.Information("OpenTelemetry metrics disabled (ENABLE_METRICS=0)");
}

// Register HttpClient for API calls
builder.Services.AddHttpClient("PerpetualsAPI", (serviceProvider, client) =>
{
    var config = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MarketMakerConfiguration>>().Value;
    client.BaseAddress = new Uri(config.ApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "MarketMakerWorkerService/1.0");
});

// Register metrics service
builder.Services.AddSingleton<MetricsService>();

// Register Stage 1 services
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
builder.Services.AddSingleton<IRedisConnectionService, RedisConnectionService>();

// Register Stage 2 services
builder.Services.AddSingleton<OrderStateManager>();
builder.Services.AddSingleton<IOrderService, OrderService>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<IContinuousSettlementService, ContinuousSettlementService>();

// Register Stage 3 services
builder.Services.AddSingleton<RedisIndexWatcher>();
builder.Services.AddSingleton<BasicMarketMakerStrategy>();


// Register the worker service
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Log startup information
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Market Maker Worker Service starting");
logger.LogInformation("Environment: {Environment}", builder.Environment.EnvironmentName);

// Verify configuration
var config = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MarketMakerConfiguration>>().Value;
logger.LogInformation("API Base URL: {ApiBaseUrl}", config.ApiBaseUrl);
logger.LogInformation("Account ID: {AccountId}", config.AccountId);
logger.LogInformation("Ledger ID: {LedgerId}", config.LedgerId);
logger.LogInformation("Redis Index Key: {RedisIndexKey}", config.RedisIndexKey);

// Log metrics configuration status
if (config.EnableMetrics == 1)
{
    logger.LogInformation("Metrics: ENABLED → Azure Application Insights (export interval: {Interval}ms)", 
        config.MetricsExportIntervalMs);
}
else
{
    logger.LogInformation("Metrics: DISABLED");
}

// Validate critical configuration
if (string.IsNullOrEmpty(config.AccountId))
{
    logger.LogError("AccountId is not configured. Set HEDERA_ACCOUNT_ID environment variable.");
    return;
}

if (string.IsNullOrEmpty(config.PrivateKeyDerHex))
{
    logger.LogError("PrivateKeyDerHex is not configured. Set HEDERA_PRIVATE_KEY_DER_HEX environment variable.");
    return;
}

if (string.IsNullOrEmpty(config.RedisConnectionString))
{
    logger.LogError("RedisConnectionString is not configured. Set REDIS_CONNECTION_STRING environment variable.");
    return;
}

try
{
    logger.LogInformation("Starting Market Maker Worker Service");
    host.Run();
    logger.LogInformation("Market Maker Worker Service stopped cleanly");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Helper method to expand ${VAR} placeholders in configuration
static void ExpandEnvironmentVariables(IConfiguration configuration)
{
    foreach (var section in configuration.GetChildren())
    {
        ExpandSection(section);
    }
}

static void ExpandSection(IConfigurationSection section)
{
    if (section.Value != null)
    {
        var value = section.Value;
        if (value.StartsWith("${") && value.EndsWith("}"))
        {
            var placeholder = value.Substring(2, value.Length - 3);
            
            // Support ${VAR:default} syntax
            string envVarName;
            string? defaultValue = null;
            
            var colonIndex = placeholder.IndexOf(':');
            if (colonIndex > 0)
            {
                envVarName = placeholder.Substring(0, colonIndex);
                defaultValue = placeholder.Substring(colonIndex + 1);
            }
            else
            {
                envVarName = placeholder;
            }
            
            var envValue = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrEmpty(envValue))
            {
                section.Value = envValue;
            }
            else if (defaultValue != null)
            {
                section.Value = defaultValue;
            }
        }
    }
    
    foreach (var child in section.GetChildren())
    {
        ExpandSection(child);
    }
}
