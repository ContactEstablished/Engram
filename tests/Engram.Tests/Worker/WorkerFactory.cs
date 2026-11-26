using System.Net.Http.Headers;
using Dapper;
using Engram.Core;
using Engram.Core.Security;
using Engram.Worker.Capture;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Engram.Tests.Worker;

/// <summary>An in-process worker over its own temp data folder (never the real ~/.engram).</summary>
internal sealed class WorkerFactory : WebApplicationFactory<Program>
{
    private readonly TempDir _temp = new();

    private readonly Engram.Worker.Observer.ILlmClient? _fakeClient;

    /// <param name="settingsJson">Defaults to <c>observer.provider = none</c> so a test never launches the real claude.</param>
    /// <param name="fakeClient">Replaces the summarizer provider (the observer then runs for real, against this fake).</param>
    public WorkerFactory(string? settingsJson = null, Engram.Worker.Observer.ILlmClient? fakeClient = null)
    {
        _fakeClient = fakeClient;
        File.WriteAllText(EngramPaths.SettingsFile(Home), settingsJson ?? """{ "observer": { "provider": "none" } }""");

        Token = WorkerToken.ReadOrCreate(Home);
    }

    public string Home => _temp.Path;

    public string Token { get; }

    public TempDir Temp => _temp;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Engram:Home", Home);
        if (_fakeClient is { } fake)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Engram.Worker.Observer.ObserverRuntime>();
                services.AddSingleton(sp => new Engram.Worker.Observer.ObserverRuntime(
                    new Engram.Worker.Observer.Observer(fake, sp.GetRequiredService<Engram.Worker.Observer.ObserverStore>(), sp.GetRequiredService<Engram.Worker.WorkerContext>().Settings.Observer, new Redactor()),
                    fake));
            });
        }
    }

    public HttpClient CreateHookClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public Task DrainAsync() =>
        Services.GetRequiredService<CaptureQueue>().WaitForIdleAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

    public IReadOnlyList<T> Query<T>(string sql, object? args = null)
    {
        using var c = new SqliteConnection($"Data Source={EngramPaths.Database(Home)};Pooling=false");
        c.Open();
        return [.. c.Query<T>(sql, args)];
    }

    public int Execute(string sql, object? args = null)
    {
        using var c = new SqliteConnection($"Data Source={EngramPaths.Database(Home)};Pooling=false");
        c.Open();
        return c.Execute(sql, args);
    }

    public long Count(string sql, object? args = null)
    {
        using var c = new SqliteConnection($"Data Source={EngramPaths.Database(Home)};Pooling=false");
        c.Open();
        return c.ExecuteScalar<long>(sql, args);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // Surface the worker's log in the test output; it's the first place to look when a test fails.
            var logs = EngramPaths.Logs(Home);
            if (Directory.Exists(logs))
            {
                foreach (var file in Directory.GetFiles(logs))
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(File.ReadAllText(file));
                }
            }

            SqliteConnection.ClearAllPools();
            _temp.Dispose();
        }
    }
}
