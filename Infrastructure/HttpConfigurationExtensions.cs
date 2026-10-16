using ComplexityScorer.Configuration;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Infrastructure;

public static class HttpConfigurationExtensions
{
    public static WebApplicationBuilder ConfigureApiRequestHandling(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddRequestTimeouts();
        builder.Services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<TinkerOptions>>((timeouts, tinker) =>
            timeouts.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(tinker.Value.TimeoutSeconds + 10),
                TimeoutStatusCode = 504,
                WriteTimeoutResponse = context => Results.Problem(statusCode: 504, title: "The request timed out.")
                    .ExecuteAsync(context)
            });

        builder.WebHost.ConfigureKestrel((context, server) =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = context.Configuration.GetValue<int?>("Api:MaxRequestBytes") ?? 65_536;
        });

        return builder;
    }
}
