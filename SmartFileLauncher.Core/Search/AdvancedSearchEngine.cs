using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Search;

public class AdvancedSearchEngine
{
    private readonly Func<CancellationToken, ISearchStateReader> _stateProvider;
    private readonly ITokenizer _tokenizer;
    private readonly IScoringStrategy _scoring;

    public AdvancedSearchEngine(
        InvertedIndex invertedIndex,
        ITokenizer tokenizer,
        IScoringStrategy scoring,
        FileSystemNode rootNode)
    {
        ArgumentNullException.ThrowIfNull(invertedIndex);
        ArgumentNullException.ThrowIfNull(rootNode);
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
        _stateProvider = cancellationToken =>
        {
            var snapshot = SearchSnapshot.Create(invertedIndex, rootNode, cancellationToken);
            return SearchState.Create(
                snapshot.InvertedIndex,
                GetTreeNodes(snapshot.RootNode!, cancellationToken));
        };
    }

    public AdvancedSearchEngine(
        Func<CancellationToken, SearchSnapshot> snapshotProvider,
        ITokenizer tokenizer,
        IScoringStrategy scoring)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
        _stateProvider = cancellationToken =>
        {
            var snapshot = snapshotProvider(cancellationToken);
            return snapshot.RootNode == null
                ? SearchState.Empty
                : SearchState.Create(
                    snapshot.InvertedIndex,
                    GetTreeNodes(snapshot.RootNode, cancellationToken));
        };
    }

    public AdvancedSearchEngine(
        Func<CancellationToken, ISearchStateReader> stateProvider,
        ITokenizer tokenizer,
        IScoringStrategy scoring)
    {
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }

    public IReadOnlyList<SearchResult> Search(
        StructuredQuery query,
        int maxResults = 100,
        CancellationToken cancellationToken = default) =>
        Search(query, maxResults, ResultView.SearchDefault, cancellationToken);

    public IReadOnlyList<SearchResult> Search(
        StructuredQuery query,
        int maxResults,
        ResultView view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(view);
        var filter = view.Filter;
        cancellationToken.ThrowIfCancellationRequested();
        var state = _stateProvider(cancellationToken).ForQuery();
        using var lease = state as IQuerySnapshotLease;
        if (state is ITokenIndexSnapshot { IsTokenIndexReady: false }) throw new TokenIndexNotReadyException();
        if (state.ItemCount == 0 || maxResults <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        var namedFolders = FindNamedSubfolders(query, state, maxResults, view, cancellationToken);
        var searchTerms = GetSearchTerms(query);
        var requiredFolders = ExpandFilterFolderNames(query.OriginalText == null && query.FilterOnlyMode
            ? query.FolderHints : query.RequiredFolders, cancellationToken);
        var folderHints = ExpandFilterFolderNames(query.FolderHints, cancellationToken);
        var extensions = CreateFilterOnlyAllowedExtensions(query, cancellationToken);
        var metadata = CreateInitialCatalogFilter(query) with
        {
            AllowedExtensions = extensions.Count > 0 && query.TargetType is not { HasStrongPreference: true, PrefersFolder: true }
                ? extensions : null,
            PathSegments = requiredFolders
        };
        var hasEvidence = searchTerms.Count > 0 || query.OriginalTerms.Count > 0 || folderHints != null;
        if (!hasEvidence && !query.FilterOnlyMode && query.OriginalText != null)
        {
            return namedFolders;
        }
        if ((query.FilterOnlyMode || !hasEvidence) && state is IQueryCatalogSnapshot catalog)
        {
            return SearchFilterOnly(query, searchTerms, catalog, requiredFolders, maxResults, view, cancellationToken);
        }

        var candidates = query.FilterOnlyMode || !hasEvidence
            ? state.GetAllItems(cancellationToken).Select(item => (item, default(MatchWeights)))
            : GetCandidateNodes(query, searchTerms, state, metadata, folderHints, cancellationToken).Values;

        var order = SortedResultOrder.For(view.Sort);
        var queue = new PriorityQueue<SearchResult, SearchResult>(
            Comparer<SearchResult>.Create((left, right) => order.Compare(right, left)));
        var confirmedFolders = namedFolders.Select(folder => folder.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoring = CreateScoringContext(query, searchTerms);
        var now = DateTime.Now;
        foreach (var (node, matches) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (confirmedFolders.Contains(node.FullPath) ||
                !metadata.Matches(node) ||
                !MatchesExplicitNameAndFolder(query, node, requiredFolders) ||
                !filter.Matches(node.Name, node.IsDirectory, node.SizeBytes, node.LastWriteTime, now) ||
                !PathScope.Contains(view.Scope, node.FullPath))
            {
                continue;
            }

            Offer(new SearchResult
            {
                Name = node.Name,
                FullPath = node.FullPath,
                Score = CalculateScore(query, node, matches, scoring, cancellationToken),
                IsDirectory = node.IsDirectory,
                SizeBytes = node.SizeBytes,
                LastWriteTime = node.LastWriteTime
            });
        }
        foreach (var folder in namedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Offer(folder);
        }

        var results = new SearchResult[queue.Count];
        for (var i = results.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = queue.Dequeue();
        }
        return results;

        void Offer(SearchResult result)
        {
            if (queue.Count < maxResults) queue.Enqueue(result, result);
            else if (order.Compare(result, queue.Peek()) < 0) queue.EnqueueDequeue(result, result);
        }
    }

    private static bool MatchesExplicitNameAndFolder(StructuredQuery query, SearchItem item, HashSet<string>? folders) =>
        (folders == null || PathHasFolder(Path.GetDirectoryName(item.FullPath) ?? "", folders)) &&
        (query.RequiredName == null || ContainsRequiredName(item.Name, query.RequiredName));

    internal static bool ContainsRequiredName(string name, string required) =>
        WithoutSeparators(name).Contains(WithoutSeparators(required), StringComparison.OrdinalIgnoreCase);

    private static string WithoutSeparators(string value)
    {
        var folded = SearchTextNormalizer.Fold(value);
        return folded.AsSpan().IndexOfAny(" ._-") < 0 ? folded : string.Concat(folded.Where(c => c is not (' ' or '.' or '_' or '-')));
    }

    private static IReadOnlyList<SearchResult> FindNamedSubfolders(
        StructuredQuery query, ISearchStateReader state, int maxResults, ResultView view,
        CancellationToken cancellationToken)
    {
        if (query.OriginalText is not { } text || query.FilterOnlyMode || view.Filter.HidesFolders ||
            query.RequiredName != null || query.RequiredFolders.Count > 0 ||
            query.TargetType is { HasStrongPreference: true, PrefersFile: true } ||
            query.HardExtensions.Count > 0 || query.PredictedExtensions.Count > 0 || query.FileTypes.Count > 0 ||
            query.DateFilter != null || query.SizeFilter != null || query.OpenAction?.ShouldOpen == true)
        {
            return [];
        }

        var words = NormalizeFolderText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return [];
        }

        var nameFirstOnly = words[^1] is "klasorunde" or "altinda";
        string? namedAncestor = null;
        var location = Array.FindIndex(words, word => word is "altindaki" or "klasorundeki");
        if (location > 0 && location < words.Length - 1)
        {
            var end = words[^1] is "klasoru" or "klasorunu" ? words.Length - 1 : words.Length;
            namedAncestor = string.Join(' ', words[..location]);
            words = words[(location + 1)..end];
        }
        else if (nameFirstOnly)
        {
            words = words[..^1];
        }
        if (words.Length < (namedAncestor == null ? 2 : 1))
        {
            return [];
        }

        var phrase = string.Join(' ', words);
        var matches = FindMatches(words[0], nameFirst: true);
        if (matches.Count == 0 && !nameFirstOnly && namedAncestor == null)
        {
            matches = FindMatches(words[^1], nameFirst: false);
        }
        if (namedAncestor == null)
        {
            matches.AddRange(FindCompoundNames().Where(compound =>
                !matches.Exists(match => string.Equals(match.FullPath, compound.FullPath, StringComparison.OrdinalIgnoreCase))));
        }
        return matches.OrderBy(result => result, SortedResultOrder.For(view.Sort)).Take(maxResults).ToArray();

        List<SearchResult> FindCompoundNames()
        {
            var results = new List<SearchResult>();
            var parts = FolderWords(phrase);
            foreach (var item in state.Get(words[0], cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDirectory && !item.Name.Any(char.IsWhiteSpace) &&
                    (view.Scope is not { } scope || PathScope.Contains(scope, item.FullPath)) &&
                    FolderWords(NormalizeFolderText(item.Name)) == parts)
                {
                    results.Add(Named(item, 2000));
                }
            }
            return results;
        }

        List<SearchResult> FindMatches(string token, bool nameFirst)
        {
            var results = new List<SearchResult>();
            foreach (var item in state.Get(token, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.IsDirectory ||
                    (view.Scope is { } scope && !PathScope.Contains(scope, item.FullPath)))
                {
                    continue;
                }

                var name = NormalizeFolderText(item.Name);
                string ancestorName;
                if (namedAncestor != null)
                {
                    if (name != phrase) continue;
                    ancestorName = namedAncestor;
                }
                else
                {
                    var nameWithBoundary = nameFirst ? name + " " : " " + name;
                    var hasName = nameFirst
                        ? phrase.StartsWith(nameWithBoundary, StringComparison.Ordinal)
                        : phrase.EndsWith(nameWithBoundary, StringComparison.Ordinal);
                    if (!hasName) continue;
                    ancestorName = nameFirst ? phrase[nameWithBoundary.Length..] : phrase[..^nameWithBoundary.Length];
                }

                var depth = NamedAncestorDepth(item.FullPath, ancestorName);
                if (depth == 1 || depth > 1 && namedAncestor != null)
                {
                    results.Add(Named(item, depth == 1 ? 2000 : 1000));
                }
            }
            return results;
        }

        static SearchResult Named(SearchItem item, double score) => new()
        {
            Name = item.Name,
            FullPath = item.FullPath,
            IsDirectory = true,
            Score = score,
            SizeBytes = item.SizeBytes,
            LastWriteTime = item.LastWriteTime
        };

        static string FolderWords(string value) =>
            string.Join(' ', value.Split(FolderWordSeparators, StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly char[] FolderWordSeparators = [' ', '.', '_', '-'];

    private static int NamedAncestorDepth(string path, string name)
    {
        var depth = 1;
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent), depth++)
        {
            if (NormalizeFolderText(Path.GetFileName(parent)) == name)
            {
                return depth;
            }
        }
        return 0;
    }

    private static string NormalizeFolderText(string value) =>
        string.Join(' ', SearchTextNormalizer.Fold(value.ToLowerInvariant())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private readonly record struct FilterCandidate(SearchItem Item, double Score);

    private sealed class FilterCandidateOrder : IComparer<FilterCandidate>
    {
        private readonly bool _reverse;
        private readonly ItemSort _sort;

        private FilterCandidateOrder(bool reverse, ItemSort sort)
        {
            _reverse = reverse;
            _sort = sort;
        }

        internal static FilterCandidateOrder Create(ItemSort sort, bool reverse) =>
            new(reverse, sort);

        public int Compare(FilterCandidate x, FilterCandidate y) =>
            _reverse ? CompareCore(y, x) : CompareCore(x, y);

        private int CompareCore(FilterCandidate x, FilterCandidate y) =>
            _sort.Compare(Row(x), Row(y));

        private static SortRow Row(FilterCandidate candidate) => new(
            candidate.Item.Name,
            candidate.Item.FullPath,
            candidate.Item.IsDirectory,
            candidate.Item.SizeBytes,
            candidate.Item.LastWriteTime,
            candidate.Score);
    }

    private IReadOnlyList<SearchResult> SearchFilterOnly(
        StructuredQuery query,
        IReadOnlyList<SearchTerm> searchTerms,
        IQueryCatalogSnapshot state,
        HashSet<string>? expandedFolderNames,
        int maxResults,
        ResultView view,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var userFilter = view.Filter;
        var filter = CreateInitialCatalogFilter(query);
        var allowedExtensions = CreateFilterOnlyAllowedExtensions(query, cancellationToken);
        var userWantsFolders = query.TargetType is { HasStrongPreference: true, PrefersFolder: true };
        filter = filter with
        {
            PathSegments = expandedFolderNames,
            AllowedExtensions = !userWantsFolders && allowedExtensions.Count > 0 ? allowedExtensions : null,
            IncludeDirectoriesWithoutExtension = query.IncludeFolderContents
        };
        var nestsFolders = query.IncludeFolderContents && !userWantsFolders && filter.IncludeDirectories &&
            allowedExtensions.Count == 0;
        var folders = nestsFolders ? new List<SearchItem>() : null;
        var folderPaths = nestsFolders ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        var winners = new PriorityQueue<FilterCandidate, FilterCandidate>(
            FilterCandidateOrder.Create(view.Sort, reverse: true));
        var bestFirst = FilterCandidateOrder.Create(view.Sort, reverse: false);
        var scoringContext = CreateScoringContext(query, searchTerms);
        var noMatches = default(MatchWeights);

        foreach (var node in state.GetItems(filter, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expandedFolderNames != null && !PathHasFolder(node.FullPath, expandedFolderNames))
            {
                continue;
            }

            if (allowedExtensions.Count > 0 && !userWantsFolders &&
                !(node.IsDirectory && query.IncludeFolderContents) &&
                !allowedExtensions.Contains(Path.GetExtension(node.Name)))
            {
                continue;
            }

            if (!node.IsDirectory)
            {
                Offer(node);
            }
            else if (userWantsFolders)
            {
                Offer(node);
            }
            else if (folders != null)
            {
                folders.Add(node);
                folderPaths!.Add(node.FullPath);
            }
        }

        if (folders != null)
        {
            foreach (var folder in folders)
            {
                for (var parent = Path.GetDirectoryName(folder.FullPath); parent != null; parent = Path.GetDirectoryName(parent))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!folderPaths!.Contains(parent)) continue;
                    Offer(folder);
                    break;
                }
            }
        }

        var ordered = new FilterCandidate[winners.Count];
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered[index] = winners.Dequeue();
        }
        Array.Sort(ordered, bestFirst);

        return ordered.Select(candidate => new SearchResult
        {
            Name = candidate.Item.Name,
            FullPath = candidate.Item.FullPath,
            Score = candidate.Score,
            IsDirectory = candidate.Item.IsDirectory,
            SizeBytes = candidate.Item.SizeBytes,
            LastWriteTime = candidate.Item.LastWriteTime
        }).ToArray();

        void Offer(SearchItem item)
        {
            if (!PathScope.Contains(view.Scope, item.FullPath) ||
                !MatchesExplicitNameAndFolder(query, item, expandedFolderNames) ||
                !PassesFilterOnlyDate(item, query.DateFilter) ||
                !PassesFilterOnlySize(item, query.SizeFilter) ||
                !userFilter.Matches(
                    item.Name,
                    item.IsDirectory,
                    item.SizeBytes,
                    item.LastWriteTime,
                    now))
            {
                return;
            }

            var candidate = new FilterCandidate(
                item,
                CalculateScore(query, item, noMatches, scoringContext, cancellationToken));
            if (winners.Count < maxResults)
            {
                winners.Enqueue(candidate, candidate);
            }
            else if (bestFirst.Compare(candidate, winners.Peek()) < 0)
            {
                winners.Dequeue();
                winners.Enqueue(candidate, candidate);
            }
        }
    }

    private static QueryCatalogFilter CreateInitialCatalogFilter(StructuredQuery query)
    {
        var target = query.TargetType;
        var strongFolder = target is { HasStrongPreference: true, PrefersFolder: true };
        var strongFile = target is { HasStrongPreference: true, PrefersFile: true, File: > 0.7 };
        var userWantsFolders = target?.PrefersFolder == true;
        var includeDirectories = !strongFile && (userWantsFolders || query.IncludeFolderContents);

        return new QueryCatalogFilter(
            IncludeFiles: !strongFolder,
            IncludeDirectories: includeDirectories,
            FilterDirectoryDates: query.DateFilter != null,
            FilterFileSize: query.SizeFilter != null,
            CreatedAfter: ParseDate(query.DateFilter?.CreatedAfter),
            CreatedBeforeExclusive: ParseDate(query.DateFilter?.CreatedBeforeExclusive),
            ModifiedAfter: ParseDate(query.DateFilter?.ModifiedAfter),
            ModifiedBeforeExclusive: ParseDate(query.DateFilter?.ModifiedBeforeExclusive),
            MinSizeMb: query.SizeFilter?.MinMb,
            MaxSizeMb: query.SizeFilter?.MaxMb);

        static DateTime? ParseDate(string? value) =>
            value != null && DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    private static HashSet<string> CreateFilterOnlyAllowedExtensions(
        StructuredQuery query,
        CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> source = query.HardExtensions.Count > 0
            ? query.HardExtensions
            : query.PredictedExtensions.Count > 0
                ? query.PredictedExtensions
                : FileTypeMapper.GetExtensionsForTypes(query.FileTypes);
        foreach (var extension in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            allowed.Add(extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}");
        }
        return allowed;
    }

    private static HashSet<string>? ExpandFilterFolderNames(
        IReadOnlyList<FolderHint> hints,
        CancellationToken cancellationToken)
    {
        if (hints.Count == 0) return null;
        var mappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["downloads"] = ["downloads", "indirilenler", "download"],
            ["indirilenler"] = ["downloads", "indirilenler", "download"],
            ["desktop"] = ["desktop", "masaüstü", "masa üstü"],
            ["masaüstü"] = ["desktop", "masaüstü", "masa üstü"],
            ["documents"] = ["documents", "belgeler", "dökümanlar", "dokümanlar"],
            ["belgeler"] = ["documents", "belgeler", "dökümanlar", "dokümanlar"],
            ["pictures"] = ["pictures", "resimler", "fotograflar", "fotoğraflar"],
            ["resimler"] = ["pictures", "resimler", "fotograflar", "fotoğraflar"],
            ["music"] = ["music", "müzik", "muzik"],
            ["müzik"] = ["music", "müzik", "muzik"],
            ["videos"] = ["videos", "videolar", "video"],
            ["videolar"] = ["videos", "videolar", "video"]
        };
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in hints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hint.Weight <= 0.3) continue;
            var lowered = hint.Name.ToLowerInvariant();
            expanded.Add(lowered);
            if (!mappings.TryGetValue(lowered, out var alternatives))
            {
                if (lowered.Contains(' '))
                {
                    expanded.Add(lowered.Replace(' ', '.'));
                    expanded.Add(lowered.Replace(' ', '-'));
                    expanded.Add(lowered.Replace(' ', '_'));
                }
                continue;
            }
            foreach (var alternative in alternatives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded.Add(alternative);
            }
        }
        return expanded;
    }

    private static bool PathHasFolder(string path, HashSet<string> expandedFolderNames) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(expandedFolderNames.Contains);

    private static bool PassesFilterOnlyDate(SearchItem item, DateFilter? filter)
    {
        if (filter == null) return true;
        if (filter.CreatedAfter != null && DateTime.TryParse(filter.CreatedAfter, out var createdAfter) &&
            (item.CreatedTime == null || item.CreatedTime < createdAfter)) return false;
        if (filter.CreatedBeforeExclusive != null &&
            DateTime.TryParse(filter.CreatedBeforeExclusive, out var createdBeforeExclusive) &&
            (item.CreatedTime == null || item.CreatedTime >= createdBeforeExclusive)) return false;
        if (filter.ModifiedAfter != null && DateTime.TryParse(filter.ModifiedAfter, out var modifiedAfter) &&
            (item.LastWriteTime == null || item.LastWriteTime < modifiedAfter)) return false;
        if (filter.ModifiedBeforeExclusive != null &&
            DateTime.TryParse(filter.ModifiedBeforeExclusive, out var modifiedBeforeExclusive) &&
            (item.LastWriteTime == null || item.LastWriteTime >= modifiedBeforeExclusive)) return false;
        return true;
    }

    private static bool PassesFilterOnlySize(SearchItem item, SizeFilter? filter)
    {
        if (filter == null || item.IsDirectory) return true;
        if (item.SizeBytes == null) return false;
        var sizeMb = item.SizeBytes.Value / (1024.0 * 1024.0);
        return (!filter.MinMb.HasValue || sizeMb >= filter.MinMb.Value) &&
               (!filter.MaxMb.HasValue || sizeMb <= filter.MaxMb.Value);
    }

    private static IReadOnlyList<SearchTerm> GetSearchTerms(StructuredQuery query)
    {
        if (query.SearchTerms.Count > 0)
        {
            return query.SearchTerms
                .Where(term => !string.IsNullOrWhiteSpace(term.Text))
                .Select(term => new SearchTerm
                {
                    Text = term.Text,
                    Category = term.Category,
                    Role = term.Role,
                    AnchorGroup = term.AnchorGroup,
                    Weight = Math.Clamp(term.Weight, 0, 1)
                })
                .ToList();
        }

        return query.Keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => new SearchTerm
            {
                Text = keyword,
                Category = SearchTermCategory.Legacy,
                Role = SearchTermRole.Anchor,
                AnchorGroup = 0,
                Weight = 1
            })
            .ToList();
    }

    private Dictionary<string, (SearchItem node, MatchWeights matches)> GetCandidateNodes(
        StructuredQuery query,
        IReadOnlyList<SearchTerm> searchTerms,
        ISearchStateReader state,
        QueryCatalogFilter metadata,
        HashSet<string>? folderHints,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, (SearchItem node, MatchWeights matches)>(
            StringComparer.OrdinalIgnoreCase);
        var tokens = CollectTokens(query, searchTerms, cancellationToken);

        var includeContents = query.IncludeFolderContents &&
            query.TargetType is not { HasStrongPreference: true, PrefersFolder: true };
        QueryCatalogFilter? tokenFilter = query.OriginalText == null ? null : metadata with
        {
            PathSegments = null,
            IncludeDirectories = includeContents || metadata.IncludeDirectories,
            IncludeDirectoriesWithoutExtension = includeContents,
            FilterDirectoryDates = !includeContents && metadata.FilterDirectoryDates
        };
        foreach (var (token, weight, useFuzzy) in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddTokenMatches(candidates, token, weight, useFuzzy, state, tokenFilter, cancellationToken);
        }

        if (query.OriginalText == null && query.SearchTerms.Count == 0)
        {
            var spelling = tokens.ToDictionary(entry => SearchTextNormalizer.Fold(entry.Token), entry => entry.Token,
                StringComparer.OrdinalIgnoreCase);
            var keywords = searchTerms.Select(term => _tokenizer.Tokenize(term.Text)
                .Select(token => spelling[SearchTextNormalizer.Fold(token)])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()).Where(parts => parts.Length > 0).ToArray();
            if (keywords.Any(parts => parts.Length > 1))
            {
                foreach (var path in candidates.Keys.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var matches = candidates[path].matches;
                    if (!keywords.Any(parts => parts.All(matches.Contains))) candidates.Remove(path);
                }
            }
        }

        if (query.OriginalText != null && state is IQueryCatalogSnapshot catalogSnapshot)
        {
            var matchingFolders = includeContents
                ? candidates.Values.Where(candidate => candidate.node.IsDirectory &&
                    candidate.matches.CanExpandFolder)
                    .ToDictionary(candidate => candidate.node.FullPath, candidate => candidate.matches,
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MatchWeights>(StringComparer.OrdinalIgnoreCase);
            if (folderHints == null && matchingFolders.Count == 0) return candidates;
            var segments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (folderHints != null) segments.UnionWith(folderHints);
            foreach (var folder in matchingFolders.Keys) segments.Add(Path.GetFileName(folder));

            foreach (var item in catalogSnapshot.GetItems(metadata with { PathSegments = segments }, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!metadata.Matches(item)) continue;
                var parent = Path.GetDirectoryName(item.FullPath);
                if (parent == null) continue;
                if (folderHints != null && PathHasFolder(parent, folderHints))
                    candidates.TryAdd(item.FullPath, (item, default));
                if (item.IsDirectory || matchingFolders.Count == 0) continue;
                for (var ancestor = parent; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                {
                    if (!matchingFolders.TryGetValue(ancestor, out var matches)) continue;
                    foreach (var (token, weight) in matches.Terms())
                        AddMatch(candidates, item, token, weight * 0.65);
                }
            }
            return candidates;
        }

        if (folderHints != null)
        {
            var folderFilter = metadata with { PathSegments = folderHints };
            var items = state is IQueryCatalogSnapshot catalog
                ? catalog.GetItems(folderFilter, cancellationToken)
                : state.GetAllItems(cancellationToken).Where(folderFilter.Matches);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathHasFolder(Path.GetDirectoryName(item.FullPath) ?? "", folderHints))
                    candidates.TryAdd(item.FullPath, (item, default));
            }
        }

        if (includeContents)
        {
            foreach (var (folder, matches) in candidates.Values.Where(candidate => candidate.node.IsDirectory).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.OriginalText != null && !matches.CanExpandFolder)
                    continue;
                foreach (var child in state.GetDescendants(folder, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!child.IsDirectory && metadata.Matches(child))
                    {
                        foreach (var (token, weight) in matches.Terms())
                            AddMatch(candidates, child, token, query.OriginalText == null ? weight : weight * 0.65);
                    }
                }
                if (query.OriginalText == null) candidates.Remove(folder.FullPath);
            }
        }
        return candidates;
    }

    private (string Token, double Weight, bool UseFuzzy)[] CollectTokens(
        StructuredQuery query,
        IReadOnlyList<SearchTerm> searchTerms,
        CancellationToken cancellationToken)
    {
        var tokens = new Dictionary<string, (string Token, double Weight, bool UseFuzzy)>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in searchTerms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weight = term.Role == SearchTermRole.Anchor ? term.Weight : Math.Min(term.Weight, 0.65);
            foreach (var token in _tokenizer.Tokenize(term.Text))
                AddToken(token, weight, term.Role == SearchTermRole.Anchor);
        }
        foreach (var term in query.OriginalTerms)
        foreach (var token in _tokenizer.Tokenize(term))
            AddToken(token, 1.4, false);
        return tokens.Values.ToArray();

        void AddToken(string token, double weight, bool useFuzzy)
        {
            var key = SearchTextNormalizer.Fold(token);
            if (!tokens.TryGetValue(key, out var old))
                tokens[key] = (token, weight, useFuzzy);
            else
                tokens[key] = (old.Token, Math.Max(old.Weight, weight), old.UseFuzzy || useFuzzy);
        }
    }

    private static void AddTokenMatches(
        Dictionary<string, (SearchItem node, MatchWeights matches)> candidates,
        string token,
        double weight,
        bool useFuzzy,
        ISearchStateReader state,
        QueryCatalogFilter? filter,
        CancellationToken cancellationToken)
    {
        var found = false;
        var partialWeight = weight * 0.7;
        if (state is ITokenPartialSnapshot combined)
        {
            foreach (var (node, exact) in combined.GetWithPartial(token, filter, out found, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMatch(candidates, node, token, exact ? weight : partialWeight);
            }
        }
        else
        {
            AddSpelling(token);
            var folded = SearchTextNormalizer.Fold(token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(folded, token)) AddSpelling(folded);
        }

        void AddSpelling(string spelling)
        {
            foreach (var node in state.Get(spelling, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filter?.Matches(node) != false) AddMatch(candidates, node, token, weight);
                found = true;
            }
            foreach (var node in state.GetPartial(spelling, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filter?.Matches(node) != false) AddMatch(candidates, node, token, partialWeight);
                found = true;
            }
        }

        if (!found && useFuzzy && token.Length >= 4)
        {
            var maxDistance = token.Length >= 7 ? 2 : 1;
            foreach (var node in state.GetFuzzy(token, maxDistance, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filter?.Matches(node) != false) AddMatch(candidates, node, token, weight * 0.4);
            }
        }
    }

    private static void AddMatch(
        Dictionary<string, (SearchItem node, MatchWeights matches)> matches,
        SearchItem node,
        string token,
        double contribution)
    {
        ref var match = ref CollectionsMarshal.GetValueRefOrAddDefault(matches, node.FullPath, out var exists);
        if (!exists) match = (node, default);
        match.matches.Add(token, contribution);
    }

    private struct MatchWeights
    {
        private string? _firstToken;
        private double _firstWeight;
        private List<(string Token, double Weight)>? _others;

        public readonly bool HasMatches => _firstToken != null;

        public readonly bool Contains(string token) => StringComparer.OrdinalIgnoreCase.Equals(_firstToken, token) ||
            (_others?.Exists(match => StringComparer.OrdinalIgnoreCase.Equals(match.Token, token)) == true);
        public readonly bool CanExpandFolder =>
            (_firstToken is { Length: >= 4 } && _firstWeight >= 1) ||
            (_others?.Exists(match => match.Token.Length >= 4 && match.Weight >= 1) == true);

        public void Add(string token, double weight)
        {
            if (_firstToken == null)
            {
                _firstToken = token;
                _firstWeight = weight;
                return;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(_firstToken, token))
            {
                _firstWeight = Math.Max(_firstWeight, weight);
                return;
            }

            var others = _others ??= [];
            for (var i = 0; i < others.Count; i++)
            {
                var entry = others[i];
                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Token, token)) continue;
                if (weight > entry.Weight) others[i] = (entry.Token, weight);
                return;
            }
            others.Add((token, weight));
        }

        public readonly double Score(HashSet<string>? allowedTokens = null, double factor = 1)
        {
            var score = _firstToken != null && (allowedTokens == null || allowedTokens.Contains(_firstToken))
                ? _firstWeight * factor : 0;
            if (_others != null)
            {
                foreach (var (token, weight) in _others)
                    if (allowedTokens == null || allowedTokens.Contains(token)) score += weight * factor;
            }
            return score;
        }

        public readonly IEnumerable<(string Token, double Weight)> Terms()
        {
            if (_firstToken != null) yield return (_firstToken, _firstWeight);
            if (_others != null)
                foreach (var entry in _others) yield return entry;
        }
    }

    private sealed record QueryScoringContext(
        IReadOnlyList<(string Normalized, double Weight)> ExactAndPhraseTerms,
        IReadOnlyList<(string Lowered, double Weight)> FolderHints,
        IReadOnlyList<(int Group, string[] Tokens, double Weight)> FolderContext,
        Dictionary<string, double> ContextTokenWeights);

    private QueryScoringContext CreateScoringContext(
        StructuredQuery query,
        IReadOnlyList<SearchTerm> searchTerms)
    {
        var exactAndPhraseTerms = searchTerms
            .Where(term =>
                term.Role == SearchTermRole.Phrase ||
                (term.Role == SearchTermRole.Anchor && term.Category == SearchTermCategory.Exact))
            .Select(term => (Normalized: NormalizeForComparison(term.Text), term.Weight))
            .ToList();
        if (query.OriginalTerms.Count > 0)
        {
            exactAndPhraseTerms.Add((
                NormalizeForComparison(string.Join(' ', query.OriginalTerms)), 1.4));
        }

        var original = " " + NormalizeForComparison(query.OriginalText ?? "") + " ";
        var folderHints = query.FolderHints.Select(hint => (
                Lowered: NormalizeForComparison(hint.Name), Weight: Math.Clamp(hint.Weight, 0, 1)))
            .Select(hint => (hint.Lowered, Weight: hint.Weight *
                (original.Contains(" " + hint.Lowered + " ", StringComparison.OrdinalIgnoreCase) ? 400 : 100)))
            .ToList();
        var folderContext = query.FolderContextTerms.Select(term => (
            Group: term.AnchorGroup,
            Tokens: _tokenizer.Tokenize(term.Text).Select(SearchTextNormalizer.Fold)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Weight: Math.Clamp(term.Weight, 0, 1))).ToList();

        var contextTokenWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in searchTerms.Where(term => term.Role == SearchTermRole.Context))
        {
            foreach (var token in _tokenizer.Tokenize(term.Text))
            {
                if (!contextTokenWeights.TryGetValue(token, out var current) || term.Weight > current)
                {
                    contextTokenWeights[token] = term.Weight;
                }
            }
        }

        return new QueryScoringContext(exactAndPhraseTerms, folderHints, folderContext, contextTokenWeights);
    }

    private double CalculateScore(
        StructuredQuery query,
        SearchItem node,
        MatchWeights matches,
        QueryScoringContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var score = matches.Score() * 100;
        if (matches.HasMatches && node.FullPath.Contains(Path.DirectorySeparatorChar))
        {
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(node.FullPath) ?? string.Empty);
            var folderTokens = _tokenizer.Tokenize(parentFolder).ToHashSet();
            score += matches.Score(folderTokens, 60);
        }

        if (context.FolderHints.Count > 0 || context.FolderContext.Count > 0)
        {
            var segments = (Path.GetDirectoryName(node.FullPath) ?? "")
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var names = segments.Select(NormalizeForComparison).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (lowered, weight) in context.FolderHints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (names.Contains(lowered))
                {
                    score += weight;
                }
            }
            if (context.FolderContext.Count > 0)
            {
                var folderTokens = segments.Select(segment => _tokenizer.Tokenize(segment)
                    .Select(SearchTextNormalizer.Fold).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
                score += context.FolderContext
                    .Where(term => term.Tokens.Length > 0 && folderTokens.Any(segment => term.Tokens.All(segment.Contains)))
                    .GroupBy(term => term.Group)
                    .Sum(group => group.Max(term => term.Weight) * 100);
            }
        }

        if (query.TargetType != null)
        {
            if (node.IsDirectory && query.TargetType.PrefersFolder)
            {
                score += 150 * query.TargetType.Folder;
            }
            else if (!node.IsDirectory && query.TargetType.PrefersFile)
            {
                score += 100 * query.TargetType.File;
            }
        }

        if (!node.IsDirectory && query.SoftExtensions.Count > 0)
        {
            var extension = Path.GetExtension(node.Name).TrimStart('.');
            var softIndex = query.SoftExtensions.FindIndex(value => string.Equals(
                value.TrimStart('.'),
                extension,
                StringComparison.OrdinalIgnoreCase));
            if (softIndex >= 0)
            {
                score += Math.Max(10, 35 - softIndex * 5);
            }
        }

        if (context.ExactAndPhraseTerms.Count > 0 || context.ContextTokenWeights.Count > 0)
        {
            var fileName = Path.GetFileNameWithoutExtension(node.Name);
            var normalizedName = NormalizeForComparison(fileName);
            foreach (var (normalizedTerm, weight) in context.ExactAndPhraseTerms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (normalizedName == normalizedTerm)
                {
                    score += 150 * weight;
                }
                else if (normalizedTerm.Length > 1 && normalizedName.Contains(normalizedTerm))
                {
                    score += 75 * weight;
                }
            }

            if (context.ContextTokenWeights.Count > 0)
            {
                var nameTokens = _tokenizer.Tokenize(fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                score += context.ContextTokenWeights
                    .Where(item => nameTokens.Contains(item.Key))
                    .Sum(item => item.Value * 30);
            }
        }

        return score + node.OpenCount * 2;
    }

    private string NormalizeForComparison(string value) =>
        string.Join(' ', _tokenizer.Tokenize(value).Select(SearchTextNormalizer.Fold).Distinct(StringComparer.Ordinal));

    private static IEnumerable<FileSystemNode> GetTreeNodes(
        FileSystemNode root,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<FileSystemNode>(root.Children.Reverse());
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            yield return node;
            foreach (var child in node.Children.Reverse())
            {
                pending.Push(child);
            }
        }
    }
}
