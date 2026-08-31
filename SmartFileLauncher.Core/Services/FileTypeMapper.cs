using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartFileLauncher.Core.Services;

public static class FileTypeMapper {
    private static readonly Dictionary<string, string[]> _typeMap = new(StringComparer.OrdinalIgnoreCase) {
        ["video"] = [".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg"],
        ["image"] = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff", ".tif"],
        ["document"] = [".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".tex"],
        ["spreadsheet"] = [".xls", ".xlsx", ".csv", ".ods"],
        ["presentation"] = [".ppt", ".pptx", ".odp", ".key"],
        ["audio"] = [".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus"],
        ["archive"] = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"],
        ["code"] = [".cs", ".js", ".py", ".java", ".cpp", ".c", ".h", ".html", ".css", ".json", ".xml"],
        ["executable"] = [".exe", ".dll", ".msi", ".bat", ".cmd", ".sh"],
        ["text"] = [".txt", ".md", ".log", ".cfg", ".ini", ".conf"]
    };
    
    public static IEnumerable<string> GetExtensions(string typeName) {
        if (_typeMap.TryGetValue(typeName, out var extensions)) {
            return extensions;
        }
        if (typeName.StartsWith(".")) {
            return [typeName];
        }
        return ["." + typeName];
    }
    
    public static IEnumerable<string> GetExtensionsForTypes(IEnumerable<string> types) {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types) {
            foreach (var ext in GetExtensions(type)) {
                result.Add(ext);
            }
        }
        return result;
    }
}