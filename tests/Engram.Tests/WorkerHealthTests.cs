using Engram.Tests.Worker;

namespace Engram.Tests;

public sealed class WorkerHealthTests
{
    [Fact]
    public async Task Health_returns_ok_and_creates_the_database()
    {
        using var factory = new WorkerFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\"ok\"");
        body.ShouldContain("\"schema\":1");
        File.Exists(Path.Combine(factory.Home, "engram.db")).ShouldBeTrue();
    }
}
