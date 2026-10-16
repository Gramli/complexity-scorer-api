namespace ComplexityScorer.Configuration;

public static class ConfigurationExtensions
{
    public static IServiceCollection AddScorerOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidClients(),
                "Configure Api:Clients with unique API keys of 32-256 printable non-space ASCII characters.")
            .ValidateOnStart();

        services.AddOptions<TinkerOptions>()
            .BindConfiguration(TinkerOptions.SectionName)
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                    options.ApiKey = configuration["TINKER_API_KEY"] ?? "";
            })
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey)
                && options.ApiKey.All(character => character is >= '!' and <= '~'),
                "Set Tinker:ApiKey or TINKER_API_KEY to a valid API key.")
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && options.BaseUrl.EndsWith('/'), "Tinker:BaseUrl must be an absolute HTTPS URL ending in /.")
            .Validate(options => options.Model.StartsWith("tinker://", StringComparison.Ordinal)
                && options.Model.Contains("/sampler_weights/", StringComparison.Ordinal),
                "Tinker:Model must identify a sampler checkpoint.")
            .Validate(options => options.ReasoningEffort is null or "" or "none" or "minimal"
                or "low" or "medium" or "high" or "xhigh",
                "Tinker:ReasoningEffort must be empty or one of: none, minimal, low, medium, high, xhigh. Boolean strings are not supported.")
            .ValidateOnStart();

        return services;
    }
}
