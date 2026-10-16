using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ComplexityScorer.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Security;

public static class ApiSecurityExtensions
{
    public const string ScoringRateLimitPolicy = "scoring";

    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyRegistry>();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<ApiOptions>>((rateLimiting, configured) =>
            {
                var limits = configured.Value;
                rateLimiting.GlobalLimiter = CreateGlobalLimiter(limits);
                rateLimiting.AddPolicy(ScoringRateLimitPolicy, context => CreateClientRateLimitPartition(context, limits));
                rateLimiting.OnRejected = WriteRateLimitRejectionAsync;
            });

        return services;
    }

    private static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(ApiOptions limits) =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
                "global", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.GlobalRequestsPerWindow,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0
                })),
            PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter(
                "global", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = limits.MaxConcurrentRequests,
                    QueueLimit = 0
                })));

    private static RateLimitPartition<string> CreateClientRateLimitPartition(HttpContext context, ApiOptions limits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.RequestsPerWindow,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0
            });

    private static async ValueTask WriteRateLimitRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 1;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Too many requests. Retry later.")
            .ExecuteAsync(context.HttpContext);
    }
}
