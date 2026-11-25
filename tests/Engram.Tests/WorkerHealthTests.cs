using Microsoft.AspNetCore.Mvc.Testing;

namespace Engram.Tests;

public sealed class WorkerHealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\"ok\"");
    }
}
