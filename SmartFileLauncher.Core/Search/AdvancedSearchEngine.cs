using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Search;

public class AdvancedSearchEngine
{
    private readonly Func<CancellationToken, SearchState> _stateProvider;
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
        Func<CancellationToken, SearchState> stateProvider,
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var state = _stateProvider(cancellationToken);
        if (state.ItemCount == 0 || maxResults <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        List<(SearchItem node, Dictionary<string, double> matches)> candidates;
        var searchTerms = GetSearchTerms(query);
        if (query.FilterOnlyMode || searchTerms.Count == 0)
        {
            candidates = GetAllFilesForFiltering(query, state, cancellationToken);
        }
        else
        {
            candidates = GetCandidateNodes(searchTerms, state, cancellationToken);
        }

        if (query.TargetType != null && query.TargetType.HasStrongPreference)
        {
            if (query.TargetType.PrefersFolder)
            {
                candidates = candidates.Where(candidate =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return candidate.node.IsDirectory;
                }).ToList();
            }
            else if (query.TargetType.PrefersFile && query.TargetType.File > 0.7)
            {
                candidates = candidates.Where(candidate =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return !candidate.node.IsDirectory;
                }).ToList();
            }
        }

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (query.HardExtensions.Any())
        {
            foreach (var extension in query.HardExtensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}");
            }
        }
        else if (query.PredictedExtensions.Any())
        {
            foreach (var extension in query.PredictedExtensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}");
            }
        }
        else if (query.FileTypes.Any())
        {
            foreach (var extension in FileTypeMapper.GetExtensionsForTypes(query.FileTypes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(extension);
            }
        }

        var userWantsFolders = query.TargetType?.PrefersFolder == true;
        if (allowedExtensions.Any() && !userWantsFolders)
        {
            candidates = candidates.Where(candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.node.IsDirectory && query.IncludeFolderContents)
                {
                    return true;
                }

                return allowedExtensions.Contains(Path.GetExtension(candidate.node.Name));
            }).ToList();
        }

        var finalResults = new List<(SearchItem node, Dictionary<string, double> matches)>();
        foreach (var (node, matches) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.IsDirectory)
            {
                finalResults.Add((node, matches));
                continue;
            }

            if (userWantsFolders)
            {
                finalResults.Add((node, matches));
            }
            else if (query.IncludeFolderContents)
            {
                var children = state.GetDescendants(node, cancellationToken);
                if (allowedExtensions.Any())
                {
                    children = children.Where(child =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return !child.IsDirectory &&
                               allowedExtensions.Contains(Path.GetExtension(child.Name));
                    }).ToArray();
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    finalResults.Add((child, new Dictionary<string, double>(matches)));
                }
            }
        }

        finalResults = finalResults
            .GroupBy(item => item.node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.First().node,
                MergeMatches(group.Select(item => item.matches))))
            .ToList();

        if (query.DateFilter != null)
        {
            finalResults = ApplyDateFilter(finalResults, query.DateFilter, cancellationToken);
        }

        if (query.SizeFilter != null)
        {
            finalResults = ApplySizeFilter(finalResults, query.SizeFilter, cancellationToken);
        }

        var scoredResults = new List<SearchResult>(finalResults.Count);
        foreach (var (node, matches) in finalResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scoredResults.Add(new SearchResult
            {
                Name = node.Name,
                FullPath = node.FullPath,
                Score = CalculateScore(query, node, matches, searchTerms, cancellationToken),
                IsDirectory = node.IsDirectory
            });
        }

        var queue = new PriorityQueue<SearchResult, SearchResult>(SearchResultOrder.Instance);
        foreach (var result in scoredResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            queue.Enqueue(result, result);
        }

        var results = new List<SearchResult>(Math.Min(maxResults, queue.Count));
        while (queue.Count > 0 && results.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(queue.Dequeue());
        }

        return results;
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

    private List<(SearchItem node, Dictionary<string, double> matches)> GetCandidateNodes(
        IReadOnlyList<SearchTerm> searchTerms,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var anchorGroups = searchTerms
            .Where(term => term.Role == SearchTermRole.Anchor)
            .GroupBy(term => term.AnchorGroup)
            .OrderBy(group => group.Key)
            .ToList();
        if (anchorGroups.Count == 0)
        {
            return [];
        }

        Dictionary<string, (SearchItem node, Dictionary<string, double> matches)>? candidates = null;
        foreach (var anchorGroup in anchorGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupMatches = new Dictionary<string, (SearchItem node, Dictionary<string, double> matches)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var searchTerm in anchorGroup)
            {
                MergeCandidateUnion(groupMatches, GetTermMatches(searchTerm, state, cancellationToken));
            }

            if (groupMatches.Count == 0)
            {
                return [];
            }

            if (candidates == null)
            {
                candidates = groupMatches;
                continue;
            }

            foreach (var path in candidates.Keys.ToArray())
            {
                if (!groupMatches.TryGetValue(path, out var groupMatch))
                {
                    candidates.Remove(path);
                    continue;
                }

                MergeContributions(candidates[path].matches, groupMatch.matches);
            }

            if (candidates.Count == 0)
            {
                return [];
            }
        }

        return candidates?.Values.ToList() ?? [];
    }

    private Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> GetTermMatches(
        SearchTerm searchTerm,
        SearchState state,
        CancellationToken cancellationToken)
    {
        // Tokenizer bir kelime için aslını ve —farklıysa— aksansız biçimini
        // üretir. Bunlar aynı kelimenin **alternatifleridir**; terimdeki ayrı
        // kelimeler ise birlikte aranmalıdır. Aksansız biçim iki alternatifte
        // de ortak olduğundan gruplama anahtarı odur: grup içi birleşim (VEYA),
        // gruplar arası kesişim (VE). Gruplanmazsa `görüşme` sorgusu
        // `gorusme.txt`'yi hiç bulamaz.
        var alternativeGroups = _tokenizer.Tokenize(searchTerm.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(SearchTextNormalizer.Fold, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<string, (SearchItem node, Dictionary<string, double> matches)>? termMatches = null;
        foreach (var group in alternativeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> groupMatches =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (var token in group)
            {
                MergeCandidateUnion(
                    groupMatches,
                    GetTokenMatches(token, searchTerm.Weight, state, cancellationToken));
            }

            if (groupMatches.Count == 0)
            {
                return [];
            }

            if (termMatches == null)
            {
                termMatches = groupMatches;
                continue;
            }

            foreach (var path in termMatches.Keys.ToArray())
            {
                if (!groupMatches.TryGetValue(path, out var groupMatch))
                {
                    termMatches.Remove(path);
                    continue;
                }

                MergeContributions(termMatches[path].matches, groupMatch.matches);
            }
        }

        return termMatches ?? [];
    }

    private static void MergeCandidateUnion(
        Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> destination,
        Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> source)
    {
        foreach (var (path, candidate) in source)
        {
            if (destination.TryGetValue(path, out var existing))
            {
                MergeContributions(existing.matches, candidate.matches);
            }
            else
            {
                destination[path] = (
                    candidate.node,
                    new Dictionary<string, double>(candidate.matches, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> GetTokenMatches(
        string token,
        double weight,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var matches = new Dictionary<string, (SearchItem node, Dictionary<string, double> matches)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in state.Get(token, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMatch(matches, node, token, weight);
        }

        var allowPartial = token.Length >= 4 ||
            (token.Length >= 3 && token.Any(char.IsDigit));
        if (allowPartial)
        {
            foreach (var node in state.GetPartial(token, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMatch(matches, node, token, weight * 0.7);
            }
        }

        if (matches.Count == 0 && token.Length >= 4)
        {
            var maxDistance = token.Length >= 7 ? 2 : 1;
            foreach (var node in state.GetFuzzy(token, maxDistance, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMatch(matches, node, token, weight * 0.4);
            }
        }

        return matches;
    }

    private List<(SearchItem node, Dictionary<string, double> matches)> GetAllFilesForFiltering(
        StructuredQuery query,
        SearchState state,
        CancellationToken cancellationToken)
    {
        var allNodes = state.GetAllItems(cancellationToken).ToList();
        if (query.FolderHints.Any())
        {
            var folderNames = query.FolderHints
                .Where(hint =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return hint.Weight > 0.3;
                })
                .Select(hint => hint.Name.ToLowerInvariant())
                .ToHashSet();
            var folderMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
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
            var expandedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folderName in folderNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expandedFolderNames.Add(folderName);
                if (folderMappings.TryGetValue(folderName, out var mappings))
                {
                    foreach (var mapping in mappings)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        expandedFolderNames.Add(mapping);
                    }
                }
            }

            allNodes = allNodes.Where(node =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathParts = node.FullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return pathParts.Any(part => expandedFolderNames.Contains(part));
            }).ToList();
        }

        return allNodes
            .Select(node => (node, new Dictionary<string, double>()))
            .ToList();
    }

    private static void AddMatch(
        Dictionary<string, (SearchItem node, Dictionary<string, double> matches)> matches,
        SearchItem node,
        string token,
        double contribution)
    {
        if (!matches.TryGetValue(node.FullPath, out var match))
        {
            match = (node, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
            matches[node.FullPath] = match;
        }

        if (!match.matches.TryGetValue(token, out var current) || contribution > current)
        {
            match.matches[token] = contribution;
        }
    }

    private static Dictionary<string, double> MergeMatches(
        IEnumerable<Dictionary<string, double>> sources)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var (token, contribution) in source)
            {
                if (!result.TryGetValue(token, out var current) || contribution > current)
                {
                    result[token] = contribution;
                }
            }
        }

        return result;
    }

    private static void MergeContributions(
        Dictionary<string, double> destination,
        Dictionary<string, double> source)
    {
        foreach (var (token, contribution) in source)
        {
            if (!destination.TryGetValue(token, out var current) || contribution > current)
            {
                destination[token] = contribution;
            }
        }
    }

    private static List<(SearchItem node, Dictionary<string, double> matches)> ApplyDateFilter(
        List<(SearchItem node, Dictionary<string, double> matches)> candidates,
        DateFilter filter,
        CancellationToken cancellationToken) =>
        candidates.Where(candidate =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.CreatedAfter != null && DateTime.TryParse(filter.CreatedAfter, out var createdAfter) &&
                (candidate.node.CreatedTime == null || candidate.node.CreatedTime < createdAfter))
            {
                return false;
            }

            if (filter.CreatedBeforeExclusive != null &&
                DateTime.TryParse(filter.CreatedBeforeExclusive, out var createdBeforeExclusive) &&
                (candidate.node.CreatedTime == null || candidate.node.CreatedTime >= createdBeforeExclusive))
            {
                return false;
            }

            if (filter.ModifiedAfter != null && DateTime.TryParse(filter.ModifiedAfter, out var modifiedAfter) &&
                (candidate.node.LastWriteTime == null || candidate.node.LastWriteTime < modifiedAfter))
            {
                return false;
            }

            if (filter.ModifiedBeforeExclusive != null &&
                DateTime.TryParse(filter.ModifiedBeforeExclusive, out var modifiedBeforeExclusive) &&
                (candidate.node.LastWriteTime == null || candidate.node.LastWriteTime >= modifiedBeforeExclusive))
            {
                return false;
            }

            return true;
        }).ToList();

    private static List<(SearchItem node, Dictionary<string, double> matches)> ApplySizeFilter(
        List<(SearchItem node, Dictionary<string, double> matches)> candidates,
        SizeFilter filter,
        CancellationToken cancellationToken) =>
        candidates.Where(candidate =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.node.IsDirectory)
            {
                return true;
            }

            if (candidate.node.SizeBytes == null)
            {
                return false;
            }

            var sizeMb = candidate.node.SizeBytes.Value / (1024.0 * 1024.0);
            return (!filter.MinMb.HasValue || sizeMb >= filter.MinMb.Value) &&
                   (!filter.MaxMb.HasValue || sizeMb <= filter.MaxMb.Value);
        }).ToList();

    private double CalculateScore(
        StructuredQuery query,
        SearchItem node,
        Dictionary<string, double> matches,
        IReadOnlyList<SearchTerm> searchTerms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var score = matches.Values.Sum() * 100;
        if (node.FullPath.Contains(Path.DirectorySeparatorChar))
        {
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(node.FullPath) ?? string.Empty);
            var folderTokens = _tokenizer.Tokenize(parentFolder).ToHashSet();
            score += matches
                .Where(match => folderTokens.Contains(match.Key))
                .Sum(match => match.Value * 60);
        }

        if (query.FolderHints.Any())
        {
            var pathLower = node.FullPath.ToLowerInvariant();
            foreach (var hint in query.FolderHints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pathLower.Contains(hint.Name.ToLowerInvariant()))
                {
                    score += 100 * Math.Clamp(hint.Weight, 0, 1);
                }
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

        var fileName = Path.GetFileNameWithoutExtension(node.Name);
        var normalizedName = NormalizeForComparison(fileName);
        foreach (var term in searchTerms.Where(term =>
                     term.Role == SearchTermRole.Phrase ||
                     (term.Role == SearchTermRole.Anchor && term.Category == SearchTermCategory.Exact)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTerm = NormalizeForComparison(term.Text);
            if (normalizedName == normalizedTerm)
            {
                score += 150 * term.Weight;
            }
            else if (normalizedTerm.Length > 1 && normalizedName.Contains(normalizedTerm))
            {
                score += 75 * term.Weight;
            }
        }

        var nameTokens = _tokenizer.Tokenize(fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        score += contextTokenWeights
            .Where(item => nameTokens.Contains(item.Key))
            .Sum(item => item.Value * 30);
        return score + node.OpenCount * 2;
    }

    private string NormalizeForComparison(string value) =>
        string.Join(' ', _tokenizer.Tokenize(value));

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
