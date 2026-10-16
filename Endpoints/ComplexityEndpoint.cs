using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComplexityScorer.Configuration;
using ComplexityScorer.Infrastructure;
using ComplexityScorer.Security;
using ComplexityScorer.Services;
using Microsoft.Extensions.Options;

namespace ComplexityScorer.Endpoints;

public sealed record ScoreRequest([property: Required] string? Prompt);
public sealed record ScoreResponse([property: Range(0.0, 1.0)] decimal Score);

public static class ComplexityEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RouteHandlerBuilder MapComplexityEndpoint(this WebApplication app) => app
        .MapPost("/api/complexity", ScoreAsync)
        .WithName("ScoreComplexity")
        .WithSummary("Estimate the reasoning complexity of a prompt from 0 to 1.")
        .Accepts<ScoreRequest>("application/json")
        .Produces<ScoreResponse>()
        .ProducesProblem(400)
        .ProducesProblem(401)
        .ProducesProblem(413)
        .ProducesProblem(415)
        .ProducesProblem(429)
        .ProducesProblem(502)
        .ProducesProblem(503)
        .ProducesProblem(504)
        .RequireAuthorization()
        .RequireRateLimiting(ApiSecurityExtensions.ScoringRateLimitPolicy);

    private static async Task<IResult> ScoreAsync(HttpContext context,
        IComplexityScorer scorer, IOptions<ApiOptions> options)
    {
        var limits = options.Value;
        if (!context.Request.HasJsonContentType())
            return Results.Problem(statusCode: 415, title: "Content-Type must be application/json.");

        if (context.Request.ContentLength > limits.MaxRequestBytes)
            return Results.Problem(statusCode: 413, title: "Request body is too large.");

        ScoreRequest? request;
        try
        {
            var body = await BoundedBody.ReadAsync(context.Request.Body, limits.MaxRequestBytes,
                context.RequestAborted);
            request = JsonSerializer.Deserialize<ScoreRequest>(body, JsonOptions);
        }
        catch (BodyTooLargeException)
        {
            return Results.Problem(statusCode: 413, title: "Request body is too large.");
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Expected a JSON object with a string prompt.");
        }
        catch (BadHttpRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: "Invalid request body.");
        }

        if (string.IsNullOrWhiteSpace(request?.Prompt) || request.Prompt.Length > limits.MaxPromptCharacters)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["prompt"] = [$"Prompt must contain between 1 and {limits.MaxPromptCharacters} characters and cannot be whitespace."]
            });

        try
        {
            var score = await scorer.ScoreAsync(request.Prompt, context.RequestAborted);
            return Results.Ok(new ScoreResponse(score));
        }
        catch (ScoringException exception)
        {
            if (exception.StatusCode == 503)
                context.Response.Headers.RetryAfter = "5";
            Dictionary<string, object?>? extensions = exception.UpstreamStatusCode is { } upstreamStatus
                ? new() { ["upstreamStatusCode"] = upstreamStatus }
                : null;
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Message, extensions: extensions);
        }
    }
}
