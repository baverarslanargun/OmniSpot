using System.Diagnostics;
using System.Text.Json;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;

const string qwenModel = "qwen/qwen3.8-27b";
const string keywordReasoningEffort = "medium";

var model = qwenModel;
var reasoningEffort = "medium";
var queryArguments = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (string.Equals(
            args[index],
            "--reasoning-effort",
            StringComparison.OrdinalIgnoreCase) &&
        index + 1 < args.Length)
    {
        reasoningEffort = args[++index];
    }
    else if (string.Equals(
                 args[index],
                 "--model",
                 StringComparison.OrdinalIgnoreCase) &&
             index + 1 < args.Length)
    {
        model = args[++index];
    }
    else
    {
        queryArguments.Add(args[index]);
    }
}

var input = string.Join(" ", queryArguments).Trim();
if (input.Length == 0)
{
    Console.Error.WriteLine(
        "Kullanım: PromptProbe [--model model] [--reasoning-effort effort] <sorgu>");
    return 1;
}

var stopwatch = Stopwatch.StartNew();
var parser = new IntentParser(
    reasoningEffort: reasoningEffort,
    keywordReasoningEffort: keywordReasoningEffort,
    model: model,
    keywordModel: qwenModel);
var result = await parser.ParseWithGroqAsync(input);
stopwatch.Stop();

var target = result.TargetType switch
{
    null => null,
    { File: >= 0.7 } => "file",
    { Folder: >= 0.7 } => "folder",
    _ => "both"
};

var output = new
{
    query = input,
    model = new
    {
        intent = model,
        keyword = qwenModel
    },
    reasoning_effort = new
    {
        intent = reasoningEffort,
        keyword = keywordReasoningEffort
    },
    elapsed_ms = stopwatch.ElapsedMilliseconds,
    used_fallback = result.UsedFallback,
    fallback_reason = result.FallbackReason,
    warning = result.WarningMessage,
    intent = new
    {
        mode = result.FilterOnlyMode ? "filter" : "keyword",
        target,
        hard_extensions = result.HardExtensions,
        soft_extensions = result.SoftExtensions,
        folders = result.FolderHints.Select(folder => new
        {
            name = folder.Name,
            weight = folder.Weight
        }),
        created_from = result.DateFilter?.CreatedAfter,
        created_to_exclusive = result.DateFilter?.CreatedBeforeExclusive,
        modified_from = result.DateFilter?.ModifiedAfter,
        modified_to_exclusive = result.DateFilter?.ModifiedBeforeExclusive,
        min_mb = result.SizeFilter?.MinMb,
        max_mb = result.SizeFilter?.MaxMb,
        open = result.OpenAction?.ShouldOpen ?? false
    },
    folder_context = result.FolderContextTerms.Select(term => new { text = term.Text, group = term.AnchorGroup }),
    search_terms = result.SearchTerms.Select(term => new
    {
        text = term.Text,
        category = term.Category.ToString().ToLowerInvariant(),
        role = term.Role.ToString().ToLowerInvariant(),
        anchor_group = term.Role == SearchTermRole.Anchor
            ? term.AnchorGroup
            : (int?)null,
        weight = term.Weight
    })
};

Console.WriteLine(JsonSerializer.Serialize(
    output,
    new JsonSerializerOptions { WriteIndented = true }));
return 0;
