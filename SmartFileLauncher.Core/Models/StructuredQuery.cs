using System;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.Models;

public class StructuredQuery {
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "search";
    
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    [JsonIgnore]
    public List<SearchTerm> SearchTerms { get; set; } = new();
    
    [JsonPropertyName("file_types")]
    public List<string> FileTypes { get; set; } = new();
    
    [JsonPropertyName("predicted_extensions")]
    public List<string> PredictedExtensions { get; set; } = new();

    [JsonPropertyName("hard_extensions")]
    public List<string> HardExtensions { get; set; } = new();

    [JsonPropertyName("soft_extensions")]
    public List<string> SoftExtensions { get; set; } = new();
    
    [JsonPropertyName("date_filter")]
    public DateFilter? DateFilter { get; set; }
    
    [JsonPropertyName("include_folder_contents")]
    public bool IncludeFolderContents { get; set; } = true;
    
    [JsonPropertyName("sort_by")]
    public string SortBy { get; set; } = "relevance";

    [JsonPropertyName("size_filter")]
    public SizeFilter? SizeFilter { get; set; }

    [JsonPropertyName("folder_hints")]
    public List<FolderHint> FolderHints { get; set; } = new();

    [JsonPropertyName("open_action")]
    public OpenAction? OpenAction { get; set; }
    
    [JsonPropertyName("target_type")]
    public TargetType? TargetType { get; set; }
    
    [JsonPropertyName("filter_only_mode")]
    public bool FilterOnlyMode { get; set; } = false;
    
    [JsonIgnore]
    public bool UsedFallback { get; set; } = false;
    
    [JsonIgnore]
    public string? FallbackReason { get; set; }
    
    [JsonIgnore]
    public string? WarningMessage { get; set; }
}

public class DateFilter {
    [JsonPropertyName("created_after")]
    public string? CreatedAfter { get; set; }
    
    [JsonPropertyName("created_before_exclusive")]
    public string? CreatedBeforeExclusive { get; set; }
    
    [JsonPropertyName("modified_after")]
    public string? ModifiedAfter { get; set; }
    
    [JsonPropertyName("modified_before_exclusive")]
    public string? ModifiedBeforeExclusive { get; set; }
}

public enum SearchTermCategory {
    Exact,
    Variant,
    Translation,
    Related,
    Legacy
}

public enum SearchTermRole {
    Anchor,
    Phrase,
    Context
}

public sealed class SearchTerm {
    public string Text { get; set; } = "";
    public SearchTermCategory Category { get; set; }
    public SearchTermRole Role { get; set; } = SearchTermRole.Anchor;
    public int AnchorGroup { get; set; }
    public double Weight { get; set; }
}

public class SizeFilter {
    [JsonPropertyName("min_mb")]
    public double? MinMb { get; set; }

    [JsonPropertyName("max_mb")]
    public double? MaxMb { get; set; }
}

public class FolderHint {
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("weight")]
    public double Weight { get; set; }
}

public class OpenAction {
    [JsonPropertyName("should_open")]
    public bool ShouldOpen { get; set; }

    [JsonPropertyName("open_mode")]
    public string OpenMode { get; set; } = "show_list";
}

public class TargetType {
    [JsonPropertyName("file")]
    public double File { get; set; } = 0.5;
    
    [JsonPropertyName("folder")]
    public double Folder { get; set; } = 0.5;
    
    [JsonIgnore]
    public bool PrefersFolder => Folder > File;
    
    [JsonIgnore]
    public bool PrefersFile => File > Folder;
    
    [JsonIgnore]
    public bool HasStrongPreference => Math.Abs(File - Folder) > 0.3;
}
