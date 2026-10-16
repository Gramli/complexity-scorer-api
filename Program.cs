using ComplexityScorer.Configuration;
using ComplexityScorer.Endpoints;
using ComplexityScorer.Infrastructure;
using ComplexityScorer.Security;
using ComplexityScorer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddScorerOptions(builder.Configuration);
builder.Services.AddTinkerScoring();
builder.Services.AddApiKeyAuthentication();
builder.Services.AddApiRateLimiting();
builder.ConfigureApiRequestHandling();

var app = builder.Build();

app.UseApiErrorHandling();
app.UseApiResponseHeaders();
app.UseRequiredHttps();

// Serve the built playground before API rate limiting; scoring still requires an API key.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().RequireAuthorization();

app.MapComplexityEndpoint();
app.Run();
