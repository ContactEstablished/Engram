using System.Globalization;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;
using Engram.Worker;
using Engram.Worker.Capture;
using Engram.Worker.Embedding;
using Engram.Worker.Memory;
using Engram.Worker.Observer;
using Engram.Worker.Storage;

// The Engram worker: sole writer to ~/.engram/engram.db. Phase 1 = capture only.
var builder = WebApplication.CreateSlimBuilder(args);
Func<string, string?> getEnv = Environment.GetEnvironmentVariable;

// Tests point Engram:Home at a temp folder; real runs use ENGRAM_HOME or ~/.engram.
var home = builder.Configuration["Engram:Home"] ?? EngramPaths.Home(getEnv);
using var instance = SingleInstanceLock.TryAcquire(home);
if (instance is null)
{
    Console.Error.WriteLine($"engram-worker: already running for {home}");
    return 0;
}

var (settings, settingsWarnings) = SettingsLoader.Load(home, getEnv);
var context = new WorkerContext(home, settings, WorkerToken.ReadOrCreate(home), WorkerContext.DetectPlatform(getEnv));

// Loopback only. Never bind to all interfaces.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? $"http://127.0.0.1:{settings.Worker.Port}");
builder.Logging.AddProvider(new FileLoggerProvider(EngramPaths.Logs(home)));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // no per-request lines

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(context);
builder.Services.AddSingleton(new Database(EngramPaths.Database(home)));
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddSingleton<ObserverStore>();
builder.Services.AddSingleton<JobSignal>();
builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddSingleton<ContextBuilder>();
builder.Services.AddSingleton<VectorIndex>();
builder.Services.AddSingleton<EmbeddingSignal>();
builder.Services.AddSingleton<IEmbeddingClient>(new OllamaEmbeddingClient(settings.Search, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<HybridSearcher>();
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton(sp => ObserverRuntime.Create(
    sp.GetRequiredService<WorkerContext>(), sp.GetRequiredService<ObserverStore>(), sp.GetRequiredService<IProcessRunner>(), new HttpClient(), getEnv));
builder.Services.AddSingleton<ProjectResolver>();
builder.Services.AddSingleton<CaptureProcessor>();
builder.Services.AddSingleton<CaptureQueue>();
builder.Services.AddSingleton<IdleTracker>();
builder.Services.AddSingleton<SpoolIngestor>();
builder.Services.AddHostedService<CaptureWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpoolIngestor>());
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddingService>());
builder.Services.AddHostedService<IdleShutdownService>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Engram.Worker");
foreach (var warning in settingsWarnings)
{
    log.LogWarning("Settings: {Warning}", warning);
}

var migration = app.Services.GetRequiredService<Database>().Initialize(EngramPaths.Backups(home), TimeProvider.System);
log.LogInformation(
    "Worker {Version} starting: home {Home}, platform {Platform}, schema v{From} -> v{To}{Backup}",
    typeof(WorkerContext).Assembly.GetName().Version?.ToString(3),
    home,
    context.Platform,
    migration.FromVersion,
    migration.ToVersion,
    migration.BackupPath is null ? string.Empty : $", backup {migration.BackupPath}");

app.UseMiddleware<LocalOnlyMiddleware>();
app.MapGet("/health", () => Results.Text(
    string.Create(CultureInfo.InvariantCulture, $$"""{"status":"ok","version":"{{typeof(WorkerContext).Assembly.GetName().Version?.ToString(3)}}","pid":{{Environment.ProcessId}},"schema":{{migration.ToVersion}}}"""),
    "application/json"));
app.MapHookEndpoints();
app.MapStatsEndpoint();
app.MapMemoryEndpoints();
app.MapPost("/shutdown", (HttpContext http, IHostApplicationLifetime lifetime) =>
{
    if (!WorkerToken.Matches(http.Request.Headers.Authorization, context.Token))
    {
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    lifetime.StopApplication(); // the capture queue drains as the host stops
    return Results.Ok();
});

await app.RunAsync().ConfigureAwait(false);
return 0;

public partial class Program;
