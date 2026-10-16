using System.Net;
using ComplexityScorer.Configuration;
using ComplexityScorer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ComplexityScorer.Tests;

public sealed class TinkerClientTests
{
    [Fact]
    public async Task DefaultOptionsWorkWithCheckpointThatRejectsReasoningEffort()
    {
        using var handler = new FakeTinkerHandler();
        handler.Respond = cancellationToken => Task.FromResult(
            handler.LastBody!.Value.TryGetProperty("reasoning_effort", out _)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : FakeTinkerHandler.Completion("0.25"));
        using var http = CreateHttp(handler);
        var score = await CreateScorer(http).ScoreAsync("prompt", default);

        Assert.Equal(0.25m, score);
        Assert.False(handler.LastBody!.Value.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, "Tinker authentication failed. Check the server's Tinker API key.")]
    [InlineData(403, "Tinker denied access. Check the server's Tinker account and model permissions.")]
    [InlineData(404, "The configured Tinker endpoint or model was not found.")]
    [InlineData(400, "Tinker rejected the scoring request. Check the model and sampling settings.")]
    [InlineData(422, "Tinker rejected the scoring request. Check the model and sampling settings.")]
    public async Task ExplainsUpstreamFailuresWithoutExposingTheirBodies(int statusCode, string expectedMessage)
    {
        using var handler = new FakeTinkerHandler
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent("private-provider-error-and-secret")
            })
        };
        using var http = CreateHttp(handler);
        var error = await Assert.ThrowsAsync<ScoringException>(() => CreateScorer(http).ScoreAsync("prompt", default));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(statusCode, error.UpstreamStatusCode);
        Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("0.00", 0.00)]
    [InlineData("1.00", 1.00)]
    [InlineData(" 0.375\n", 0.38)]
    public async Task AcceptsAndRoundsScores(string output, double expected)
    {
        using var handler = new FakeTinkerHandler { Respond = _ => Task.FromResult(FakeTinkerHandler.Completion(output)) };
        using var http = CreateHttp(handler);
        var scorer = CreateScorer(http);
        Assert.Equal((decimal)expected, await scorer.ScoreAsync("prompt", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("1.01")]
    [InlineData("0.25 because it is easy")]
    [InlineData("{\"score\":0.5}")]
    [InlineData("<think>0.5</think>0.25")]
    [InlineData("0,50")]
    public async Task RejectsInvalidScores(string? output)
    {
        using var handler = new FakeTinkerHandler { Respond = _ => Task.FromResult(FakeTinkerHandler.Completion(output)) };
        using var http = CreateHttp(handler);
        var error = await Assert.ThrowsAsync<ScoringException>(() => CreateScorer(http).ScoreAsync("prompt", default));
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    public async Task RejectsIncompleteOrUnexpectedCompletions(string reason)
    {
        using var handler = new FakeTinkerHandler { Respond = _ => Task.FromResult(FakeTinkerHandler.Completion("0.5", reason)) };
        using var http = CreateHttp(handler);
        var error = await Assert.ThrowsAsync<ScoringException>(() => CreateScorer(http).ScoreAsync("prompt", default));
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":3}]}")]
    public async Task RejectsMalformedEnvelopes(string json)
    {
        using var handler = new FakeTinkerHandler
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })
        };
        using var http = CreateHttp(handler);
        var error = await Assert.ThrowsAsync<ScoringException>(() => CreateScorer(http).ScoreAsync("prompt", default));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToUpstream()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeTinkerHandler
        {
            Respond = async ct =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { if (ct.IsCancellationRequested) cancelled.TrySetResult(); }
                return FakeTinkerHandler.Completion("0.5");
            }
        };
        using var http = CreateHttp(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = CreateScorer(http).ScoreAsync("prompt", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimeoutIncludesReadingTheResponseBody()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeTinkerHandler
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream(cancelled))
            })
        };
        using var http = CreateHttp(handler);
        var scorer = new TinkerComplexityScorer(http,
            Options.Create(new TinkerOptions { ApiKey = "test-secret", TimeoutSeconds = 1 }),
            NullLogger<TinkerComplexityScorer>.Instance);
        var error = await Assert.ThrowsAsync<ScoringException>(() => scorer.ScoreAsync("prompt", default));
        Assert.Equal(504, error.StatusCode);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RejectsOversizedUpstreamBody()
    {
        using var handler = new FakeTinkerHandler
        {
            Respond = _ => Task.FromResult(FakeTinkerHandler.Completion(new string('0', 70_000)))
        };
        using var http = CreateHttp(handler);
        var error = await Assert.ThrowsAsync<ScoringException>(() => CreateScorer(http).ScoreAsync("prompt", default));
        Assert.Equal(502, error.StatusCode);
    }

    private static HttpClient CreateHttp(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://tinker.example/api/v1/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static TinkerComplexityScorer CreateScorer(HttpClient http) => new(http,
        Options.Create(new TinkerOptions { ApiKey = "test-secret" }), NullLogger<TinkerComplexityScorer>.Instance);

    private sealed class StalledStream(TaskCompletionSource cancelled) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) cancelled.TrySetResult(); }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
