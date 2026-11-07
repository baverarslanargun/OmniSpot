using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IntentParserRateLimitTests
{
    private const string Intent = """
        {"mode":"keyword","target":"file","hard_extensions":[],"soft_extensions":[],"folders":[],
        "created_from":null,"created_to_exclusive":null,"modified_from":null,"modified_to_exclusive":null,
        "min_mb":null,"max_mb":null,"open":false}
        """;
    private const string Keywords = """
        {"anchors":[{"primary":"rapor","variants":[],"translations":[]}],"phrases":[],"context":[]}
        """;

    [Theory]
    [InlineData("Request too large: Limit 1000, Requested 2048", "rate_limit_exceeded")]
    [InlineData("output tokens per minute: Limit 1,000, Requested 2,048", "rate_limit_exceeded")]
    [InlineData("Account quota exhausted", "insufficient_quota")]
    [InlineData("Billing limit", "billing_hard_limit_reached")]
    public async Task PermanentLimitsAreNotRetriedWithTheSamePayload(string message, string code)
    {
        var keyword = new Handler(call => call == 1 ? Limited(message, code, TimeSpan.Zero) : Completion(Keywords));
        var parser = Parser(keyword);

        var result = await parser.ParseWithGroqAsync("rapor");

        Assert.Equal(1, keyword.Calls);
        Assert.Contains("429", result.WarningMessage);
        Assert.False(result.UsedFallback);
        Assert.Contains(result.SearchTerms, term => term.Text == "rapor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientLimitsWaitForTheFullDeltaOrDateHeader(bool dateHeader)
    {
        var clock = new ManualClock();
        var keyword = new Handler(call =>
        {
            if (call > 1) return Completion(Keywords);
            var response = Limited("Limit 1000, Used 900, Requested 200", "rate_limit_exceeded", TimeSpan.FromSeconds(8));
            if (dateHeader) response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(8));
            return response;
        });
        var parser = Parser(keyword, clock);
        var pending = parser.ParseWithGroqAsync("rapor");
        Assert.True(await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5)));

        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(1, keyword.Calls);
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, keyword.Calls);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task CancellingOneQueryDoesNotEraseTheProviderCooldownForTheNextQuery()
    {
        var clock = new ManualClock();
        var keyword = new Handler(call => call == 1
            ? Limited("Too many tokens", "rate_limit_exceeded", TimeSpan.FromSeconds(9)) : Completion(Keywords));
        var parser = Parser(keyword, clock);
        using var cancellation = new CancellationTokenSource();
        var pending = parser.ParseWithGroqAsync("rapor", cancellation.Token);
        Assert.True(await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var coolingDown = await parser.ParseWithGroqAsync("başka rapor");
        Assert.Equal(1, keyword.Calls);
        Assert.Contains("9 sn", coolingDown.WarningMessage);
        clock.Advance(TimeSpan.FromSeconds(9));
        var resumed = await parser.ParseWithGroqAsync("rapor");
        Assert.Equal(2, keyword.Calls);
        Assert.Null(resumed.WarningMessage);
    }

    [Fact]
    public async Task ALongCooldownReturnsThe429WithoutAnEarlyRetryOrTimeout()
    {
        var clock = new ManualClock();
        var keyword = new Handler(call => call == 1
            ? Limited("Please retry later", "rate_limit_exceeded", TimeSpan.FromMinutes(1)) : Completion(Keywords));
        var parser = Parser(keyword, clock);

        var result = await parser.ParseWithGroqAsync("rapor").WaitAsync(TimeSpan.FromSeconds(5));
        var next = await parser.ParseWithGroqAsync("başka rapor").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, keyword.Calls);
        Assert.Contains("429", result.WarningMessage);
        Assert.Contains("60 sn", next.WarningMessage);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await parser.ParseWithGroqAsync("rapor")).WarningMessage);
        Assert.Equal(2, keyword.Calls);
    }

    [Fact]
    public async Task TheLastFailedAttemptAlsoProtectsSubsequentQueries()
    {
        var keyword = new Handler(call => Limited("Try again later", "rate_limit_exceeded",
            call == 1 ? TimeSpan.Zero : TimeSpan.FromSeconds(6)));
        var parser = Parser(keyword, new ManualClock());

        await parser.ParseWithGroqAsync("rapor");
        await parser.ParseWithGroqAsync("başka rapor");

        Assert.Equal(2, keyword.Calls);
    }

    [Fact]
    public void ExpiredDatesAreImmediateAndProvidersDoNotShareTheirCooldowns()
    {
        var clock = new ManualClock();
        var state = new ProviderRetryState(clock);
        using var response = Limited("try later", "rate_limit_exceeded", TimeSpan.Zero);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(-1));
        Assert.Equal(TimeSpan.Zero, state.RetryDelay(response, 0));
        state.Defer("Groq", TimeSpan.FromSeconds(20));
        state.Defer("Groq", TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(20), state.Remaining("Groq"));
        Assert.Equal(TimeSpan.Zero, state.Remaining("Celeris"));
    }

    [Fact]
    public async Task TruncatedKeywordContentIsRejectedEvenIfItLooksLikeValidJson()
    {
        var keyword = new Handler(_ => Completion(Keywords.Replace("rapor", "yarim-yanit"), "length"));
        var result = await Parser(keyword).ParseWithGroqAsync("rapor");
        Assert.Contains("tamamlanmadı", result.WarningMessage);
        Assert.DoesNotContain(result.SearchTerms, term => term.Text == "yarim-yanit");
        Assert.Contains(result.SearchTerms, term => term.Text == "rapor");
    }

    private static IntentParser Parser(Handler keyword, TimeProvider? clock = null) =>
        new(new HttpClient(new Handler(_ => Completion(Intent))), new HttpClient(keyword), timeProvider: clock);

    private static HttpResponseMessage Completion(string content, string finishReason = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        { choices = new[] { new { message = new { content }, finish_reason = finishReason } } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Limited(string message, string code, TimeSpan delay)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message, code } })) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(Interlocked.Increment(ref Calls)));
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        internal SemaphoreSlim TimerCreated { get; } = new(0);
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) { timer.Change(dueTime, period); _timers.Add(timer); }
            TimerCreated.Release();
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (_gate)
            {
                _utcNow += elapsed;
                ready = _timers.Where(timer => !timer.Disposed && timer.Due <= _utcNow).ToArray();
                foreach (var timer in ready) timer.Disposed = true;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal DateTimeOffset Due;
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._utcNow + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
