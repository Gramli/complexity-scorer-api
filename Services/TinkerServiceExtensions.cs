using ComplexityScorer.Configuration;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Services;

public static class TinkerServiceExtensions
{
    public static IServiceCollection AddTinkerScoring(this IServiceCollection services)
    {
        services.AddHttpClient<IComplexityScorer, TinkerComplexityScorer>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TinkerOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        .RemoveAllLoggers();

        return services;
    }
}
