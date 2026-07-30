using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

/// <summary>
/// Intent parser that supports both rule-based and Groq AI parsing.
/// Uses two separate API calls: Intent analysis + Keyword generation (if needed).
/// </summary>
public class IntentParser
{
    // Intent API - for metadata analysis (filter_only_mode, extensions, domain_tags, etc.)
    private const string IntentApiKeyEnvironmentVariable = "OMNISPOT_GROQ_INTENT_API_KEY";
    // Keyword API - for keyword generation (only called when filter_only_mode=false)
    private const string KeywordApiKeyEnvironmentVariable = "OMNISPOT_GROQ_KEYWORD_API_KEY";
    // Shared fallback for installations that use the same Groq key for both requests.
    private const string SharedApiKeyEnvironmentVariable = "OMNISPOT_GROQ_API_KEY";

    private const string GroqApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    
    private readonly HttpClient _intentHttpClient;
    private readonly HttpClient _keywordHttpClient;
    private readonly Action<string>? _logger;
    private readonly object _httpLock = new object(); // Thread safety for HTTP client

    // File type mappings
    private static readonly Dictionary<string, string[]> FileTypePatterns = new()
    {
        ["video"] = new[] { "video", "film", "movie", "clip", "mp4", "avi", "mkv", "mov", "wmv" },
        ["image"] = new[] { "image", "picture", "photo", "pic", "jpg", "jpeg", "png", "gif", "bmp" },
        ["document"] = new[] { "document", "doc", "pdf", "text", "txt", "docx", "rtf", "odt" },
        ["audio"] = new[] { "audio", "music", "song", "sound", "mp3", "wav", "flac", "ogg" },
        ["code"] = new[] { "code", "source", "script", "cs", "js", "py", "cpp", "java", "html" },
        ["subtitle"] = new[] { "subtitle", "sub", "srt", "vtt", "ass" }
    };

    // Extension mappings
    private static readonly Dictionary<string, string[]> ExtensionMappings = new()
    {
        ["video"] = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".m4v", ".3gp" },
        ["image"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".svg", ".webp" },
        ["document"] = new[] { ".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".ppt", ".pptx" },
        ["audio"] = new[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a" },
        ["code"] = new[] { ".cs", ".js", ".py", ".cpp", ".java", ".html", ".css", ".xml", ".json", ".sql" },
        ["subtitle"] = new[] { ".srt", ".vtt", ".ass", ".sub" }
    };

    // Common stopwords to filter out
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "for", "to", "of", "in", "on", "at",
        "with", "show", "me", "my", "all", "please", "find", "search", "files", 
        "file", "get", "give", "list", "display", "open", "look", "see"
    };

    public IntentParser(Action<string>? logger = null)
    {
        _logger = logger;

        var sharedApiKey = Environment.GetEnvironmentVariable(SharedApiKeyEnvironmentVariable);
        var intentApiKey = Environment.GetEnvironmentVariable(IntentApiKeyEnvironmentVariable) ?? sharedApiKey;
        var keywordApiKey = Environment.GetEnvironmentVariable(KeywordApiKeyEnvironmentVariable) ?? sharedApiKey;
        
        // Initialize Intent HTTP client
        _intentHttpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(intentApiKey))
            _intentHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {intentApiKey}");
        
        // Initialize Keyword HTTP client
        _keywordHttpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(keywordApiKey))
            _keywordHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {keywordApiKey}");

        if (string.IsNullOrWhiteSpace(intentApiKey) || string.IsNullOrWhiteSpace(keywordApiKey))
            Log("[IntentParser] Groq API key is not configured; API failures will use rule-based fallback.");
        
        Log("[IntentParser] ✅ Parser initialized (Rule-based + Dual Groq API support)");
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        _logger?.Invoke(message);
    }

    /// <summary>
    /// Parse natural language query using Groq AI.
    /// Uses PARALLEL approach: Intent and Keyword APIs run simultaneously.
    /// If filter_only_mode=true, keyword results are discarded.
    /// Handles API failures gracefully - Intent failure triggers fallback, Keyword failure is tolerated.
    /// </summary>
    public async Task<StructuredQuery> ParseWithGroqAsync(string query, CancellationToken cancellationToken = default)
    {
        string? fallbackReason = null;
        string? warningMessage = null;
        
        try
        {
            // Check cancellation early
            cancellationToken.ThrowIfCancellationRequested();
            
            Log($"[IntentParser] 🚀 Starting PARALLEL API calls for: '{query}'");

            // Use Task-based approach with proper result capture (no shared closures for thread safety)
            var intentTask = CallIntentApiWithErrorHandlingAsync(query, cancellationToken);
            var keywordTask = CallKeywordApiWithErrorHandlingAsync(query, cancellationToken);
            
            // Wait for BOTH to complete (even if one fails) - CRITICAL for synchronization
            Log($"[IntentParser] ⏳ Waiting for both API calls to complete...");
            await Task.WhenAll(intentTask, keywordTask);
            Log($"[IntentParser] ✅ Both API calls completed!");
            
            // Get results from completed tasks (thread-safe)
            var (intentResult, intentError) = await intentTask;
            var (keywordResult, keywordError) = await keywordTask;
            
            // Debug: Log what we received from both APIs
            Log($"[IntentParser] 📊 Intent result: {(intentResult != null ? "OK" : "NULL")}, error: {(intentError != null ? intentError.Message : "none")}");
            Log($"[IntentParser] 📊 Keyword result: {(keywordResult != null ? $"{keywordResult.Count} keywords" : "NULL")}, error: {(keywordError != null ? keywordError.Message : "none")}");
            
            // Check for user cancellation
            cancellationToken.ThrowIfCancellationRequested();
            
            // CRITICAL: Intent API failure triggers fallback
            if (intentError != null)
            {
                if (intentError is OperationCanceledException)
                    fallbackReason = "Intent API zaman aşımı (30 saniye)";
                else if (intentError is HttpRequestException httpEx)
                    fallbackReason = $"Intent API bağlantı hatası: {httpEx.Message}";
                else
                    fallbackReason = $"Intent API hatası: {intentError.Message}";
                    
                throw new Exception(fallbackReason, intentError);
            }
            
            if (intentResult == null)
            {
                fallbackReason = "Intent API yanıtı boş veya geçersiz";
                throw new Exception(fallbackReason);
            }
            
            Log($"[IntentParser] ✅ Intent analysis complete. filter_only_mode={intentResult.FilterOnlyMode}");
            
            // Keyword API failure is tolerated - just log warning
            if (keywordError != null)
            {
                if (keywordError is OperationCanceledException)
                    warningMessage = "Keyword API zaman aşımı - basit arama yapılıyor";
                else if (keywordError is HttpRequestException httpEx)
                    warningMessage = $"Keyword API bağlantı hatası - basit arama yapılıyor";
                else
                    warningMessage = $"Keyword API hatası - basit arama yapılıyor";
                    
                Log($"[IntentParser] ⚠️ {warningMessage}");
            }
            
            // Map intent result to StructuredQuery
            var result = MapIntentResultToStructuredQuery(intentResult);
            
            // Use keyword results only if filter_only_mode is false AND keyword API succeeded
            if (!intentResult.FilterOnlyMode && keywordError == null && keywordResult != null && keywordResult.Count > 0)
            {
                // Filter keywords by weight and extract tokens
                var filteredKeywords = keywordResult
                    .Where(k => k.Weight > 0.3)
                    .OrderByDescending(k => k.Weight)
                    .Select(k => k.Token)
                    .ToList();
                
                if (filteredKeywords.Count > 0)
                {
                    result.Keywords = filteredKeywords;
                    Log($"[IntentParser] ✅ Using {result.Keywords.Count} keywords (filtered from {keywordResult.Count} raw keywords).");
                }
                else
                {
                    // Keywords exist but all filtered out due to low weight
                    Log($"[IntentParser] ⚠️ All {keywordResult.Count} keywords filtered out (weight < 0.3)!");
                    result.Keywords = new List<string> { query };
                    result.WarningMessage = "Anahtar kelimeler düşük güvenilirlik nedeniyle filtrelendi - sorgu metni ile devam edildi";
                }
            }
            else if (intentResult.FilterOnlyMode)
            {
                Log("[IntentParser] 🔍 Filter-only mode: discarding keyword results.");
                result.Keywords = new List<string>();
            }
            else if (keywordError != null)
            {
                Log("[IntentParser] ⚠️ Using empty keywords due to Keyword API failure.");
                result.Keywords = new List<string> { query };
                result.WarningMessage = warningMessage ?? "Keyword API hatası - sorgu metni ile devam edildi";
            }
            else
            {
                // filter_only_mode=false ama keyword üretilemedi - bu bir sorun!
                Log($"[IntentParser] ⚠️ No keywords generated despite filter_only_mode=false! keywordResult={(keywordResult == null ? "NULL" : $"Count={keywordResult.Count}")}");
                result.Keywords = new List<string> { query };
                result.WarningMessage = "Anahtar kelimeler üretilemedi - sorgu metni ile devam edildi";
            }
            
            result.UsedFallback = false;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("[IntentParser] 🚫 Request cancelled by user");
            throw;
        }
        catch (OperationCanceledException)
        {
            Log("[IntentParser] ⏱️ Groq API timeout, falling back to rule-based parser");
            fallbackReason = "API zaman aşımı (30 saniye)";
        }
        catch (HttpRequestException ex)
        {
            Log($"[IntentParser] ❌ HTTP error: {ex.Message}");
            fallbackReason = $"Bağlantı hatası: {ex.Message}";
        }
        catch (Exception ex)
        {
            Log($"[IntentParser] ❌ Groq API error: {ex.Message}");
            // Use the fallback reason if it was already set, otherwise use exception message
            fallbackReason = fallbackReason ?? $"Beklenmeyen hata: {ex.Message}";
        }

        Log("[IntentParser] ⚠️ Falling back to rule-based parser.");
        var fallbackResult = ParseIntent(query);
        fallbackResult.UsedFallback = true;
        fallbackResult.FallbackReason = fallbackReason ?? "Bilinmeyen hata";
        return fallbackResult;
    }

    /// <summary>
    /// Thread-safe wrapper for Intent API call - returns tuple with result or error
    /// </summary>
    private async Task<(GroqIntentResult? Result, Exception? Error)> CallIntentApiWithErrorHandlingAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CallIntentApiAsync(query, cancellationToken);
            return (result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Rethrow user cancellation
        }
        catch (Exception ex)
        {
            Log($"[IntentParser] ❌ Intent API failed: {ex.Message}");
            return (null, ex);
        }
    }
    
    /// <summary>
    /// Thread-safe wrapper for Keyword API call - returns tuple with result or error
    /// </summary>
    private async Task<(List<GroqKeyword>? Result, Exception? Error)> CallKeywordApiWithErrorHandlingAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CallKeywordApiAsync(query, null, cancellationToken);
            return (result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Rethrow user cancellation
        }
        catch (Exception ex)
        {
            Log($"[IntentParser] ❌ Keyword API failed: {ex.Message}");
            return (null, ex);
        }
    }

    /// <summary>
    /// Call the Intent API (Phase 1) - Analyzes query for filters, extensions, domain tags, etc.
    /// </summary>
    private async Task<GroqIntentResult?> CallIntentApiAsync(string query, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = "meta-llama/llama-4-maverick-17b-128e-instruct",
            messages = new[]
            {
                new { role = "system", content = IntentSystemPrompt },
                new { role = "user", content = query }
            },
            temperature = 0.3,
            response_format = new { type = "json_object" }
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        var response = await _intentHttpClient.PostAsync(GroqApiUrl, jsonContent, linkedCts.Token);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(linkedCts.Token);
            Log($"[IntentParser] ❌ Intent API Error ({response.StatusCode}): {errorContent}");
            return null;
        }
        
        var responseString = await response.Content.ReadAsStringAsync(linkedCts.Token);
        var groqResponse = JsonSerializer.Deserialize<GroqApiResponse>(responseString);
        
        if (groqResponse?.Choices != null && groqResponse.Choices.Length > 0)
        {
            var content = groqResponse.Choices[0].Message.Content;
            Log($"[IntentParser] 📥 Intent API response received ({content.Length} chars)");
            return JsonSerializer.Deserialize<GroqIntentResult>(content);
        }
        
        return null;
    }

    /// <summary>
    /// Call the Keyword API - Generates keywords. Can run in parallel with Intent API.
    /// </summary>
    private async Task<List<GroqKeyword>?> CallKeywordApiAsync(string query, GroqIntentResult? intentResult, CancellationToken cancellationToken)
    {
        // Build context for keyword generation (if intent result available, otherwise use defaults)
        var contextInfo = new
        {
            language = intentResult?.Language ?? "other",
            domain_tags = intentResult?.DomainTags ?? new List<string>(),
            extensions = intentResult?.Extensions?.Select(e => e.Ext).ToList() ?? new List<string>()
        };
        
        var userPrompt = $"Context: {JsonSerializer.Serialize(contextInfo)}\n\nQuery: {query}";
        
            var requestBody = new
            {
                model = "meta-llama/llama-4-scout-17b-16e-instruct",
                messages = new[]
                {
                    new { role = "system", content = KeywordSystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.3,
                response_format = new { type = "json_object" }
            };

        var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        var response = await _keywordHttpClient.PostAsync(GroqApiUrl, jsonContent, linkedCts.Token);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(linkedCts.Token);
            Log($"[IntentParser] ❌ Keyword API Error ({response.StatusCode}): {errorContent}");
            return null;
        }
        
        var responseString = await response.Content.ReadAsStringAsync(linkedCts.Token);
        var groqResponse = JsonSerializer.Deserialize<GroqApiResponse>(responseString);
        
        if (groqResponse?.Choices != null && groqResponse.Choices.Length > 0)
        {
            var content = groqResponse.Choices[0].Message.Content;
            Log($"[IntentParser] 📥 Keyword API response received ({content.Length} chars)");
            
            // Debug: Log first 500 chars of response
            Log($"[IntentParser] 🔍 Response preview: {content.Substring(0, Math.Min(500, content.Length))}...");
            
            // Try to parse as keyword wrapper first (for json_object mode)
            try
            {
                var wrapper = JsonSerializer.Deserialize<GroqKeywordWrapper>(content);
                if (wrapper?.Keywords != null && wrapper.Keywords.Count > 0)
                {
                    Log($"[IntentParser] ✅ Parsed {wrapper.Keywords.Count} keywords from wrapper");
                    return wrapper.Keywords;
                }
                else if (wrapper?.Keywords != null)
                {
                    Log($"[IntentParser] ⚠️ Wrapper parsed but empty keywords");
                }
            }
            catch (Exception ex) 
            { 
                Log($"[IntentParser] ⚠️ Wrapper parse failed: {ex.Message}");
            }
            
            // Try to parse as direct array
            try
            {
                var keywords = JsonSerializer.Deserialize<List<GroqKeyword>>(content);
                if (keywords != null && keywords.Count > 0)
                {
                    Log($"[IntentParser] ✅ Parsed {keywords.Count} keywords from array");
                    return keywords;
                }
                else if (keywords != null)
                {
                    Log($"[IntentParser] ⚠️ Array parsed but empty (0 keywords)");
                    return keywords; // Return empty list instead of null
                }
            }
            catch (Exception ex) 
            { 
                Log($"[IntentParser] ⚠️ Array parse failed: {ex.Message}");
            }
            
            Log($"[IntentParser] ❌ Could not parse keywords from response - returning empty list");
        }
        
        return new List<GroqKeyword>(); // Return empty list instead of null
    }

    /// <summary>
    /// Maps Intent API result to StructuredQuery (without keywords).
    /// </summary>
    private StructuredQuery MapIntentResultToStructuredQuery(GroqIntentResult intent)
    {
        if (intent == null) return CreateDefaultQuery();

        var sq = new StructuredQuery
        {
            Intent = intent.Intent ?? "search_files",
            IncludeFolderContents = true, // Default to true as per prompt rules
            SortBy = intent.Priority == "quality" ? "relevance" : "modified_desc", // Simple mapping
            FilterOnlyMode = intent.FilterOnlyMode // Important: preserve filter-only mode from AI
        };

        // Keywords will be set later from Keyword API response
        sq.Keywords = new List<string>();

        // Map extensions
        if (intent.Extensions != null)
        {
            sq.PredictedExtensions = intent.Extensions
                .Where(e => e.Weight > 0.4)
                .Select(e => e.Ext.TrimStart('.'))
                .ToList();
        }

        // Map date filters
        if (intent.DateFilters != null)
        {
            var df = new DateFilter();
            bool hasDateFilter = false;
            
            if (intent.DateFilters.Created != null && intent.DateFilters.Created.Confidence > 0.5)
            {
                df.CreatedAfter = intent.DateFilters.Created.From;
                df.CreatedBefore = intent.DateFilters.Created.To;
                hasDateFilter = true;
            }
            
            if (intent.DateFilters.Modified != null && intent.DateFilters.Modified.Confidence > 0.5)
            {
                df.ModifiedAfter = intent.DateFilters.Modified.From;
                df.ModifiedBefore = intent.DateFilters.Modified.To;
                hasDateFilter = true;
            }

            if (hasDateFilter)
            {
                sq.DateFilter = df;
            }
        }

        // Map file types from domain tags
        if (intent.DomainTags != null)
        {
            sq.FileTypes = intent.DomainTags.ToList();
        }

        // Map size filter
        if (intent.SizeFilter != null && (intent.SizeFilter.MinMb.HasValue || intent.SizeFilter.MaxMb.HasValue))
        {
            sq.SizeFilter = new SizeFilter
            {
                MinMb = intent.SizeFilter.MinMb,
                MaxMb = intent.SizeFilter.MaxMb
            };
        }

        // Map folder hints
        if (intent.FolderHints != null)
        {
            sq.FolderHints = intent.FolderHints
                .Select(h => new FolderHint { Name = h.Name, Weight = h.Weight })
                .ToList();
        }

        // Map open action
        if (intent.OpenAction != null)
        {
            sq.OpenAction = new OpenAction
            {
                ShouldOpen = intent.OpenAction.ShouldOpen,
                OpenMode = intent.OpenAction.OpenMode
            };
        }

        // Map target type (file vs folder preference)
        if (intent.TargetType != null)
        {
            sq.TargetType = new TargetType
            {
                File = intent.TargetType.File,
                Folder = intent.TargetType.Folder
            };
        }

        return sq;
    }

    /// <summary>
    /// Parse natural language query into structured search parameters (Rule-based).
    /// </summary>
    public StructuredQuery ParseIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return CreateDefaultQuery();
        }

        Log($"[IntentParser] Parsing query: '{query}' (rule-based)");

        var result = new StructuredQuery
        {
            Intent = "search_files",
            Keywords = ExtractKeywords(query),
            FileTypes = DetectFileTypes(query),
            PredictedExtensions = new List<string>(),
            IncludeFolderContents = true,
            DateFilter = null,
            SortBy = "relevance"
        };

        // Add predicted extensions based on detected file types
        foreach (var fileType in result.FileTypes)
        {
            if (ExtensionMappings.TryGetValue(fileType, out var extensions))
            {
                result.PredictedExtensions.AddRange(extensions);
            }
        }

        // Remove duplicates
        result.PredictedExtensions = result.PredictedExtensions.Distinct().ToList();

        Log($"[IntentParser] ✅ Parsed: {result.Keywords.Count} keywords, {result.FileTypes.Count} file types, {result.PredictedExtensions.Count} extensions");

        return result;
    }

    // ============================================================
    // INTENT SYSTEM PROMPT - Analyzes query for metadata/filters
    // Source reference: docs/prompts/metadata-analyzer.txt
    // ============================================================
    private const string IntentSystemPrompt = """
# AGENT 1: METADATA ANALYZER

You are the metadata analyzer for OmniSpot desktop launcher.

Your ONLY job:
- Receive ONE natural-language query from the user
- Analyze intent, target type, filters, and context
- Output ONE valid JSON object (WITHOUT keywords field)
- Keywords will be generated by a separate specialized agent

You NEVER generate keywords. You NEVER access files or open anything.

========================================================
= SCOPE ================================================
========================================================

OmniSpot is a file and folder search + launcher.
User queries always relate to finding or opening FILES or FOLDERS on disk.

You must decide:

1. **intent**
   - "search_files"
   - "search_folders"
   - "search_both"
   - "open_best_match"

2. **filter_only_mode** (CRITICAL!)
   - true: User wants ALL files matching filters, NO keyword search needed
   - false: User wants to find SPECIFIC files, keywords will be needed

3. **target_type**
   - Probabilities for what user is seeking:
     - target_type.file   ∈ [0.0, 1.0]
     - target_type.folder ∈ [0.0, 1.0]

4. **open_action**
   - open_action.should_open: true/false
   - open_action.open_mode: "single_best" or "show_list"

5. **Filters and signals:**
   - likely file extensions
   - domain tags
   - date filters
   - size hints
   - folder hints
   - priority

========================================================
= FILTER-ONLY MODE (VERY RESTRICTED) ===================
========================================================

User is NOT searching by keywords for a specific file, but wants to BROWSE
all files of a GENERIC TYPE (images, videos, PDFs, documents, etc.),
optionally inside a FOLDER (Downloads, Desktop, …).

Use filter_only_mode = true IF AND ONLY IF:

The user intent can be fully satisfied WITHOUT guessing or matching
file/folder NAMES or CONTENT, and ONLY by applying STRUCTURED METADATA 
FILTERS such as:
- file type (extension)
- folder/location
- creation or modification date
- size

IMPORTANT:
- Years, dates, and temporal expressions (e.g. "2025", "last year")
  DO NOT automatically break filter_only_mode
  IF they can be represented purely as date_filters.

filter_only_mode MUST be false ONLY WHEN:
- The user is implicitly or explicitly searching by
  file/folder NAME, TITLE, TOPIC, CONCEPT, BRAND, or CONTENT.

**Patterns where filter_only_mode = true:**
- "[file type] in [folder]"
- "all photos/images/videos"
- "list/show PDFs"
- "tüm resimler", "masaüstündeki PDF'ler"

**Examples of FILTER-ONLY queries (filter_only_mode = TRUE):**
- "indirilenler klasörü içindeki tüm resimler"
  → filter_only_mode:true, extensions:[jpg,png,...], folder_hints:[Downloads]
- "masaüstündeki PDF'ler"
  → filter_only_mode:true, extensions:[pdf], folder_hints:[Desktop]
- "bütün videolar"
  → filter_only_mode:true, extensions:[mp4,mkv,...], domain_tags:[video]
- "tüm dökümanları göster"
  → filter_only_mode:true, extensions:[pdf,docx,...]
- "all images in downloads"
  → filter_only_mode:true, extensions:[jpg,png,...], folder_hints:[Downloads]

**Examples of KEYWORD-BASED queries (filter_only_mode = FALSE):**
- "rapor.pdf dosyasını bul"
  → filter_only_mode:false, specific file name
- "meeting notes"
  → filter_only_mode:false, specific content search
- "2024 bütçe excel"
  → filter_only_mode:false, specific file with keywords
- "john birthday photos"
  → filter_only_mode:false, specific photos with keywords

**IMPORTANT EXAMPLES (ALWAYS KEYWORD MODE):**
- "tüm faturalarım"
  → "faturalarım" is a semantic category word (invoice) → filter_only_mode:false
- "fatura isimli tüm pdfler"
  → "fatura" is an explicit keyword + extension filter → filter_only_mode:false
- "indirilenler içindeki bütün 2025 dosyalar"
  → "2025" is a year/number keyword → filter_only_mode:false

========================================================
= LANGUAGE =============================================
========================================================

The query can be in Turkish, English, or mixed.

- "language": "tr" if mainly Turkish
- "language": "en" if mainly English
- Otherwise "language": "other"

========================================================
= EXTENSIONS & DOMAIN TAGS =============================
========================================================

You must predict likely file extensions based on query semantics.

**Examples:**
- Text/office/academic documents:
  - typical: "pdf", "docx", "txt"
- Images/photos:
  - "jpg", "jpeg", "png", "heic", "gif", "bmp", "webp"
- Music/audio:
  - "mp3", "wav", "flac", "m4a", "aac", "ogg"
- Videos:
  - "mp4", "mkv", "avi", "mov", "wmv", "flv"
- ROM/firmware:
  - "zip", "rar", "7z", "img", "iso", "bin"
- Design/Photoshop:
  - "psd", "psb", "ai", "sketch", "fig"

Each extension must have:
```json
{
  "ext": "string (without dot)",
  "weight": number [0.0, 1.0]
}
```

You must also include high-level **domain_tags**, such as:
- "thesis", "academic", "homework", "project"
- "music", "audio", "video"
- "photo", "image", "screenshot"
- "rom", "firmware", "game"
- "design", "code", "document"

Even when target is a FOLDER, extensions and domain_tags are still useful hints.

========================================================
= DATE FILTERS =========================================
========================================================

Interpret any temporal expressions:
- explicit years or dates
- relative expressions: "last year", "this year", "last month"
- verbs implying time: downloaded, saved, created, captured

Produce:
```json
"date_filters": {
  "created": {
    "from": "YYYY-MM-DD or null",
    "to": "YYYY-MM-DD or null",
    "confidence": number [0.0, 1.0]
  },
  "modified": {
    "from": "YYYY-MM-DD or null",
    "to": "YYYY-MM-DD or null",
    "confidence": number [0.0, 1.0]
  }
}
```

If phrasing refers to when downloaded/created: use higher confidence on "created".
If refers to recent edits: weight "modified" more.
If unclear: moderate confidence or null ranges.

========================================================
= SIZE FILTER ==========================================
========================================================

If query implies size (large ROM files, tiny images, etc.):

```json
"size_filter": {
  "min_mb": number or null,
  "max_mb": number or null,
  "confidence": number [0.0, 1.0]
}
```

If no clear size implication: keep min_mb and max_mb null, confidence low.

========================================================
= FOLDER HINTS =========================================
========================================================

Normalize folder location hints:
- desktop, documents, downloads, music, pictures, videos, etc.
- Including Turkish synonyms: masaüstü, belgeler, indirilenler, müzik, resimler, videolar

```json
"folder_hints": [
  {
    "name": "string (e.g. Desktop, Downloads, Music, Documents)",
    "weight": number [0.0, 1.0]
  }
]
```

These are hints, not hard filters.

========================================================
= PRIORITY =============================================
========================================================

"priority" should be:
- "quality" if user clearly wants exact correct item, even if slower
- "speed" if speed is more important than perfect precision
- "balanced" by default

========================================================
= JSON OUTPUT FORMAT (STRICT) ==========================
========================================================

You MUST return exactly ONE JSON object:

```json
{
  "intent": "string",
  "natural_query": "string",
  "language": "string",
  "filter_only_mode": boolean,
  
  "target_type": {
    "file": number,
    "folder": number
  },
  
  "open_action": {
    "should_open": boolean,
    "open_mode": "single_best" or "show_list"
  },
  
  "extensions": [
    {
      "ext": "string",
      "weight": number
    }
  ],
  
  "domain_tags": ["string", ...],
  
  "date_filters": {
    "created": {
      "from": "YYYY-MM-DD" or null,
      "to": "YYYY-MM-DD" or null,
      "confidence": number
    },
    "modified": {
      "from": "YYYY-MM-DD" or null,
      "to": "YYYY-MM-DD" or null,
      "confidence": number
    }
  },
  
  "size_filter": {
    "min_mb": number or null,
    "max_mb": number or null,
    "confidence": number
  },
  
  "folder_hints": [
    {
      "name": "string",
      "weight": number
    }
  ],
  
  "priority": "speed" | "quality" | "balanced",
  "notes_for_ranker": "string"
}
```

**Additional rules:**
- All numeric weights/confidences must be in [0.0, 1.0]
- "notes_for_ranker" is a short free-text note for the host app
- Do NOT include unescaped double quotes (") inside notes_for_ranker
- Extensions and domain_tags are ALWAYS required

========================================================
= FINAL CONSTRAINTS ====================================
========================================================

**CRITICAL:**
- Your ENTIRE response must be ONLY the JSON object
- No prose, no explanations, no comments, no markdown
- DO NOT include a "keywords" field - this will be handled by another agent
- If filter_only_mode is TRUE, the keyword agent will not be called
- If filter_only_mode is FALSE, your output will be sent to the keyword agent

Prompt: ""
""";

    // ============================================================
    // KEYWORD SYSTEM PROMPT - Generates keywords for file matching
    // Source reference: docs/prompts/keyword-generator.txt
    // ============================================================
    private const string KeywordSystemPrompt = """
You are the keyword generation specialist for OmniSpot desktop launcher.

Your ONLY job:
- Receive ONE natural-language query
- Receive context about the query (language, domain_tags, extensions)
- Generate MANY diverse keywords for fuzzy file/folder name matching
- Output ONE valid JSON array of keyword objects

You ONLY generate keywords.

Use this context to inform your keyword generation strategy.

========================================================
= YOUR TASK ============================================
========================================================

Generate MANY keywords (typically 30+ for queries with sufficient semantic content) and return them inside a JSON object with a single field `keywords`.

Assume humans name files and folders in messy, inconsistent ways:
- with/without spaces
- with/without accents
- mixed languages
- version numbers
- abbreviations
- misspellings
- concatenations

Your job is to predict ALL the ways a user might have named the file/folder
they're looking for.

========================================================
= KEYWORD GENERATION STRATEGY ==========================
========================================================

## 1. INTENDED PHRASE
- Add the intended file/folder name as a keyword
- This should be your FIRST and HIGHEST weighted keyword
- High importance: weight in [0.9, 1.0]
- kind: "base"

**Example:**
Query: "club rom dosyası"
→ `{"token": "club rom", "weight": 0.95, "language": "tr", "kind": "base"}`

---

## 2. TOKENIZED PARTS
- Split into meaningful words or subphrases
- Each becomes a separate keyword
- Importance: weight in [0.5, 0.9]
- kind: "base" or "variant"

**Example:**
Query: "club rom dosyası"
→ `{"token": "club", "weight": 0.85, "language": "en", "kind": "base"}`
→ `{"token": "rom", "weight": 0.80, "language": "en", "kind": "base"}`

Never generate container terms as standalone keywords.
Example: → `{"token": "dosya", "weight": 0.65, "language": "tr", "kind": "variant"}`

---

## 3. MORPHOLOGICAL / SIMPLIFIED VARIANTS
- Create shortened, stemmed, pluralized, or simplified forms
- Remove diacritics and language-specific characters
- Drop suffixes (Turkish: -ler, -lar, -si, -sı, -im, -ım, etc.)
- Informal/shortened forms
- Importance: weight in [0.4, 0.8]
- kind: "variant"

**Examples:**
- "fotoğraflar" → "fotoğraf", "fotograf", "foto"
- "müzikler" → "müzik", "muzik", "music"
- "raporlarım" → "raporlar", "rapor", "rapo"
- "kulübümüz" → "kulüb", "kulup", "club"
- "dosyası" → "dosya", "dosyam", "file"

---

## 4. CROSS-LANGUAGE TRANSLATIONS
- Generate Turkish ↔ English equivalents for meaningful words
- Importance: weight in [0.2, 0.6]
- kind: "translation"

**Examples:**
- "kulüp" ↔ "club"
- "rapor" ↔ "report"
- "tez" ↔ "thesis"
- "proje" ↔ "project"

---

## 5. HUMAN-STYLE FILENAME PATTERNS
- Simulate how humans actually name files and folders
- Importance: weight in [0.3, 0.7]
- kind: "filename_form"

**Common patterns:**
- lowercase concatenations (no spaces): "clubrom", "myphoto", "workdoc"
- underscores: "club_rom", "my_photo", "work_doc"
- hyphens: "club-rom", "my-photo", "work-doc"
- without accents: "fotograflar", "muzik", "kulup"
- versions: "v1", "v2", "final", "latest", "copy", "backup", "new", "old"
- dates embedded: "2024", "2025", "jan", "jan2025"
- numbering: "1", "2", "01", "02"

**Examples:**
Query: "club rom dosyası"
→ `{"token": "clubrom", "weight": 0.60, "language": "other", "kind": "filename_form"}`
→ `{"token": "club_rom", "weight": 0.55, "language": "other", "kind": "filename_form"}`
→ `{"token": "club-rom", "weight": 0.50, "language": "other", "kind": "filename_form"}`
→ `{"token": "clubrom.zip", "weight": 0.45, "language": "other", "kind": "filename_form"}`

Query: "2024 bütçe raporu"
→ `{"token": "2024butceraporu", "weight": 0.60, "language": "tr", "kind": "filename_form"}`
→ `{"token": "2024_butce_raporu", "weight": 0.58, "language": "tr", "kind": "filename_form"}`
→ `{"token": "butce2024", "weight": 0.55, "language": "tr", "kind": "filename_form"}`
→ `{"token": "budget_2024", "weight": 0.52, "language": "en", "kind": "filename_form"}`

---

## 6. DOMAIN-SPECIFIC KEYWORDS
- Based on semantic domain from domain_tags
- Generate several relevant tokens real users might include
- Importance: weight in [0.3, 0.7]
- kind: "domain"

**Domain patterns:**

### Academic/Thesis:
- "tez", "thesis", "bitirme", "graduation", "akademik", "academic"
- "rapor", "report", "ödev", "homework", "proje", "project"
- "sunum", "presentation", "araştırma", "research"

### Music/Audio:
- "şarkı", "song", "track", "müzik", "music", "audio"
- "albüm", "album", "playlist", "mix", "remix", "cover"
- "instrumental", "enstrümantal", "beat", "mp3"


### Photos/Images:
- "foto", "photo", "fotoğraf", "resim", "image", "picture"
- "galeri", "gallery", "album", "screenshot", "ekran"
- "kamera", "camera", "shot", "snap"

### Videos:
- "video", "film", "movie", "klip", "clip"
- "kayıt", "recording", "record", "capture"

### Documents/Office:
- "döküman", "document", "belge", "file", "dosya"
- "excel", "word", "pdf", "sheet", "table"
- "form", "template", "şablon"

**Examples:**

Query: "tez fotoğrafları"
Domain: academic, photo
→ `{"token": "thesis", "weight": 0.55, "language": "en", "kind": "domain"}`
→ `{"token": "foto", "weight": 0.60, "language": "tr", "kind": "domain"}`
→ `{"token": "resim", "weight": 0.55, "language": "tr", "kind": "domain"}`
→ `{"token": "galeri", "weight": 0.45, "language": "tr", "kind": "domain"}`

---

## 7. TEMPORAL / VERSION / CONTEXTUAL KEYWORDS
- If query implies time: generate year tokens, short year forms
- If query implies versions: "v1", "v2", "final", "son", "latest"
- If query implies context: "work", "iş", "personal", "kişisel"
- Importance: weight in [0.2, 0.6]
- kind: "variant" or "domain"

**Examples:**
Query: "2024 bütçe raporu"
→ `{"token": "2024", "weight": 0.75, "language": "other", "kind": "base"}`
→ `{"token": "24", "weight": 0.45, "language": "other", "kind": "variant"}`

Query: "son raporum"
→ `{"token": "son", "weight": 0.60, "language": "tr", "kind": "variant"}`
→ `{"token": "final", "weight": 0.50, "language": "en", "kind": "translation"}`
→ `{"token": "latest", "weight": 0.45, "language": "en", "kind": "translation"}`
→ `{"token": "last", "weight": 0.45, "language": "en", "kind": "translation"}`

========================================================
= KEYWORD OBJECT STRUCTURE =============================
========================================================

Each keyword object MUST have:

```json
{
  "token": "string (the keyword text)",
  "weight": number [0.0, 1.0],
  "language": "tr" | "en" | "other",
  "kind": "base" | "variant" | "translation" | "domain" | "filename_form"
}
```

**Kind definitions:**
- **base**: Core tokens directly from the query
- **variant**: Morphological variants, stems, simplified forms
- **translation**: Cross-language equivalents
- **domain**: Domain-specific related terms
- **filename_form**: Realistic filename concatenations/patterns

========================================================
========================================================
= OUTPUT FORMAT (STRICT JSON OBJECT) ===================
========================================================

You MUST return exactly ONE JSON OBJECT with a single field `keywords` that is an array:

```json
{
    "keywords": [
        {
            "token": "string",
            "weight": number,
            "language": "string",
            "kind": "string"
        },
        ...
    ]
}
```

**Rules:**
1. keywords array should contain 30+ keyword objects for queries with sufficient semantic content
2. First keyword should be the intended phrase with highest weight (0.9-1.0)
3. All weights must be in [0.0, 1.0]
4. Distribute keywords across all categories (base, variant, translation, domain, filename_form)
5. Your ENTIRE response must be ONLY this JSON object, nothing else (no markdown, no prose)

========================================================
= EXAMPLES =============================================
========================================================

**Example 1 (wrapped object):**

Prompt: "2025 yaz stajı proje planı ve görev dağılımı",

Output (partial - you should generate 30+):
```json
{
    "keywords": [
        {"token":"2025 yaz stajı proje planı ve görev dağılımı","weight":0.96,"language":"tr","kind":"base"},
        {"token":"yaz stajı proje planı","weight":0.90,"language":"tr","kind":"base"},
        {"token":"stajı proje planı","weight":0.88,"language":"tr","kind":"base"},
        {"token":"proje planı","weight":0.85,"language":"tr","kind":"base"},
        {"token":"görev dağılımı","weight":0.83,"language":"tr","kind":"base"},
        {"token":"proje","weight":0.80,"language":"tr","kind":"base"},
        {"token":"plan","weight":0.75,"language":"tr","kind":"variant"},
        {"token":"görev","weight":0.72,"language":"tr","kind":"variant"},
        {"token":"dagilim","weight":0.70,"language":"tr","kind":"variant"},
        {"token":"projeplani","weight":0.60,"language":"other","kind":"filename_form"},
        {"token":"proje_plani","weight":0.58,"language":"other","kind":"filename_form"},
        {"token":"gorev_dagilimi","weight":0.56,"language":"other","kind":"filename_form"},
        {"token":"2025_yaz_staji","weight":0.55,"language":"other","kind":"filename_form"},
        {"token":"summer internship project plan","weight":0.55,"language":"en","kind":"translation"},
        {"token":"project plan","weight":0.50,"language":"en","kind":"translation"},
        {"token":"task distribution","weight":0.48,"language":"en","kind":"translation"},
        {"token":"internship","weight":0.45,"language":"en","kind":"translation"},
        {"token":"roadmap","weight":0.42,"language":"en","kind":"domain"},
        {"token":"timeline","weight":0.40,"language":"en","kind":"domain"},
        {"token":"milestone","weight":0.38,"language":"en","kind":"domain"}
    ]
}
```

---

**Example 2 (wrapped object):**

Prompt: "2024 bütçe raporu"

Output (partial - you should generate 30+):
```json
{
    "keywords": [
        {"token": "2024 bütçe raporu", "weight": 0.95, "language": "tr", "kind": "base"},
        {"token": "bütçe raporu", "weight": 0.90, "language": "tr", "kind": "base"},
        {"token": "2024", "weight": 0.85, "language": "other", "kind": "base"},
        {"token": "bütçe", "weight": 0.85, "language": "tr", "kind": "base"},
        {"token": "raporu", "weight": 0.80, "language": "tr", "kind": "base"},
        {"token": "butce", "weight": 0.75, "language": "tr", "kind": "variant"},
        {"token": "rapor", "weight": 0.75, "language": "tr", "kind": "variant"},
        {"token": "budget", "weight": 0.60, "language": "en", "kind": "translation"},
        {"token": "report", "weight": 0.55, "language": "en", "kind": "translation"},
        {"token": "2024butceraporu", "weight": 0.60, "language": "tr", "kind": "filename_form"},
        {"token": "butce_2024", "weight": 0.58, "language": "tr", "kind": "filename_form"},
        {"token": "budget_2024", "weight": 0.55, "language": "en", "kind": "filename_form"},
        {"token": "24", "weight": 0.50, "language": "other", "kind": "variant"}
    ]
}
```

========================================================
= CRITICAL REMINDERS ===================================
========================================================

1. Generate AT LEAST 30 keywords for queries with sufficient content
2. First keyword = intended phrase with weight 0.9-1.0
3. Cover ALL categories: base, variant, translation, domain, filename_form
4. Think like a messy human naming files
5. Output ONLY the JSON array, nothing else
6. All weights must be [0.0, 1.0]
7. Be creative with filename patterns (no spaces, underscores, hyphens, versions)
8. Use domain_tags to inform domain-specific keywords
9. Generate cross-language variants for both Turkish and English

Prompt: ""
""";

    // DTOs for Intent API response (Phase 1)
    private class GroqIntentResult
    {
        [JsonPropertyName("intent")]
        public string? Intent { get; set; }
        
        [JsonPropertyName("natural_query")]
        public string? NaturalQuery { get; set; }
        
        [JsonPropertyName("language")]
        public string? Language { get; set; }
        
        [JsonPropertyName("filter_only_mode")]
        public bool FilterOnlyMode { get; set; } = false;
        
        [JsonPropertyName("extensions")]
        public List<GroqExtension>? Extensions { get; set; }
        
        [JsonPropertyName("domain_tags")]
        public List<string>? DomainTags { get; set; }
        
        [JsonPropertyName("date_filters")]
        public GroqDateFilters? DateFilters { get; set; }
        
        [JsonPropertyName("size_filter")]
        public GroqSizeFilter? SizeFilter { get; set; }

        [JsonPropertyName("folder_hints")]
        public List<GroqFolderHint>? FolderHints { get; set; }

        [JsonPropertyName("open_action")]
        public GroqOpenAction? OpenAction { get; set; }
        
        [JsonPropertyName("target_type")]
        public GroqTargetType? TargetType { get; set; }
        
        [JsonPropertyName("priority")]
        public string? Priority { get; set; }
        
        [JsonPropertyName("notes_for_ranker")]
        public string? NotesForRanker { get; set; }
    }

    // Wrapper for keyword API response (json_object mode returns object, not array)
    private class GroqKeywordWrapper
    {
        [JsonPropertyName("keywords")]
        public List<GroqKeyword>? Keywords { get; set; }
    }

    // DTOs for Groq JSON deserialization
    private class GroqApiResponse
    {
        [JsonPropertyName("choices")]
        public GroqChoice[]? Choices { get; set; }
    }

    private class GroqChoice
    {
        [JsonPropertyName("message")]
        public GroqMessage Message { get; set; } = new();
    }

    private class GroqMessage
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    private class GroqKeyword
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
        
        [JsonPropertyName("weight")]
        public double Weight { get; set; }
        
        [JsonPropertyName("language")]
        public string? Language { get; set; }
        
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }

    private class GroqExtension
    {
        [JsonPropertyName("ext")]
        public string Ext { get; set; } = "";
        
        [JsonPropertyName("weight")]
        public double Weight { get; set; }
    }

    private class GroqDateFilters
    {
        [JsonPropertyName("created")]
        public GroqDateRange? Created { get; set; }
        
        [JsonPropertyName("modified")]
        public GroqDateRange? Modified { get; set; }
    }

    private class GroqDateRange
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }
        
        [JsonPropertyName("to")]
        public string? To { get; set; }
        
        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }

    private class GroqSizeFilter
    {
        [JsonPropertyName("min_mb")]
        public double? MinMb { get; set; }

        [JsonPropertyName("max_mb")]
        public double? MaxMb { get; set; }
    }

    private class GroqFolderHint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("weight")]
        public double Weight { get; set; }
    }

    private class GroqOpenAction
    {
        [JsonPropertyName("should_open")]
        public bool ShouldOpen { get; set; }

        [JsonPropertyName("open_mode")]
        public string OpenMode { get; set; } = "show_list";
    }

    private class GroqTargetType
    {
        [JsonPropertyName("file")]
        public double File { get; set; } = 0.5;
        
        [JsonPropertyName("folder")]
        public double Folder { get; set; } = 0.5;
    }

    /// <summary>
    /// Extract meaningful keywords from the query, filtering out stopwords.
    /// </summary>
    private List<string> ExtractKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<string>();

        // Normalize and split
        var tokens = Regex.Split(query.ToLowerInvariant(), @"[\s\-_,;:.!?()[\]{}]+")
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Where(token => token.Length > 1) // Skip single characters
            .Where(token => !Stopwords.Contains(token))
            .Distinct()
            .Take(6) // Limit to 6 keywords
            .ToList();

        return tokens;
    }

    /// <summary>
    /// Detect file types mentioned in the query.
    /// </summary>
    private List<string> DetectFileTypes(string query)
    {
        var detectedTypes = new HashSet<string>();
        var lowerQuery = query.ToLowerInvariant();

        foreach (var (fileType, patterns) in FileTypePatterns)
        {
            foreach (var pattern in patterns)
            {
                if (lowerQuery.Contains(pattern))
                {
                    detectedTypes.Add(fileType);
                    break; // Found this type, no need to check other patterns
                }
            }
        }

        return detectedTypes.ToList();
    }

    /// <summary>
    /// Create a default query when input is empty or invalid.
    /// </summary>
    private static StructuredQuery CreateDefaultQuery()
    {
        return new StructuredQuery
        {
            Intent = "search_files",
            Keywords = new List<string>(),
            FileTypes = new List<string>(),
            PredictedExtensions = new List<string>(),
            IncludeFolderContents = true,
            DateFilter = null,
            SortBy = "relevance"
        };
    }
}
