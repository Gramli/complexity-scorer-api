using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ComplexityScorer.Tests;

public sealed class DemoHostingTests
{
    [Fact]
    public async Task DemoAssetsAreServedOverHttpsWithoutConsumingApiAllowanceOrBypassingAuthentication()
    {
        // Exercise hosting without making the .NET test suite depend on a frontend build.
        var webRoot = Path.Combine(Path.GetTempPath(), $"complexity-demo-{Guid.NewGuid():N}");
        var demoDirectory = Path.Combine(webRoot, "demo");
        var indexPath = Path.Combine(demoDirectory, "index.html");
        var scriptPath = Path.Combine(demoDirectory, "app.js");
        Directory.CreateDirectory(demoDirectory);
        await File.WriteAllTextAsync(indexPath, "<!doctype html><title>Complexity playground</title>");
        await File.WriteAllTextAsync(scriptPath, "/* demo asset */");

        try
        {
            await using var factory = new ApiFactory(new() { ["Api:GlobalRequestsPerWindow"] = "1" });
            await using var demo = factory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
            using var client = demo.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });

            using var index = await client.GetAsync("/demo/");
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
            Assert.Contains("Complexity playground", await index.Content.ReadAsStringAsync());
            Assert.True(index.Headers.CacheControl!.NoStore);

            using var script = await client.GetAsync("/demo/app.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("nosniff", Assert.Single(script.Headers.GetValues("X-Content-Type-Options")));

            using var insecure = await client.GetAsync("http://localhost/demo/");
            Assert.Equal(HttpStatusCode.BadRequest, insecure.StatusCode);
            Assert.DoesNotContain("Complexity playground", await insecure.Content.ReadAsStringAsync());

            using var unauthenticated = await client.PostAsJsonAsync("/api/complexity", new { prompt = "A custom prompt" });
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            Assert.Equal(0, factory.Tinker.Calls);
        }
        finally
        {
            File.Delete(indexPath);
            File.Delete(scriptPath);
            Directory.Delete(demoDirectory);
            Directory.Delete(webRoot);
        }
    }
}
