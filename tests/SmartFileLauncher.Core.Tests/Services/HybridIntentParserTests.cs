using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class HybridIntentParserTests
{
    private const string Intent = """
        {"mode":"filter","target":"file","hard_extensions":["pdf"],"soft_extensions":[],
         "folders":["Downloads"],"created_from":null,"created_to_exclusive":null,
         "modified_from":"2026-09-01","modified_to_exclusive":"2026-10-01",
         "min_mb":null,"max_mb":null,"open":false}
        """;
    private const string Keywords = """
        {"anchors":[{"primary":"fatura","variants":["faturalar"],"translations":["invoice"]}],
         "phrases":[],"context":[]}
        """;

    [Theory]
    [InlineData(null, "medium")]
    [InlineData("low", "low")]
    [InlineData("high", "high")]
    public async Task DefaultsRouteToSeparateProvidersAndEffortOnlyChangesKeywords(string? effort, string expected)
    {
        var intent = Handler.Returning(Intent);
        var keyword = Handler.Returning(Keywords);
        using var intentClient = new HttpClient(intent);
        using var keywordClient = new HttpClient(keyword);
        intentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "intent-test-key");
        keywordClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "keyword-test-key");
        var parser = new IntentParser(intentClient, keywordClient,
            nowProvider: () => new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
            timeZone: TimeZoneInfo.Utc);

        var result = await parser.ParseWithGroqEffortAsync("İndirilenlerdeki geçen ay değiştirdiğim faturalar", effort);

        Assert.False(result.UsedFallback);
        Assert.Equal("https://inference.celeris.ai/celeris-1/v1/chat/completions", intent.Uri);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", keyword.Uri);
        Assert.Equal("intent-test-key", intent.Authorization);
        Assert.Equal("keyword-test-key", keyword.Authorization);
        using var intentBody = JsonDocument.Parse(intent.Body!);
        var root = intentBody.RootElement;
        Assert.Equal("celeris-1", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetDouble());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning_format", out _));
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        using var input = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal("2026-10-17", input.RootElement.GetProperty("today").GetString());
        Assert.Equal("UTC", input.RootElement.GetProperty("timezone").GetString());
        var schema = root.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Equal(12, schema.GetProperty("required").GetArrayLength());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        using var keywordBody = JsonDocument.Parse(keyword.Body!);
        Assert.Equal("qwen/qwen3.8-27b", keywordBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(expected, keywordBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(768, keywordBody.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.6, keywordBody.RootElement.GetProperty("temperature").GetDouble());
        Assert.True(keywordBody.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task RequestsStartInParallelAndKeywordsCorrectFilterWithoutLosingMetadata()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Handler(async token => { await release.Task.WaitAsync(token); return Completion(Intent); });
        var keyword = new Handler(async token => { await release.Task.WaitAsync(token); return Completion(Keywords); });
        var parser = new IntentParser(new HttpClient(intent), new HttpClient(keyword));
        var pending = parser.ParseWithGroqAsync("geçen ay değiştirdiğim faturalar");
        try
        {
            await Task.WhenAll(intent.Started.Task, keyword.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await pending;
        Assert.False(result.UsedFallback);
        Assert.False(result.FilterOnlyMode);
        Assert.Equal("2026-09-01", result.DateFilter?.ModifiedAfter);
        Assert.Equal("2026-10-01", result.DateFilter?.ModifiedBeforeExclusive);
        Assert.Equal("Downloads", Assert.Single(result.FolderHints).Name);
        Assert.Equal("pdf", Assert.Single(result.HardExtensions));
        Assert.Contains(result.SearchTerms, term => term.Text == "invoice" && term.Role == SearchTermRole.Anchor);
        Assert.All(result.SearchTerms, term => Assert.Equal(0, term.AnchorGroup));
    }

    [Fact]
    public async Task MetadataOnlyQueryStillCallsBothProviders()
    {
        var keyword = Handler.Returning("""{"anchors":[],"phrases":[],"context":[]}""");
        var parser = new IntentParser(new HttpClient(Handler.Returning(Intent)), new HttpClient(keyword));
        var result = await parser.ParseWithGroqAsync("İndirilenlerdeki geçen ay değiştirilmiş PDF dosyaları");
        Assert.True(keyword.Started.Task.IsCompleted);
        Assert.True(result.FilterOnlyMode);
        Assert.Empty(result.SearchTerms);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("null-array")]
    [InlineData("null-open")]
    [InlineData("invalid-date")]
    [InlineData("length")]
    public async Task InvalidOrIncompleteIntentFallsBackWithoutOpening(string invalid)
    {
        var payload = JsonNode.Parse(Intent)!.AsObject();
        switch (invalid)
        {
            case "missing": payload.Remove("open"); break;
            case "extra": payload["unexpected"] = true; break;
            case "null-array": payload["folders"] = null; break;
            case "null-open": payload["open"] = null; break;
            case "invalid-date": payload["modified_from"] = "yesterday"; break;
        }
        var json = payload.ToJsonString();
        if (invalid == "duplicate") json = json.Insert(1, "\"open\":true,");
        var intent = new Handler(_ => Task.FromResult(Completion(json, invalid == "length" ? "length" : "stop")));
        var parser = new IntentParser(new HttpClient(intent), new HttpClient(Handler.Returning(Keywords)));

        var result = await parser.ParseWithGroqAsync("fatura pdf aç");

        Assert.True(result.UsedFallback);
        Assert.NotNull(result.FallbackReason);
        Assert.Contains("fatura", result.Keywords);
        Assert.NotEqual(true, result.OpenAction?.ShouldOpen);
    }

    [Fact]
    public async Task KeywordFailurePreservesCelerisFiltersAndUsesRuleBasedTerms()
    {
        var keyword = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{}") }));
        var parser = new IntentParser(new HttpClient(Handler.Returning(Intent.Replace("\"filter\"", "\"keyword\""))), new HttpClient(keyword));
        var result = await parser.ParseWithGroqAsync("fatura pdf");
        Assert.False(result.UsedFallback);
        Assert.Contains("400", result.WarningMessage);
        Assert.Equal("2026-09-01", result.DateFilter?.ModifiedAfter);
        Assert.Contains(result.SearchTerms, term => term.Text == "fatura");
    }

    [Fact]
    public async Task UserCancellationReachesBothProviders()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Handler(async token => { await never.Task.WaitAsync(token); return Completion(Intent); });
        var keyword = new Handler(async token => { await never.Task.WaitAsync(token); return Completion(Keywords); });
        var parser = new IntentParser(new HttpClient(intent), new HttpClient(keyword));
        using var cancellation = new CancellationTokenSource();
        var pending = parser.ParseWithGroqAsync("fatura", cancellation.Token);
        await Task.WhenAll(intent.Started.Task, keyword.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(intent.Cancellation.IsCancellationRequested);
        Assert.True(keyword.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void KeywordProviderCannotUseCelerisWithGroqCredentials()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IntentParser(new HttpClient(), new HttpClient(), keywordModel: "celeris-1"));
    }

    private static HttpResponseMessage Completion(string content, string finishReason = "stop") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content }, finish_reason = finishReason } }
            }), Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public static Handler Returning(string content) => new(_ => Task.FromResult(Completion(content)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.Parameter;
            Cancellation = cancellationToken;
            Started.TrySetResult();
            return await respond(cancellationToken);
        }
    }
}
