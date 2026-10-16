using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ComplexityScorer.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Security;

public sealed class ApiKeyRegistry(IOptions<ApiOptions> options)
{
    private readonly (string ClientId, byte[] Hash)[] clients = options.Value.Clients
        .Select(pair => (pair.Key, SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))))
        .ToArray();

    public string? FindClient(string key)
    {
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string? clientId = null;
        foreach (var client in clients)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, client.Hash))
                clientId = client.ClientId;
        }
        return clientId;
    }
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyRegistry registry) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var keys))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (keys.Count != 1 || keys[0] is not { Length: >= 32 and <= 256 } key)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var clientId = registry.FindClient(key);
        if (clientId is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, clientId)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
            title: "A valid X-Api-Key header is required.").ExecuteAsync(Context);
    }
}
