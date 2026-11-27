using System.Globalization;
using System.Text.RegularExpressions;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IntentParser
{
    private static readonly Regex ExplicitDate = new(
        @"(?<![\p{L}\p{N}_.-])(?:(?:bugun|dun|yarin)(?:ku|ki|den|e|u)?|yesterday|today|tomorrow|(?:gecen|son|onceki|bu|next|last|past|this)\s+(?:\d+\s+)?(?:(?:gun|hafta|ay|yil)(?:ki|ku|in|nin|un|nun|da|de|ta|te|daki|deki|taki|teki|dan|den|tan|ten)?(?:\s+(?:basi|sonu|ortasi)\w*)?|days?|weeks?|months?|years?)|(?:yaz|kis|ilkbahar|sonbahar)\s+(?:donem|aylar)\w*|(?:ocak|subat|mart|nisan|mayis|haziran|temmuz|agustos|eylul|ekim|kasim|aralik)\s+ay\w*|\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{4})(?![\p{L}\p{N}_.-])");

    private const string FileNouns = @"dosya(?:si|sini|lar|lari|larini|yi)?|files?";
    private const string FolderNouns = @"klasor(?:u|unu|ler|leri|lerini)?|folders?|directories";
    private const string DateVerbs = @"(?:degistir|duzenle|guncelle|modifi|edit|updat|olustur|indir|cekil|creat|download|captur)\w*|tarihli|tarihinde";
    private static readonly string[] CalendarDateFormats = ["yyyy-MM-dd", "d.M.yyyy", "d/M/yyyy"];
    private static readonly HashSet<string> NonSearchWords = StopwordsForEvidence();

    private static HashSet<string> StopwordsForEvidence() => new([
        "the", "a", "an", "and", "or", "but", "for", "to", "of", "in", "on", "at", "with",
        "show", "me", "my", "all", "please", "find", "search", "open", "look", "see",
        "ve", "veya", "ile", "icin", "tum", "butun", "bana", "bul", "ara", "goster", "ac",
        "lutfen", "bu", "ait", "dair", "nin", "nun", "adli", "isimli", "icinde", "icindeki",
        "altinda", "altindaki", "klasorunde", "klasorundeki", "projesinde", "projesindeki",
        "only", "sadece", "yalniz", "lari", "leri",
        "hani", "ya", "sey", "vardi", "vardir", "galiba", "sanirim", "falan", "filan", "bir", "olan", "gibi",
        "mi", "mu", "da", "de"
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Identifier = new(@"^(?=[^_-]*\p{L})(?=.*\p{N})[\p{L}\p{N}]+(?:[_-][\p{L}\p{N}]+)+$");

    private void ApplyExplicitFilters(StructuredQuery query, string original)
    {
        var composed = SearchTextNormalizer.ToComposedForm(original);
        var text = SearchTextNormalizer.Fold(composed.ToLowerInvariant());
        var pathText = PathWords(text);
        var requiredFolders = query.FolderHints.Where(hint => IsExplicitLocation(pathText, hint.Name)).ToList();
        foreach (var folder in new[] { "Desktop", "Downloads", "Documents", "Pictures", "Music", "Videos" })
        {
            if (IsExplicitLocation(pathText, folder) &&
                !requiredFolders.Any(hint => string.Equals(hint.Name, folder, StringComparison.OrdinalIgnoreCase)))
                requiredFolders.Add(new FolderHint { Name = folder, Weight = 1 });
        }
        if (requiredFolders.Count == 0)
        {
            var location = Regex.Match(text, """(?:"([^"]+)"|([\p{L}\p{N}_.-]+))\s+(?:klasor(?:unde(?:ki)?|unun\s+icinde)|altinda(?:ki)?|icinde(?:ki)?|projesinde(?:ki)?)\b""");
            if (location.Success && (location.Index == 0 || location.Groups[1].Success ||
                location.Groups[2].Value.IndexOfAny(['.', '_', '-']) >= 0 ||
                Regex.IsMatch(text[(location.Index + location.Length)..], @"^\s+(?:ara|bul|goster)\b")))
            {
                var capture = location.Groups[1].Success ? location.Groups[1] : location.Groups[2];
                var folder = composed.Length == text.Length ? composed.Substring(capture.Index, capture.Length) : capture.Value;
                requiredFolders.Add(new FolderHint { Name = folder, Weight = 1 });
            }
        }
        query.RequiredFolders = requiredFolders;

        var quoted = Regex.Match(original, "\"([^\"]+)\"");
        var named = Regex.Match(original, @"([\p{L}\p{N}_.-]+)\s+(?:ad[lL][ıiİI]|isimli)\b", RegexOptions.IgnoreCase);
        query.RequiredName = quoted.Success && !requiredFolders.Any(folder =>
                PathWords(folder.Name) == PathWords(quoted.Groups[1].Value))
            ? quoted.Groups[1].Value
            : named.Success ? named.Groups[1].Value : null;

        var qualifiers = Regex.Replace(text, "\"[^\"]+\"", " ");
        qualifiers = Regex.Replace(qualifiers, @"[\p{L}\p{N}_.-]+\s+(?:adli|isimli)\b", " ");
        foreach (var folder in requiredFolders)
        {
            var alias = LocalizedFolderAlias(folder.Name);
            if (alias.Length > 0)
                qualifiers = Regex.Replace(qualifiers, $@"\b{alias}(?:nde(?:ki)?|de(?:ki)?|nun\s+icinde)\b", " ");
            var name = string.Join(@"[\s._-]+", PathWords(folder.Name).Split(' ').Select(Regex.Escape));
            if (name.Length > 0)
                qualifiers = Regex.Replace(qualifiers,
                    $@"\b(?:{name}\s+(?:klasor(?:unde(?:ki)?|unun\s+icinde)|altinda(?:ki)?|icinde(?:ki)?|projesinde(?:ki)?)|(?:in|inside|under)\s+(?:the\s+)?{name})\b", " ");
        }
        var wantsFolders = Regex.IsMatch(qualifiers, $@"\b(?:{FolderNouns})\b");
        var hasFileNoun = Regex.IsMatch(qualifiers, $@"\b(?:{FileNouns})\b");
        var words = wantsFolders && !hasFileNoun ? [] : ReadWords(qualifiers);
        var explicitTypes = FileTypePatterns
            .Where(type => words.Any(word => IsTypeWord(word, type.Value)))
            .Select(type => type.Key).ToArray();
        var explicitExtensions = words
            .Where(word => word.StartsWith('.') || KnownExtensions.Contains(word))
            .Select(word => word.TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var confirmedExtensions = explicitExtensions.Count > 0
            ? explicitExtensions
            : explicitTypes.SelectMany(type => ExtensionMappings[type])
                .Select(extension => extension.TrimStart('.'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        query.SoftExtensions = query.SoftExtensions.Concat(query.HardExtensions)
            .Where(extension => !confirmedExtensions.Contains(extension.TrimStart('.')))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        query.HardExtensions = confirmedExtensions.Select(extension => "." + extension).ToList();
        query.PredictedExtensions = query.HardExtensions.ToList();
        query.FileTypes = explicitTypes.Concat(ExtensionMappings
                .Where(type => type.Value.Any(extension => explicitExtensions.Contains(extension.TrimStart('.'))))
                .Select(type => type.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        query.SizeFilter = ReadExplicitSize(qualifiers, out var evidence);
        var wantsFiles = confirmedExtensions.Count > 0 || hasFileNoun || query.SizeFilter != null && !wantsFolders;
        if (wantsFolders != wantsFiles)
        {
            query.TargetType = new TargetType { Folder = wantsFolders ? 1 : 0, File = wantsFiles ? 1 : 0 };
        }
        else if (query.TargetType is { } hint)
        {
            query.TargetType = new TargetType
            {
                Folder = hint.PrefersFolder ? 0.6 : hint.PrefersFile ? 0.4 : 0.5,
                File = hint.PrefersFile ? 0.6 : hint.PrefersFolder ? 0.4 : 0.5
            };
        }

        var dates = ExplicitDate.Matches(qualifiers);
        var modified = Regex.IsMatch(qualifiers, @"\b(?:degistir|duzenle|guncelle|modifi|edit|updat)\w*");
        var created = Regex.IsMatch(qualifiers, @"\b(?:olustur|indir|cekil|creat|download|captur)\w*");
        if (dates.Count == 0)
        {
            query.DateFilter = null;
        }
        else if (ReadCalendarBounds(dates, qualifiers) is { } bounds)
        {
            query.DateFilter = modified && !created
                ? new DateFilter { ModifiedAfter = bounds.From, ModifiedBeforeExclusive = bounds.To }
                : new DateFilter { CreatedAfter = bounds.From, CreatedBeforeExclusive = bounds.To };
        }
        else if (query.DateFilter is { } date)
        {
            if (modified && !created)
            {
                if (date.ModifiedAfter == null && date.ModifiedBeforeExclusive == null)
                    (date.ModifiedAfter, date.ModifiedBeforeExclusive) = (date.CreatedAfter, date.CreatedBeforeExclusive);
                date.CreatedAfter = date.CreatedBeforeExclusive = null;
                if (date.ModifiedAfter == null && date.ModifiedBeforeExclusive == null) query.DateFilter = null;
            }
            else if (!modified)
            {
                if (date.CreatedAfter == null && date.CreatedBeforeExclusive == null)
                    (date.CreatedAfter, date.CreatedBeforeExclusive) = (date.ModifiedAfter, date.ModifiedBeforeExclusive);
                date.ModifiedAfter = date.ModifiedBeforeExclusive = null;
                if (date.CreatedAfter == null && date.CreatedBeforeExclusive == null) query.DateFilter = null;
            }
        }

        if (dates.Count > 0)
        {
            evidence = ExplicitDate.Replace(evidence, " ");
            evidence = Regex.Replace(evidence, $@"\b(?:{DateVerbs}|once\w*|sonra\w*|itibaren|arasinda|before|after|since|between)\b", " ");
        }
        var fillers = Regex.Matches(composed.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .Where(word => word.Contains('ş') && SearchTextNormalizer.Fold(word) is "su" or "iste")
            .Select(SearchTextNormalizer.Fold)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        query.OriginalTerms = ReadWords(evidence).Where(word =>
                !NonSearchWords.Contains(word) && !fillers.Contains(word) &&
                !explicitExtensions.Contains(word.TrimStart('.')) &&
                !explicitTypes.Any(type => IsTypeWord(word, FileTypePatterns[type])) &&
                !Regex.IsMatch(word, $@"^(?:{FileNouns}|{FolderNouns})$"))
            .Concat(query.RequiredName == null ? [] : ReadWords(query.RequiredName))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (query.RequiredName == null && query.OriginalTerms.Where(term => Identifier.IsMatch(term)).Take(2).ToArray() is [var identifier])
            query.RequiredName = identifier;
        query.FilterOnlyMode = query.RequiredName == null && query.OriginalTerms.Count == 0 &&
            (confirmedExtensions.Count > 0 || requiredFolders.Count > 0 || query.DateFilter != null ||
             query.SizeFilter != null || wantsFiles || wantsFolders);
    }

    private static bool IsTypeWord(string word, string[] patterns) =>
        !KnownExtensions.Contains(word) && !word.Contains('.') &&
        patterns.Any(pattern => !KnownExtensions.Contains(pattern) &&
            SearchTextNormalizer.Fold(pattern) == CanonicalizeFallbackTerm(word));

    private (string? From, string? To)? ReadCalendarBounds(MatchCollection matches, string text)
    {
        if (matches.Count is < 1 or > 2) return null;

        var start = ReadDate(matches[0].Value);
        if (!start.HasValue) return null;
        if (matches.Count == 2)
        {
            var end = ReadDate(matches[1].Value);
            if (!end.HasValue || end.Value < start.Value) return null;
            return (Format(start), Format(NextDay(end.Value)));
        }

        var match = matches[0];
        var prefix = text[..match.Index];
        var suffix = text[(match.Index + match.Length)..];
        if (Regex.IsMatch(prefix, @"\bbefore\s+$") ||
            Regex.IsMatch(suffix, @"^(?:['’]?[dt][ae]n)?\s+once\w*\b"))
            return (null, Format(start));
        if (Regex.IsMatch(prefix, @"\bafter\s+$") ||
            Regex.IsMatch(suffix, @"^(?:['’]?[dt][ae]n)?\s+sonra\w*\b"))
            return (Format(NextDay(start.Value)), null);
        if (Regex.IsMatch(prefix, @"\bsince\s+$") ||
            Regex.IsMatch(suffix, @"^(?:['’]?[dt][ae]n)?\s+(?:itibaren|beri)\b"))
            return (Format(start), null);

        return (Format(start), Format(NextDay(start.Value)));

        DateOnly? ReadDate(string value)
        {
            if (DateOnly.TryParseExact(value, CalendarDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return date;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_nowProvider(), _timeZone).DateTime);
            return value switch
            {
                "yesterday" => today.AddDays(-1),
                "today" => today,
                "tomorrow" => today.AddDays(1),
                _ when value.StartsWith("bugun", StringComparison.Ordinal) => today,
                _ when value.StartsWith("dun", StringComparison.Ordinal) => today.AddDays(-1),
                _ when value.StartsWith("yarin", StringComparison.Ordinal) => today.AddDays(1),
                _ => null
            };
        }

        static DateOnly? NextDay(DateOnly date) => date == DateOnly.MaxValue ? null : date.AddDays(1);
        static string? Format(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string[] ReadWords(string text) => Regex.Matches(text, @"\.?[\p{L}\p{N}][\p{L}\p{N}_.-]*")
        .Select(match => match.Value.TrimEnd('.')).ToArray();

    private static string PathWords(string text) => string.Join(' ', Regex.Matches(
            SearchTextNormalizer.Fold(text.ToLowerInvariant()), @"[\p{L}\p{N}]+").Select(match => match.Value));

    private static bool IsExplicitLocation(string text, string folder)
    {
        var name = Regex.Escape(PathWords(folder));
        if (name.Length == 0) return false;
        if (Regex.IsMatch(text,
                $@"\b(?:{name} (?:klasor(?:unde(?:ki)?|unun icinde)|altinda(?:ki)?|icinde(?:ki)?|projesinde(?:ki)?)|(?:in|inside|under) (?:the )?{name})\b"))
        {
            return true;
        }
        var alias = LocalizedFolderAlias(folder);
        return alias.Length > 0 && Regex.IsMatch(text, $@"\b{alias}(?:nde(?:ki)?|de(?:ki)?|nun icinde)\b");
    }

    private static string LocalizedFolderAlias(string folder) => folder.ToLowerInvariant() switch
        {
            "desktop" => "masaustu",
            "downloads" => "indirilenler",
            "documents" => "belgeler",
            "pictures" => "resimler",
            "music" => "muzik",
            "videos" => "videolar",
            _ => ""
        };

    private static SizeFilter? ReadExplicitSize(string text, out string remainder)
    {
        remainder = text;
        const string number = @"\d+(?:[.,]\d+)?";
        const string unit = "b|kb|mb|gb|tb";
        var range = Regex.Match(text, $@"\b({number})\s*(?:-|ile|to)\s*({number})\s*({unit})\b");
        if (range.Success)
        {
            var min = Megabytes(range.Groups[1].Value, range.Groups[3].Value);
            var max = Megabytes(range.Groups[2].Value, range.Groups[3].Value);
            if (!double.IsFinite(max) || min > max) return null;
            remainder = text.Replace(range.Value, " ", StringComparison.Ordinal);
            return new SizeFilter { MinMb = min, MaxMb = max };
        }
        var lower = Regex.Match(text, $@"\b(?:en az|at least|over|larger than)\s+({number})\s*({unit})\b|\b({number})\s*({unit})(?:['’]?[dt][ae]n)?\s+(?:daha\s+)?buyuk\b");
        var upper = Regex.Match(text, $@"\b(?:en fazla|at most|under|smaller than)\s+({number})\s*({unit})\b|\b({number})\s*({unit})(?:['’]?[dt][ae]n)?\s+(?:daha\s+)?kucuk\b");
        if (lower.Success) remainder = remainder.Replace(lower.Value, " ", StringComparison.Ordinal);
        if (upper.Success) remainder = remainder.Replace(upper.Value, " ", StringComparison.Ordinal);
        var minimum = Bound(lower);
        var maximum = Bound(upper);
        return minimum > maximum || minimum is null && maximum is null ? null : new SizeFilter
        {
            MinMb = minimum,
            MaxMb = maximum
        };

        static double? Bound(Match match)
        {
            if (!match.Success) return null;
            var value = Megabytes(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value,
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value);
            return double.IsFinite(value) ? value : null;
        }

        static double Megabytes(string number, string unit) =>
            double.Parse(number.Replace(',', '.'), CultureInfo.InvariantCulture) * (unit switch
            {
                "b" => 1d / 1048576,
                "kb" => 1d / 1024,
                "gb" => 1024,
                "tb" => 1048576,
                _ => 1
            });
    }
}
