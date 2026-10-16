using System.ComponentModel.DataAnnotations;

namespace ComplexityScorer.Configuration;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    // Stable client IDs are used for rate-limit partitions; keys are never partition names.
    public Dictionary<string, string> Clients { get; set; } = [];

    [Range(1, 100_000)]
    public int RequestsPerWindow { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    [Range(1, 256)]
    public int MaxConcurrentRequests { get; set; } = 4;

    [Range(1, 100_000)]
    public int GlobalRequestsPerWindow { get; set; } = 120;

    [Range(1, 100_000)]
    public int MaxPromptCharacters { get; set; } = 8_000;

    [Range(1024, 1_048_576)]
    public int MaxRequestBytes { get; set; } = 65_536;

    public bool HasValidClients() => Clients.Count is > 0 and <= 1000
        && Clients.All(pair => !string.IsNullOrWhiteSpace(pair.Key)
            && pair.Value is { Length: >= 32 and <= 256 }
            && pair.Value.All(character => character is >= '!' and <= '~'))
        && Clients.Values.Distinct(StringComparer.Ordinal).Count() == Clients.Count;
}
