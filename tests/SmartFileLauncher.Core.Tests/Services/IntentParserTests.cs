using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IntentParserTests
{
    [Fact]
    public async Task ProductionDefaultsUseOss120BMediumAndQwenKeywords()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "keyword",
                  "target": "file",
                  "hard_extensions": [],
                  "soft_extensions": [],
                  "folders": [],
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    { "primary": "bilet", "variants": ["biletler"], "translations": [] }
                  ],
                  "phrases": [],
                  "context": []
                }
                """));
        var parser = new IntentParser(
            new HttpClient(intentHandler),
            new HttpClient(keywordHandler),
            nowProvider: () => new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            timeZone: TimeZoneInfo.Utc);

        await parser.ParseWithGroqAsync("bu yaza ait biletler");

        using var intentDocument = JsonDocument.Parse(Assert.Single(intentHandler.RequestBodies));
        Assert.Equal(
            "openai/gpt-oss-120b",
            intentDocument.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "medium",
            intentDocument.RootElement.GetProperty("reasoning_effort").GetString());

        using var keywordDocument = JsonDocument.Parse(Assert.Single(keywordHandler.RequestBodies));
        Assert.Equal(
            "qwen/qwen3.6-27b",
            keywordDocument.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "none",
            keywordDocument.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ParallelResponsesMapToValidatedWeightedQuery()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "keyword",
                  "target": "file",
                  "hard_extensions": [".XLSX", "bad ext"],
                  "soft_extensions": ["pdf", "xlsx"],
                  "folders": ["Downloads"],
                  "created_from": "2026-07-01",
                  "created_to_exclusive": "2026-08-01",
                  "modified_from": null,
                  "modified_to_exclusive": null,
                  "min_mb": 1,
                  "max_mb": 20,
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    {
                      "primary": "bütçe raporu",
                      "variants": ["butce raporu", "bütçe-raporu"],
                      "translations": ["budget report"]
                    },
                    { "primary": "2026", "variants": [], "translations": [] }
                  ],
                  "phrases": ["2026 bütçe raporu"],
                  "context": ["finance"]
                }
                """));
        var parser = CreateParser(intentHandler, keywordHandler);

        var result = await parser.ParseWithGroqAsync("2026 bütçe raporu excel");

        Assert.False(result.UsedFallback);
        Assert.Equal(["xlsx"], result.HardExtensions);
        Assert.Equal(["pdf"], result.SoftExtensions);
        Assert.Equal("Downloads", Assert.Single(result.FolderHints).Name);
        Assert.Equal("2026-07-01", result.DateFilter?.CreatedAfter);
        Assert.Equal("2026-08-01", result.DateFilter?.CreatedBeforeExclusive);
        Assert.Equal(1, result.SizeFilter?.MinMb);
        Assert.Equal(20, result.SizeFilter?.MaxMb);
        Assert.Equal(7, result.SearchTerms.Count);
        Assert.Equal(1, result.SearchTerms[0].Weight);
        Assert.Equal(SearchTermCategory.Exact, result.SearchTerms[0].Category);
        Assert.Equal(SearchTermRole.Anchor, result.SearchTerms[0].Role);
        Assert.Equal(0, result.SearchTerms[0].AnchorGroup);
        Assert.Contains(result.SearchTerms, term =>
            term.Text == "2026" &&
            term.Role == SearchTermRole.Anchor &&
            term.AnchorGroup == 1);
        Assert.Contains(result.SearchTerms, term =>
            term.Role == SearchTermRole.Context &&
            term.Weight == 0.35);
        Assert.Contains("2026-07-31", Assert.Single(intentHandler.RequestBodies));
        Assert.Contains("max_completion_tokens", intentHandler.RequestBodies[0]);
        Assert.Contains("max_completion_tokens", Assert.Single(keywordHandler.RequestBodies));
    }

    [Fact]
    public async Task IntentReasoningCanBeEnabledWithoutEnablingKeywordReasoning()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "filter",
                  "target": "file",
                  "hard_extensions": ["pdf"],
                  "soft_extensions": [],
                  "folders": [],
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    { "primary": "pdf", "variants": [], "translations": [] }
                  ],
                  "phrases": [],
                  "context": []
                }
                """));
        var parser = CreateParser(
            intentHandler,
            keywordHandler,
            reasoningEffort: "default",
            keywordReasoningEffort: "none");

        var result = await parser.ParseWithGroqAsync("PDF dosyalarını göster");

        Assert.True(result.FilterOnlyMode);
        Assert.Empty(result.SearchTerms);
        using var intentDocument = JsonDocument.Parse(Assert.Single(intentHandler.RequestBodies));
        var intentRoot = intentDocument.RootElement;
        Assert.Equal("default", intentRoot.GetProperty("reasoning_effort").GetString());
        Assert.Equal("hidden", intentRoot.GetProperty("reasoning_format").GetString());
        Assert.Equal(0.6, intentRoot.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, intentRoot.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, intentRoot.GetProperty("max_completion_tokens").GetInt32());
        var intentMessages = intentRoot.GetProperty("messages");
        Assert.Equal(1, intentMessages.GetArrayLength());
        Assert.Equal("user", intentMessages[0].GetProperty("role").GetString());
        Assert.Contains("Input:", intentMessages[0].GetProperty("content").GetString());

        using var keywordDocument = JsonDocument.Parse(Assert.Single(keywordHandler.RequestBodies));
        var keywordRoot = keywordDocument.RootElement;
        Assert.Equal("none", keywordRoot.GetProperty("reasoning_effort").GetString());
        Assert.False(keywordRoot.TryGetProperty("reasoning_format", out _));
        Assert.False(keywordRoot.TryGetProperty("top_p", out _));
        Assert.Equal(0.3, keywordRoot.GetProperty("temperature").GetDouble());
        Assert.Equal(350, keywordRoot.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(2, keywordRoot.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData("openai/gpt-oss-20b", "low")]
    [InlineData("openai/gpt-oss-20b", "medium")]
    [InlineData("openai/gpt-oss-20b", "high")]
    [InlineData("openai/gpt-oss-120b", "low")]
    [InlineData("openai/gpt-oss-120b", "medium")]
    [InlineData("openai/gpt-oss-120b", "high")]
    public async Task OssReasoningProfilesApplyOnlyToIntent(
        string model,
        string reasoningEffort)
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "keyword",
                  "target": "file",
                  "hard_extensions": [],
                  "soft_extensions": ["pdf"],
                  "folders": [],
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    { "primary": "bilet", "variants": ["biletler"], "translations": [] }
                  ],
                  "phrases": [],
                  "context": []
                }
                """));
        var parser = CreateParser(
            intentHandler,
            keywordHandler,
            reasoningEffort,
            keywordReasoningEffort: "none",
            model: model,
            keywordModel: "qwen/qwen3.6-27b");

        await parser.ParseWithGroqAsync("bu yaza ait biletler");

        using var intentDocument = JsonDocument.Parse(Assert.Single(intentHandler.RequestBodies));
        var intentRoot = intentDocument.RootElement;
        Assert.Equal(model, intentRoot.GetProperty("model").GetString());
        Assert.Equal(reasoningEffort, intentRoot.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1.0, intentRoot.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, intentRoot.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, intentRoot.GetProperty("max_completion_tokens").GetInt32());

        using var keywordDocument = JsonDocument.Parse(Assert.Single(keywordHandler.RequestBodies));
        var keywordRoot = keywordDocument.RootElement;
        Assert.Equal("qwen/qwen3.6-27b", keywordRoot.GetProperty("model").GetString());
        Assert.Equal("none", keywordRoot.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task CompoundProfileEnablesToolsAndLatestVersionHeader()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "keyword",
                  "target": "file",
                  "hard_extensions": [],
                  "soft_extensions": ["pdf"],
                  "folders": [],
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    { "primary": "bilet", "variants": ["biletler"], "translations": [] }
                  ],
                  "phrases": [],
                  "context": []
                }
                """));
        var parser = CreateParser(
            intentHandler,
            keywordHandler,
            model: "groq/compound",
            keywordModel: "qwen/qwen3.6-27b");

        await parser.ParseWithGroqAsync("bu yaza ait biletler");

        using var document = JsonDocument.Parse(Assert.Single(intentHandler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal("groq/compound", root.GetProperty("model").GetString());
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, root.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.Equal(1, root.GetProperty("messages").GetArrayLength());
        var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("summer Jun 1-Sep 1", prompt);
        Assert.Contains("when unspecified, use created dates only", prompt);
        Assert.Contains("concept requires keyword mode", prompt);
        Assert.DoesNotContain("Examples:", prompt);
        var tools = root
            .GetProperty("compound_custom")
            .GetProperty("tools")
            .GetProperty("enabled_tools")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Equal(new string?[] { "code_interpreter" }, tools);
        Assert.Equal("latest", Assert.Single(intentHandler.ModelVersions));
    }

    [Fact]
    public async Task LlamaProfileUsesJsonModeWithoutReasoning()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "keyword",
                  "target": "file",
                  "hard_extensions": [],
                  "soft_extensions": ["pdf"],
                  "folders": [],
                  "open": false
                }
                """));
        var parser = CreateParser(
            intentHandler,
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "anchors": [
                        { "primary": "bilet", "variants": ["biletler"], "translations": [] }
                      ],
                      "phrases": [],
                      "context": []
                    }
                    """)),
            model: "llama-3.3-70b-versatile",
            keywordModel: "qwen/qwen3.6-27b");

        await parser.ParseWithGroqAsync("bu yaza ait biletler");

        using var document = JsonDocument.Parse(Assert.Single(intentHandler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal("llama-3.3-70b-versatile", root.GetProperty("model").GetString());
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble());
        Assert.Equal(1.0, root.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("messages").GetArrayLength());
        Assert.Empty(intentHandler.ModelVersions);
    }

    [Fact]
    public async Task OpenActionRequiresExplicitOpenVerb()
    {
        var intentJson = """
            {
              "mode": "keyword",
              "target": "folder",
              "hard_extensions": [],
              "soft_extensions": [],
              "folders": ["Downloads"],
              "created_from": null,
              "created_to_exclusive": null,
              "modified_from": null,
              "modified_to_exclusive": null,
              "min_mb": null,
              "max_mb": null,
              "open": true
            }
            """;
        var keywordJson = """
            {
              "anchors": [
                { "primary": "Downloads", "variants": ["indirilenler"], "translations": [] }
              ],
              "phrases": [],
              "context": []
            }
            """;

        var showParser = CreateParser(
            new QueueHttpMessageHandler(() => Completion(intentJson)),
            new QueueHttpMessageHandler(() => Completion(keywordJson)));
        var openParser = CreateParser(
            new QueueHttpMessageHandler(() => Completion(intentJson)),
            new QueueHttpMessageHandler(() => Completion(keywordJson)));

        var showResult = await showParser.ParseWithGroqAsync("Downloads klasörünü göster");
        var openResult = await openParser.ParseWithGroqAsync("Downloads klasörünü aç");

        Assert.False(showResult.OpenAction?.ShouldOpen);
        Assert.True(openResult.OpenAction?.ShouldOpen);
    }

    [Fact]
    public async Task InvalidIntentResponseUsesRuleBasedFallback()
    {
        var parser = CreateParser(
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "mode": "unknown",
                      "target": "file",
                      "hard_extensions": [],
                      "soft_extensions": [],
                      "folders": [],
                      "open": false
                    }
                    """)),
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "anchors": [
                        { "primary": "bütçe", "variants": [], "translations": [] }
                      ],
                      "phrases": [],
                      "context": []
                    }
                    """)));

        var result = await parser.ParseWithGroqAsync("bütçe pdf dosyasını bul");

        Assert.True(result.UsedFallback);
        Assert.Contains("mode", result.FallbackReason);
        Assert.Contains("bütçe", result.Keywords);
        Assert.Contains(".pdf", result.HardExtensions);
    }

    [Fact]
    public async Task RetriableHttpFailureIsRetriedOnce()
    {
        var intentHandler = new QueueHttpMessageHandler(
            TooManyRequests,
            () => Completion("""
                {
                  "mode": "filter",
                  "target": "file",
                  "hard_extensions": ["pdf"],
                  "soft_extensions": [],
                  "folders": ["Desktop"],
                  "created_from": null,
                  "created_to_exclusive": null,
                  "modified_from": null,
                  "modified_to_exclusive": null,
                  "min_mb": null,
                  "max_mb": null,
                  "open": false
                }
                """));
        var parser = CreateParser(
            intentHandler,
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "anchors": [],
                      "phrases": [],
                      "context": []
                    }
                    """)));

        var result = await parser.ParseWithGroqAsync("masaüstündeki PDF'leri göster");

        Assert.False(result.UsedFallback);
        Assert.True(result.FilterOnlyMode);
        Assert.Equal(2, intentHandler.RequestBodies.Count);
        Assert.Empty(result.SearchTerms);
    }

    [Fact]
    public async Task KeywordHttpFailureKeepsIntentAndUsesRuleBasedTerms()
    {
        var parser = CreateParser(
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "mode": "keyword",
                      "target": "file",
                      "hard_extensions": [],
                      "soft_extensions": ["pdf"],
                      "folders": [],
                      "created_from": null,
                      "created_to_exclusive": null,
                      "modified_from": null,
                      "modified_to_exclusive": null,
                      "min_mb": null,
                      "max_mb": null,
                      "open": false
                    }
                    """)),
            new QueueHttpMessageHandler(
                () => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                }));

        var result = await parser.ParseWithGroqAsync("bütçe raporu");

        Assert.False(result.UsedFallback);
        Assert.Contains("400", result.WarningMessage);
        Assert.Contains("bütçe", result.Keywords);
        Assert.NotEmpty(result.SearchTerms);
    }

    [Fact]
    public async Task SummerTicketResponseMapsRequiredAnchorAndRankingContext()
    {
        var intentHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "mode": "filter",
                  "target": "file",
                  "hard_extensions": [],
                  "soft_extensions": ["pdf", "jpg", "png"],
                  "folders": [],
                  "created_from": "2026-06-01",
                  "created_to_exclusive": "2026-09-01",
                  "open": false
                }
                """));
        var keywordHandler = new QueueHttpMessageHandler(
            () => Completion("""
                {
                  "anchors": [
                    {
                      "primary": "bilet",
                      "variants": ["biletler", "biletleri", "bileti"],
                      "translations": ["ticket", "tickets"]
                    }
                  ],
                  "phrases": ["yaz dönemi bilet", "yaz bilet"],
                  "context": ["yaz dönemi", "yaz"]
                }
                """));
        var parser = CreateParser(intentHandler, keywordHandler);

        var result = await parser.ParseWithGroqAsync("yaz dönemine ait biletler");

        Assert.False(result.FilterOnlyMode);
        var primary = Assert.Single(result.SearchTerms, term =>
            term.Text == "bilet" && term.Category == SearchTermCategory.Exact);
        Assert.Equal(SearchTermRole.Anchor, primary.Role);
        Assert.Equal(0, primary.AnchorGroup);
        Assert.Contains(result.SearchTerms, term =>
            term.Text == "ticket" && term.Role == SearchTermRole.Anchor);
        Assert.Contains(result.SearchTerms, term =>
            term.Text == "yaz bilet" && term.Role == SearchTermRole.Phrase);
        Assert.Contains(result.SearchTerms, term =>
            term.Text == "yaz" && term.Role == SearchTermRole.Context);
        Assert.DoesNotContain(result.SearchTerms, term => term.Text == "ait");
        Assert.Equal("2026-06-01", result.DateFilter?.CreatedAfter);
        Assert.Equal("2026-09-01", result.DateFilter?.CreatedBeforeExclusive);

        using var request = JsonDocument.Parse(Assert.Single(keywordHandler.RequestBodies));
        var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("primary", prompt);
        Assert.Contains("bilet", prompt);
        Assert.Contains("Use zero only when metadata filters fully satisfy", prompt);
    }

    [Fact]
    public async Task MissingAnchorUsesConservativeRuleBasedFallback()
    {
        var parser = CreateParser(
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "mode": "keyword",
                      "target": "file",
                      "hard_extensions": [],
                      "soft_extensions": ["pdf"],
                      "folders": [],
                      "created_from": "2026-06-01",
                      "created_to_exclusive": "2026-09-01",
                      "open": false
                    }
                    """)),
            new QueueHttpMessageHandler(
                () => Completion("""
                    {
                      "anchors": [],
                      "phrases": ["yaz dönemi"],
                      "context": ["yaz", "dönemi"]
                    }
                    """)));

        var result = await parser.ParseWithGroqAsync("yaz dönemine ait biletler");

        var term = Assert.Single(result.SearchTerms);
        Assert.Equal("bilet", term.Text);
        Assert.Equal(SearchTermRole.Anchor, term.Role);
        Assert.Equal(0, term.AnchorGroup);
        Assert.Contains("kullanılabilir arama terimi", result.WarningMessage);
    }

    private static IntentParser CreateParser(
        QueueHttpMessageHandler intentHandler,
        QueueHttpMessageHandler keywordHandler,
        string reasoningEffort = "none",
        string? keywordReasoningEffort = null,
        string model = "qwen/qwen3.6-27b",
        string? keywordModel = null) =>
        new(
            new HttpClient(intentHandler),
            new HttpClient(keywordHandler),
            nowProvider: () => new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            timeZone: TimeZoneInfo.Utc,
            reasoningEffort: reasoningEffort,
            keywordReasoningEffort: keywordReasoningEffort,
            model: model,
            keywordModel: keywordModel);

    private static HttpResponseMessage Completion(string content)
    {
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { content }
                }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage TooManyRequests()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private sealed class QueueHttpMessageHandler(
        params Func<HttpResponseMessage>[] responseFactories) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responseFactories);

        public List<string> RequestBodies { get; } = [];
        public List<string> ModelVersions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.Headers.TryGetValues("Groq-Model-Version", out var values))
            {
                ModelVersions.AddRange(values);
            }

            return _responses.Dequeue()();
        }
    }
}
