using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ComplexityScorer.Configuration;
using Xunit;

namespace ComplexityScorer.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task RejectsHttpEvenWithSpoofedForwardedProto()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.PostAsJsonAsync("http://localhost/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Theory]
    [InlineData("Api:Clients:first", "short")]
    [InlineData("Api:MaxConcurrentRequests", "0")]
    [InlineData("Tinker:BaseUrl", "http://insecure.example/")]
    [InlineData("Tinker:ApiKey", "")]
    [InlineData("Tinker:ReasoningEffort", "false")]
    [InlineData("Tinker:ReasoningEffort", "true")]
    [InlineData("Tinker:ReasoningEffort", "invalid")]
    [InlineData("Tinker:ReasoningEffort", "0.5")]
    public async Task InvalidConfigurationPreventsStartup(string setting, string value)
    {
        await using var factory = new ApiFactory(new()
        {
            [setting] = value,
            ["TINKER_API_KEY"] = ""
        });
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.Client());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public async Task SendsSupportedReasoningEffortOrOmitsWhenEmpty(string? effort)
    {
        await using var factory = new ApiFactory(new() { ["Tinker:ReasoningEffort"] = effort });
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = factory.Tinker.LastBody!.Value;
        if (string.IsNullOrEmpty(effort))
            Assert.False(body.TryGetProperty("reasoning_effort", out _));
        else
            Assert.Equal(effort, body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ScoresPromptUsingConfiguredCheckpointAndSamplingSettings()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        const string prompt = "Extract the name from this JSON.";
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.25m, result.GetProperty("score").GetDecimal());
        Assert.Single(result.EnumerateObject());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(1, factory.Tinker.Calls);
        Assert.Equal("https://tinker.thinkingmachines.dev/services/tinker-prod/oai/api/v1/chat/completions",
            factory.Tinker.LastUri!.ToString());
        Assert.Equal("Bearer test-tinker-secret", factory.Tinker.LastAuthorization);
        var body = factory.Tinker.LastBody!.Value;
        Assert.Equal(TinkerOptions.DefaultModel, body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());
        Assert.Equal(50, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.True(body.GetProperty("separate_reasoning").GetBoolean());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(prompt, body.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("an-invalid-key-that-is-at-least-32-characters")]
    public async Task RejectsUnauthorizedRequestsBeforeCallingTinker(string? key)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client(key);
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt = "Hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ApiKey", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Fact]
    public async Task RejectsMultipleApiKeys()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.OtherKey);
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt = "Hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"prompt\":null}")]
    [InlineData("{\"prompt\":\"  \"}")]
    [InlineData("{\"prompt\":1}")]
    [InlineData("{\"prompt\":\"ok\",\"model\":\"override\"}")]
    [InlineData("{broken")]
    public async Task RejectsInvalidInput(string json)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var response = await client.PostAsync("/api/complexity", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Fact]
    public async Task RejectsWrongContentTypeAndOversizedPrompt()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var text = await client.PostAsync("/api/complexity", new StringContent("hello"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        using var oversized = await client.PostAsJsonAsync("/api/complexity", new { prompt = new string('a', 8001) });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsOversizedBodyIncludingChunkedRequests(bool chunked)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { prompt = new string('a', 70_000) });
        using HttpContent content = chunked ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        using var response = await client.PostAsync("/api/complexity", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Fact]
    public async Task RateLimitsByAuthenticatedClientAndIgnoresSpoofedIpHeaders()
    {
        await using var factory = new ApiFactory(new() { ["Api:RequestsPerWindow"] = "1" });
        using var client = factory.Client();
        using var first = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        using var limited = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        using var otherClient = factory.Client(ApiFactory.OtherKey);
        using var other = await otherClient.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(2, factory.Tinker.Calls);
    }

    [Fact]
    public async Task GlobalRateLimitAlsoCoversUnauthenticatedRequests()
    {
        await using var factory = new ApiFactory(new() { ["Api:GlobalRequestsPerWindow"] = "1" });
        using var client = factory.Client(null);
        using var first = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        using var second = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(0, factory.Tinker.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsAreLimitedAndPermitIsReleased()
    {
        await using var factory = new ApiFactory(new() { ["Api:MaxConcurrentRequests"] = "1" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Tinker.Respond = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return FakeTinkerHandler.Completion("0.50");
        };
        using var client = factory.Client();
        var running = client.PostAsJsonAsync("/api/complexity", new { prompt = "first" });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var rejected = await client.PostAsJsonAsync("/api/complexity", new { prompt = "second" });
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.NotNull(rejected.Headers.RetryAfter);
        }
        finally
        {
            release.TrySetResult();
        }
        using var completed = await running;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var next = await client.PostAsJsonAsync("/api/complexity", new { prompt = "third" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, factory.Tinker.Calls);
    }

    [Theory]
    [InlineData(429, 503)]
    [InlineData(503, 503)]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 502)]
    [InlineData(400, 502)]
    [InlineData(422, 502)]
    [InlineData(500, 502)]
    [InlineData(504, 504)]
    public async Task UpstreamErrorsAreSanitizedAndNeverRetried(int upstreamStatus, int expectedStatus)
    {
        await using var factory = new ApiFactory();
        factory.Tinker.Respond = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstreamStatus)
        {
            Content = new StringContent("secret-upstream-information")
        });
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.DoesNotContain("secret-upstream-information", await response.Content.ReadAsStringAsync());
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(upstreamStatus, problem.GetProperty("upstreamStatusCode").GetInt32());
        Assert.Equal(1, factory.Tinker.Calls);
    }

    [Fact]
    public async Task UpstreamTimeoutReturns504()
    {
        await using var factory = new ApiFactory(new() { ["Tinker:TimeoutSeconds"] = "1" });
        factory.Tinker.Respond = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeTinkerHandler.Completion("0.5");
        };
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/complexity", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, factory.Tinker.Calls);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
