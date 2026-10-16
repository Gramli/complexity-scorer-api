using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComplexityScorer.Configuration;
using ComplexityScorer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComplexityScorer.Tests;

internal sealed class FakeTinkerHandler : HttpMessageHandler
{
    public int Calls;
    public JsonElement? LastBody;
    public Uri? LastUri;
    public string? LastAuthorization;
    public Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(Completion("0.25"));

    public static HttpResponseMessage Completion(string? score, string finishReason = "stop") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            choices = new[] { new { finish_reason = finishReason, message = new { content = score } } }
        })
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        LastBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        LastUri = request.RequestUri;
        LastAuthorization = request.Headers.Authorization?.ToString();
        return await Respond(cancellationToken);
    }
}

internal sealed class ApiFactory(Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    public const string Key = "test-client-one-key-000000000000000000000000";
    public const string OtherKey = "test-client-two-key-000000000000000000000000";
    public FakeTinkerHandler Tinker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(Path.GetFullPath("../../../../../", AppContext.BaseDirectory));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Tinker:ApiKey"] = "test-tinker-secret",
                ["Api:Clients:first"] = Key,
                ["Api:Clients:second"] = OtherKey,
                ["Api:RequestsPerWindow"] = "100",
                ["Api:GlobalRequestsPerWindow"] = "1000",
                ["Logging:LogLevel:Default"] = "None"
            };
            foreach (var pair in overrides ?? [])
                settings[pair.Key] = pair.Value;
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services =>
        {
            // Tests own their credentials; never merge real or placeholder client keys.
            services.PostConfigure<ApiOptions>(options =>
            {
                options.Clients = new() { ["first"] = Key, ["second"] = OtherKey };
                const string prefix = "Api:Clients:";
                foreach (var pair in overrides ?? [])
                    if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        options.Clients[pair.Key[prefix.Length..]] = pair.Value ?? "";
            });
            services.AddHttpClient<IComplexityScorer, TinkerComplexityScorer>()
                .ConfigurePrimaryHttpMessageHandler(() => Tinker);
        });
    }

    public HttpClient Client(string? key = Key)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        if (key is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
