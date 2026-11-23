using System.Buffers;
using System.Globalization;
using System.Text;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Services;

public sealed record StartMenuApplication(string Id, string Name, string Detail)
{
    public bool CanRunAsAdministrator { get; init; }
    public bool HasFileLocation { get; init; }
}

public readonly record struct StartMenuAppMatches(
    IReadOnlyList<StartMenuApplication> Items,
    int TotalCount,
    StartMenuApplication? UniqueFirst);

public sealed class StartMenuApplications
{
    private const int GeneralLimit = 3;
    private const int WordStartRank = 1;
    private const int NameStartRank = 2;
    private const int ExactNameRank = 3;
    private const CompareOptions NameOptions = CompareOptions.IgnoreCase;

    private static readonly string[] FileExtensions = FileFilterCategories.All
        .SelectMany(FileFilterCategories.GetExtensions)
        .Concat([".lnk", ".url", ".msc", ".cpl", ".com", ".sys", ".msix", ".appx", ".msixbundle", ".appxbundle"])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private readonly IReadOnlyList<StartMenuApplication> _applications;
    private readonly CompareInfo _names;
    private readonly bool[]? _recheckNames;

    public static StartMenuApplications Empty { get; } = new([]);
    internal IReadOnlySet<string>? ChangedIds { get; init; }

    public StartMenuApplications(IEnumerable<StartMenuApplication> applications, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(applications);

        culture ??= CultureInfo.CurrentCulture;
        _names = culture.CompareInfo;
        var comparer = StringComparer.Create(culture, ignoreCase: true);
        var sorted = applications
            .Where(app => !string.IsNullOrWhiteSpace(app.Id) && !string.IsNullOrWhiteSpace(app.Name))
            .DistinctBy(app => app.Id, StringComparer.Ordinal)
            .OrderBy(app => app.Name, comparer)
            .ThenBy(app => app.Id, StringComparer.Ordinal)
            .ToArray();

        SetDisambiguationDetails(sorted, comparer);
        _applications = Array.AsReadOnly(sorted);
        if (_names.Name is "" or "en" or "en-US" or "en-GB" or "tr" or "tr-TR")
            _recheckNames = Array.ConvertAll(sorted, app => !IsPrintableAscii(app.Name));
    }

    internal sealed class SearchSession(StartMenuApplications catalog, IReadOnlySet<string>? pinned = null)
    {
        private readonly int[] _candidates = catalog._recheckNames == null ? [] : new int[catalog._applications.Count];
        private string? _previousTerm;
        internal int CandidateCount { get; private set; }

        internal StartMenuAppMatches MatchGeneral(string query)
        {
            string term = query.Trim();
            if (catalog._recheckNames == null || term.Length == 0 || !IsPrintableAscii(term) || IsFileQuery(term))
            {
                _previousTerm = null;
                CandidateCount = 0;
                return catalog.MatchGeneral(term, pinned);
            }

            bool narrow = _previousTerm != null && term.StartsWith(_previousTerm, StringComparison.Ordinal);
            var matches = catalog.RankGeneral(term, _candidates, narrow ? CandidateCount : null, pinned, out int retained);
            _previousTerm = term;
            CandidateCount = retained;
            return matches;
        }
    }

    private static bool IsPrintableAscii(ReadOnlySpan<char> value) =>
        !value.ContainsAnyExceptInRange(' ', '~');

    public StartMenuAppMatches MatchGeneral(string? query, IReadOnlySet<string>? pinned = null)
    {
        var term = query.AsSpan().Trim();
        if (_applications.Count == 0 || term.IsEmpty ||
            IsFileQuery(term) || _names.Compare(term, ReadOnlySpan<char>.Empty, NameOptions) == 0)
        {
            return new StartMenuAppMatches([], 0, null);
        }

        return RankGeneral(term, default, null, pinned, out _);
    }

    private static int Score(int rank, StartMenuApplication app, IReadOnlySet<string>? pinned) =>
        rank * 2 + (pinned != null && pinned.Contains(app.Id) ? 1 : 0);

    private StartMenuAppMatches RankGeneral(ReadOnlySpan<char> term, Span<int> candidates, int? previousCount,
        IReadOnlySet<string>? pinned, out int retained)
    {
        retained = 0;
        bool requiresExactName = term[0] == '.' && term.IndexOfAny(' ', '\t') < 0;
        Span<int> bestIndices = stackalloc int[GeneralLimit];
        Span<int> bestRanks = stackalloc int[GeneralLimit];
        int totalCount = 0;
        int bestCount = 0;
        int firstRankCount = 0;
        int count = previousCount ?? _applications.Count;
        for (int positionInCatalog = 0; positionInCatalog < count; positionInCatalog++)
        {
            int i = previousCount.HasValue ? candidates[positionInCatalog] : positionInCatalog;
            var rank = GetRank(_applications[i].Name.AsSpan(), term);
            if (!candidates.IsEmpty && (rank > 0 || _recheckNames![i]))
                candidates[retained++] = i;
            if (rank == 0 || (requiresExactName && rank != ExactNameRank))
                continue;

            int score = Score(rank, _applications[i], pinned);
            totalCount++;
            if (bestCount == 0 || score > bestRanks[0])
                firstRankCount = 1;
            else if (score == bestRanks[0])
                firstRankCount++;

            int position = bestCount;
            while (position > 0 && score > bestRanks[position - 1])
                position--;
            if (position == GeneralLimit)
                continue;

            int last = Math.Min(bestCount, GeneralLimit - 1);
            for (int j = last; j > position; j--)
            {
                bestIndices[j] = bestIndices[j - 1];
                bestRanks[j] = bestRanks[j - 1];
            }
            bestIndices[position] = i;
            bestRanks[position] = score;
            bestCount = Math.Min(bestCount + 1, GeneralLimit);
        }

        if (bestCount == 0)
            return new StartMenuAppMatches([], 0, null);

        var items = new StartMenuApplication[bestCount];
        for (int i = 0; i < items.Length; i++)
            items[i] = _applications[bestIndices[i]];

        return new StartMenuAppMatches(items, totalCount, firstRankCount == 1 ? items[0] : null);
    }

    public IReadOnlyList<StartMenuApplication> MatchAll(string? query, IReadOnlySet<string>? pinned = null)
    {
        var term = query.AsSpan().Trim();
        if (pinned is { Count: 0 })
            pinned = null;
        if (term.IsEmpty)
        {
            if (pinned is not { } pins || !_applications.Any(app => pins.Contains(app.Id)))
                return _applications;
            return _applications.OrderByDescending(app => pins.Contains(app.Id)).ToArray();
        }

        List<(StartMenuApplication App, int Rank)>? matches = null;
        foreach (var app in _applications)
        {
            if (_names.IndexOf(app.Name.AsSpan(), term, NameOptions) >= 0)
                (matches ??= []).Add((app, Score(GetRank(app.Name.AsSpan(), term), app, pinned)));
        }

        return matches is null ? [] : matches.OrderByDescending(match => match.Rank).Select(match => match.App).ToArray();
    }

    private int GetRank(ReadOnlySpan<char> name, ReadOnlySpan<char> term)
    {
        int index = _names.IndexOf(name, term, NameOptions);
        if (index == 0)
            return _names.Compare(name, term, NameOptions) == 0 ? ExactNameRank : NameStartRank;

        int offset = 0;
        while (index >= 0)
        {
            int start = offset + index;
            if (Rune.DecodeLastFromUtf16(name[..start], out var previous, out _) != OperationStatus.Done ||
                (!Rune.IsLetterOrDigit(previous) && Rune.GetUnicodeCategory(previous) is not
                    (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)))
            {
                return WordStartRank;
            }

            offset = start + 1;
            if (offset == name.Length)
                break;
            index = _names.IndexOf(name[offset..], term, NameOptions);
        }

        return 0;
    }

    private static bool IsFileQuery(ReadOnlySpan<char> query)
    {
        if (query.ContainsAny("\\/*?") ||
            (query.Length > 1 && query[1] == ':' && char.IsAsciiLetter(query[0])))
            return true;

        int lastDot = query.LastIndexOf('.');
        if (lastDot < 0)
            return false;

        var suffix = query[lastDot..];
        foreach (string extension in FileExtensions)
        {
            if (suffix.Equals(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void SetDisambiguationDetails(StartMenuApplication[] sorted, StringComparer comparer)
    {
        for (int start = 0; start < sorted.Length;)
        {
            int end = start + 1;
            while (end < sorted.Length && comparer.Equals(sorted[start].Name, sorted[end].Name))
                end++;

            if (end == start + 1)
            {
                if (!string.IsNullOrEmpty(sorted[start].Detail))
                    sorted[start] = sorted[start] with { Detail = string.Empty };
            }
            else
            {
                var labels = LocationLabels(sorted.AsSpan(start, end - start), comparer);
                var detailCounts = new Dictionary<string, int>(comparer);
                var ids = new HashSet<string>(comparer);
                for (int i = start; i < end; i++)
                {
                    ids.Add(sorted[i].Id);
                    detailCounts[labels[i - start]] = detailCounts.GetValueOrDefault(labels[i - start]) + 1;
                }

                for (int i = start; i < end; i++)
                {
                    var app = sorted[i];
                    string detail = labels[i - start];
                    if (detail.Length == 0 || detailCounts[detail] > 1 || ids.Contains(detail))
                        detail = app.Id;

                    sorted[i] = app with { Detail = detail };
                }
            }

            start = end;
        }
    }

    private static string[] LocationLabels(ReadOnlySpan<StartMenuApplication> group, StringComparer comparer)
    {
        var parts = new string[group.Length][];
        var located = new List<string[]>();
        for (int i = 0; i < group.Length; i++)
        {
            parts[i] = LocationParts(string.IsNullOrWhiteSpace(group[i].Detail) ? group[i].Id : group[i].Detail);
            if (parts[i].Length > 1)
                located.Add(parts[i]);
        }

        int prefix = 0;
        int suffix = 0;
        if (located.Count > 1)
        {
            int shortest = located.Min(segments => segments.Length);
            while (prefix < shortest && located.TrueForAll(segments => comparer.Equals(segments[prefix], located[0][prefix])))
                prefix++;
            while (suffix < shortest - prefix &&
                   located.TrueForAll(segments => comparer.Equals(segments[^(suffix + 1)], located[0][^(suffix + 1)])))
                suffix++;
        }

        var labels = new string[group.Length];
        for (int i = 0; i < group.Length; i++)
        {
            var segments = parts[i];
            labels[i] = segments.Length switch
            {
                0 => string.Empty,
                1 => segments[0],
                _ when located.Count == 1 || prefix == 0 => segments[^2],
                _ when segments.Length - suffix > prefix => segments[prefix],
                _ => segments[prefix - 1]
            };
        }

        return labels;
    }

    private static string[] LocationParts(string source)
    {
        string text = source.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.Host + uri.AbsolutePath);

        return text.Split(['\\', '/'],StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
