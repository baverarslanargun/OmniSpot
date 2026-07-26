using System;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.Models;

/// <summary>
/// Structured query extracted from natural language by LLM
/// </summary>
public class StructuredQuery {
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "search";
    
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();
    
    [JsonPropertyName("file_types")]
    public List<string> FileTypes { get; set; } = new();
    
    /// <summary>
    /// Specific file extensions predicted by AI (e.g., "pptx", "mp4", "jpg")
    /// More specific than FileTypes - used for precise filtering
    /// </summary>
    [JsonPropertyName("predicted_extensions")]
    public List<string> PredictedExtensions { get; set; } = new();
    
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
    
    /// <summary>
    /// Target type probabilities from AI (file vs folder preference)
    /// </summary>
    [JsonPropertyName("target_type")]
    public TargetType? TargetType { get; set; }
    
    /// <summary>
    /// When true, the query is asking for ALL files matching filters (type, folder, date, size)
    /// without searching for specific keywords in filenames.
    /// Examples: "tüm resimler", "bütün PDF'ler", "indirilen dosyaları göster"
    /// </summary>
    [JsonPropertyName("filter_only_mode")]
    public bool FilterOnlyMode { get; set; } = false;
    
    /// <summary>
    /// Indicates whether AI failed and rule-based fallback was used
    /// </summary>
    [JsonIgnore]
    public bool UsedFallback { get; set; } = false;
    
    /// <summary>
    /// Reason for fallback if UsedFallback is true
    /// </summary>
    [JsonIgnore]
    public string? FallbackReason { get; set; }
    
    /// <summary>
    /// Warning message for partial failures (e.g., Keyword API failed but Intent succeeded)
    /// </summary>
    [JsonIgnore]
    public string? WarningMessage { get; set; }
}

public class DateFilter {
    [JsonPropertyName("created_after")]
    public string? CreatedAfter { get; set; }
    
    [JsonPropertyName("created_before")]
    public string? CreatedBefore { get; set; }
    
    [JsonPropertyName("modified_after")]
    public string? ModifiedAfter { get; set; }
    
    [JsonPropertyName("modified_before")]
    public string? ModifiedBefore { get; set; }
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

/// <summary>
/// Target type probabilities - whether user wants a file or folder
/// </summary>
public class TargetType {
    /// <summary>
    /// Probability that user wants a file (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("file")]
    public double File { get; set; } = 0.5;
    
    /// <summary>
    /// Probability that user wants a folder (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("folder")]
    public double Folder { get; set; } = 0.5;
    
    /// <summary>
    /// Returns true if user prefers folders over files
    /// </summary>
    [JsonIgnore]
    public bool PrefersFolder => Folder > File;
    
    /// <summary>
    /// Returns true if user prefers files over folders
    /// </summary>
    [JsonIgnore]
    public bool PrefersFile => File > Folder;
    
    /// <summary>
    /// Returns true if there's a strong preference (difference > 0.3)
    /// </summary>
    [JsonIgnore]
    public bool HasStrongPreference => Math.Abs(File - Folder) > 0.3;
}