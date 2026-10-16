using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ComplexityScorer.Configuration;
using ComplexityScorer.Infrastructure;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Services;

public interface IComplexityScorer
{
    Task<decimal> ScoreAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class ScoringException(int statusCode, string message, int? upstreamStatusCode = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public int? UpstreamStatusCode { get; } = upstreamStatusCode;
}

public sealed class TinkerComplexityScorer(
    HttpClient httpClient, IOptions<TinkerOptions> options, ILogger<TinkerComplexityScorer> logger)
    : IComplexityScorer
{
    private const int MaxResponseBytes = 65_536;
    private readonly TinkerOptions settings = options.Value;

    public async Task<decimal> ScoreAsync(string prompt, CancellationToken cancellationToken)
    {
        // Covers headers AND the complete response body when using ResponseHeadersRead.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["messages"] = new[]
            {
                new { role = "system", content = settings.SystemPrompt },
                new { role = "user", content = prompt }
            },
            ["temperature"] = settings.Temperature,
            ["max_tokens"] = settings.MaxOutputTokens,
            ["n"] = 1,
            ["stream"] = false,
            ["separate_reasoning"] = true
        };
        if (!string.IsNullOrEmpty(settings.ReasoningEffort))
            body["reasoning_effort"] = settings.ReasoningEffort;

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tinker returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw CreateUpstreamException(response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new BodyTooLargeException();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await BoundedBody.ReadAsync(stream, MaxResponseBytes, timeout.Token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return ReadScore(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Tinker request timed out.");
            throw new ScoringException(504, "The scoring service timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning("Tinker transport failed.");
            throw new ScoringException(502, "The scoring service could not be reached.");
        }
        catch (Exception exception) when (exception is JsonException or BodyTooLargeException)
        {
            logger.LogWarning("Tinker returned an invalid response.");
            throw InvalidOutput();
        }
    }

    private static decimal ReadScore(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw InvalidOutput();

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object
            || !choice.TryGetProperty("finish_reason", out var reason)
            || reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop"
            || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw InvalidOutput();

        var text = content.GetString()!.Trim();
        if (text.Length is 0 or > 64
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score)
            || score is < 0 or > 1)
            throw InvalidOutput();

        return decimal.Round(score, 2, MidpointRounding.AwayFromZero);
    }

    private static ScoringException CreateUpstreamException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized =>
            new(502, "Tinker authentication failed. Check the server's Tinker API key.", (int)statusCode),
        HttpStatusCode.Forbidden =>
            new(502, "Tinker denied access. Check the server's Tinker account and model permissions.", (int)statusCode),
        HttpStatusCode.NotFound =>
            new(502, "The configured Tinker endpoint or model was not found.", (int)statusCode),
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            new(502, "Tinker rejected the scoring request. Check the model and sampling settings.", (int)statusCode),
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
            new(503, "The scoring service is temporarily unavailable.", (int)statusCode),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
            new(504, "The scoring service timed out.", (int)statusCode),
        _ => new(502, "The scoring service request failed.", (int)statusCode)
    };

    private static ScoringException InvalidOutput() => new(502, "The scoring service returned an invalid score.");
}
