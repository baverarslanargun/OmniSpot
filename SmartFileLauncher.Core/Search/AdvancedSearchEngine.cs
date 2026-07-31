using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Search;

/// <summary>
/// Advanced search engine that accepts StructuredQuery from intent parser
/// Applies file type filters, date filters, and folder content expansion
/// </summary>
public class AdvancedSearchEngine {
    private readonly Func<CancellationToken, SearchSnapshot> _snapshotProvider;
    private readonly ITokenizer _tokenizer;
    private readonly IScoringStrategy _scoring;
    
    public AdvancedSearchEngine(
        InvertedIndex invertedIndex, 
        ITokenizer tokenizer, 
        IScoringStrategy scoring,
        FileSystemNode rootNode) {
        ArgumentNullException.ThrowIfNull(invertedIndex);
        ArgumentNullException.ThrowIfNull(rootNode);
        _snapshotProvider = cancellationToken =>
            SearchSnapshot.Create(invertedIndex, rootNode, cancellationToken);
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }

    public AdvancedSearchEngine(
        Func<CancellationToken, SearchSnapshot> snapshotProvider,
        ITokenizer tokenizer,
        IScoringStrategy scoring) {
        _snapshotProvider = snapshotProvider ??
            throw new ArgumentNullException(nameof(snapshotProvider));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }
    
    public IReadOnlyList<SearchResult> Search(
        StructuredQuery query,
        int maxResults = 100,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshotProvider(cancellationToken);
        var rootNode = snapshot.RootNode;
        if (rootNode == null || maxResults <= 0) {
            return Array.Empty<SearchResult>();
        }

        // 1. Get candidate nodes
        List<(FileSystemNode node, Dictionary<string, double> matches)> candidates;
        var searchTerms = GetSearchTerms(query);

        if (query.FilterOnlyMode || searchTerms.Count == 0) {
            // Filter-only mode: get all files, apply filters (no keyword matching)
            candidates = GetAllFilesForFiltering(query, rootNode, cancellationToken);
        } else {
            // Keyword search mode: find files matching keywords
            candidates = GetCandidateNodes(
                searchTerms,
                snapshot.InvertedIndex,
                rootNode,
                cancellationToken);
        }
        
        // 2. Apply target type filter (file vs folder preference)
        if (query.TargetType != null && query.TargetType.HasStrongPreference) {
            if (query.TargetType.PrefersFolder) {
                // User wants folders - filter to only directories
                candidates = candidates.Where(n => {
                    cancellationToken.ThrowIfCancellationRequested();
                    return n.node.IsDirectory;
                }).ToList();
            }
            else if (query.TargetType.PrefersFile && query.TargetType.File > 0.7) {
                // User strongly wants files - exclude directories
                candidates = candidates.Where(n => {
                    cancellationToken.ThrowIfCancellationRequested();
                    return !n.node.IsDirectory;
                }).ToList();
            }
        }
        
        // 3. Apply file type and extension filters
        HashSet<string> allowedExtensions = new(StringComparer.OrdinalIgnoreCase);
        
        // Priority 1: Use AI-predicted specific extensions if available
        if (query.HardExtensions.Any()) {
            foreach (var ext in query.HardExtensions) {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(ext.StartsWith(".") ? ext : $".{ext}");
            }
        }
        else if (query.PredictedExtensions.Any()) {
            foreach (var ext in query.PredictedExtensions) {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(ext.StartsWith(".") ? ext : $".{ext}");
            }
        }
        else if (query.FileTypes.Any()) {
            var mappedExtensions = FileTypeMapper.GetExtensionsForTypes(query.FileTypes);
            foreach (var ext in mappedExtensions) {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(ext);
            }
        }
        
        // Apply extension filtering (skip if user wants folders)
        bool userWantsFolders = query.TargetType?.PrefersFolder == true;
        if (allowedExtensions.Any() && !userWantsFolders) {
            candidates = candidates.Where(n => {
                cancellationToken.ThrowIfCancellationRequested();
                if (n.node.IsDirectory && query.IncludeFolderContents) {
                    // Keep folders that might contain matching files
                    return true;
                }
                var ext = Path.GetExtension(n.node.Name);
                return allowedExtensions.Contains(ext);
            }).ToList();
        }
        
        var finalResults = new List<(FileSystemNode node, Dictionary<string, double> matches)>();
        foreach (var (node, matches) in candidates) {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.IsDirectory) {
                // If user prefers folders, add the folder itself
                if (userWantsFolders) {
                    finalResults.Add((node, matches));
                }
                else if (query.IncludeFolderContents) {
                    // Add all children of matching folders
                    var children = GetAllChildren(node, cancellationToken);
                    
                    // Filter children by extension
                    if (allowedExtensions.Any()) {
                        children = children.Where(c => {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (c.IsDirectory) return false;
                            var ext = Path.GetExtension(c.Name);
                            return allowedExtensions.Contains(ext);
                        }).ToList();
                    }
                    
                    foreach (var child in children) {
                        cancellationToken.ThrowIfCancellationRequested();
                        finalResults.Add((child, new Dictionary<string, double>(matches)));
                    }
                }
            } else {
                finalResults.Add((node, matches));
            }
        }

        finalResults = finalResults
            .GroupBy(item => item.node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.First().node,
                MergeMatches(group.Select(item => item.matches))))
            .ToList();

        if (query.DateFilter != null) {
            finalResults = ApplyDateFilter(finalResults, query.DateFilter, cancellationToken);
        }

        if (query.SizeFilter != null) {
            finalResults = ApplySizeFilter(finalResults, query.SizeFilter, cancellationToken);
        }

        var pq = new PriorityQueue<SearchResult, double>();
        foreach (var (node, matches) in finalResults) {
            cancellationToken.ThrowIfCancellationRequested();
            double score = CalculateScore(
                query,
                node,
                matches,
                searchTerms,
                cancellationToken);
            pq.Enqueue(new SearchResult { 
                Name = node.Name, 
                FullPath = node.FullPath, 
                Score = score,
                IsDirectory = node.IsDirectory
            }, -score);
        }
        
        // 8. Return top results
        var results = new List<SearchResult>(Math.Min(maxResults, pq.Count));
        int count = 0;
        while (pq.Count > 0 && count < maxResults) {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(pq.Dequeue());
            count++;
        }

        return results;
    }
    
    private static IReadOnlyList<SearchTerm> GetSearchTerms(StructuredQuery query) {
        if (query.SearchTerms.Count > 0) {
            return query.SearchTerms
                .Where(term => !string.IsNullOrWhiteSpace(term.Text))
                .Select(term => new SearchTerm {
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
            .Select(keyword => new SearchTerm {
                Text = keyword,
                Category = SearchTermCategory.Legacy,
                Role = SearchTermRole.Anchor,
                AnchorGroup = 0,
                Weight = 1
            })
            .ToList();
    }

    private List<(FileSystemNode node, Dictionary<string, double> matches)> GetCandidateNodes(
        IReadOnlyList<SearchTerm> searchTerms,
        InvertedIndexSnapshot invertedIndex,
        FileSystemNode rootNode,
        CancellationToken cancellationToken) {
        var anchorGroups = searchTerms
            .Where(term => term.Role == SearchTermRole.Anchor)
            .GroupBy(term => term.AnchorGroup)
            .OrderBy(group => group.Key)
            .ToList();
        if (anchorGroups.Count == 0) {
            return [];
        }

        Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)>? candidates = null;
        foreach (var anchorGroup in anchorGroups) {
            cancellationToken.ThrowIfCancellationRequested();
            var groupMatches = new Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var searchTerm in anchorGroup) {
                var termMatches = GetTermMatches(
                    searchTerm,
                    invertedIndex,
                    cancellationToken);
                MergeCandidateUnion(groupMatches, termMatches);
            }

            if (groupMatches.Count == 0) {
                return [];
            }

            if (candidates == null) {
                candidates = groupMatches;
                continue;
            }

            foreach (var path in candidates.Keys.ToList()) {
                if (!groupMatches.TryGetValue(path, out var groupMatch)) {
                    candidates.Remove(path);
                    continue;
                }

                MergeContributions(candidates[path].matches, groupMatch.matches);
            }

            if (candidates.Count == 0) {
                return [];
            }
        }

        return candidates?.Values.ToList() ?? [];
    }

    private Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)> GetTermMatches(
        SearchTerm searchTerm,
        InvertedIndexSnapshot invertedIndex,
        CancellationToken cancellationToken) {
        var tokens = _tokenizer.Tokenize(searchTerm.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)>? termMatches = null;
        foreach (var token in tokens) {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenMatches = GetTokenMatches(
                token,
                searchTerm.Weight,
                invertedIndex,
                cancellationToken);
            if (tokenMatches.Count == 0) {
                return [];
            }

            if (termMatches == null) {
                termMatches = tokenMatches;
                continue;
            }

            foreach (var path in termMatches.Keys.ToList()) {
                if (!tokenMatches.TryGetValue(path, out var tokenMatch)) {
                    termMatches.Remove(path);
                    continue;
                }

                MergeContributions(termMatches[path].matches, tokenMatch.matches);
            }
        }

        return termMatches ?? [];
    }

    private static void MergeCandidateUnion(
        Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)> destination,
        Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)> source) {
        foreach (var (path, candidate) in source) {
            if (destination.TryGetValue(path, out var existing)) {
                MergeContributions(existing.matches, candidate.matches);
            }
            else {
                destination[path] = (
                    candidate.node,
                    new Dictionary<string, double>(candidate.matches, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)> GetTokenMatches(
        string token,
        double weight,
        InvertedIndexSnapshot invertedIndex,
        CancellationToken cancellationToken) {
        var matches = new Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in invertedIndex.Get(token)) {
            cancellationToken.ThrowIfCancellationRequested();
            AddMatch(matches, node, token, weight);
        }

        var allowPartial = token.Length >= 4 ||
            (token.Length >= 3 && token.Any(char.IsDigit));
        if (allowPartial) {
            foreach (var node in invertedIndex.GetPartial(token, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                AddMatch(matches, node, token, weight * 0.7);
            }
        }

        if (matches.Count == 0 && token.Length >= 4) {
            var maxDistance = token.Length >= 7 ? 2 : 1;
            foreach (var node in invertedIndex.GetFuzzy(
                         token,
                         maxDistance,
                         cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                AddMatch(matches, node, token, weight * 0.4);
            }
        }

        return matches;
    }

    private static void MergeContributions(
        Dictionary<string, double> destination,
        Dictionary<string, double> source) {
        foreach (var (token, contribution) in source) {
            if (!destination.TryGetValue(token, out var current) || contribution > current) {
                destination[token] = contribution;
            }
        }
    }

    /// <summary>
    /// Gets all files for filter-only mode, applying folder hints if present.
    /// In filter-only mode, we return all files (not searching by keywords),
    /// just applying type/date/size filters.
    /// </summary>
    private List<(FileSystemNode node, Dictionary<string, double> matches)> GetAllFilesForFiltering(
        StructuredQuery query,
        FileSystemNode rootNode,
        CancellationToken cancellationToken) {
        var allNodes = GetAllChildren(rootNode, cancellationToken);
        
        // Apply folder hints if present
        if (query.FolderHints.Any()) {
            var folderNames = query.FolderHints
                .Where(h => {
                    cancellationToken.ThrowIfCancellationRequested();
                    return h.Weight > 0.3;
                })
                .Select(h => h.Name.ToLowerInvariant())
                .ToHashSet();
            
            // Common folder name mappings (Turkish/English)
            var folderMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
                ["downloads"] = new[] { "downloads", "indirilenler", "download" },
                ["indirilenler"] = new[] { "downloads", "indirilenler", "download" },
                ["desktop"] = new[] { "desktop", "masaüstü", "masa üstü" },
                ["masaüstü"] = new[] { "desktop", "masaüstü", "masa üstü" },
                ["documents"] = new[] { "documents", "belgeler", "dökümanlar", "dokümanlar" },
                ["belgeler"] = new[] { "documents", "belgeler", "dökümanlar", "dokümanlar" },
                ["pictures"] = new[] { "pictures", "resimler", "fotograflar", "fotoğraflar" },
                ["resimler"] = new[] { "pictures", "resimler", "fotograflar", "fotoğraflar" },
                ["music"] = new[] { "music", "müzik", "muzik" },
                ["müzik"] = new[] { "music", "müzik", "muzik" },
                ["videos"] = new[] { "videos", "videolar", "video" },
                ["videolar"] = new[] { "videos", "videolar", "video" }
            };
            
            // Expand folder names with mappings
            var expandedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folderName in folderNames) {
                cancellationToken.ThrowIfCancellationRequested();
                expandedFolderNames.Add(folderName);
                if (folderMappings.TryGetValue(folderName, out var mappings)) {
                    foreach (var mapping in mappings) {
                        cancellationToken.ThrowIfCancellationRequested();
                        expandedFolderNames.Add(mapping);
                    }
                }
            }
            
            // Filter nodes to only those in matching folders
            allNodes = allNodes.Where(n => {
                cancellationToken.ThrowIfCancellationRequested();
                var pathParts = n.FullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return pathParts.Any(part => expandedFolderNames.Contains(part));
            }).ToList();
        }
        
        return allNodes
            .Select(n => (n, new Dictionary<string, double>()))
            .ToList();
    }

    private static void AddMatch(
        Dictionary<string, (FileSystemNode node, Dictionary<string, double> matches)> matches,
        FileSystemNode node,
        string token,
        double contribution) {
        if (!matches.ContainsKey(node.FullPath)) {
            matches[node.FullPath] = (node, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
        }

        if (!matches[node.FullPath].matches.TryGetValue(token, out var current) ||
            contribution > current) {
            matches[node.FullPath].matches[token] = contribution;
        }
    }

    private static Dictionary<string, double> MergeMatches(
        IEnumerable<Dictionary<string, double>> sources) {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources) {
            foreach (var (token, contribution) in source) {
                if (!result.TryGetValue(token, out var current) ||
                    contribution > current) {
                    result[token] = contribution;
                }
            }
        }

        return result;
    }
    
    private List<(FileSystemNode node, Dictionary<string, double> matches)> ApplyDateFilter(
        List<(FileSystemNode node, Dictionary<string, double> matches)> candidates,
        DateFilter filter,
        CancellationToken cancellationToken) {
        
        return candidates.Where(c => {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = c.node.Metadata;
            if (metadata == null) return false;
            
            if (filter.CreatedAfter != null && DateTime.TryParse(filter.CreatedAfter, out var createdAfter)) {
                if (metadata.CreatedTime == null || metadata.CreatedTime < createdAfter) {
                    return false;
                }
            }
            
            if (filter.CreatedBeforeExclusive != null &&
                DateTime.TryParse(filter.CreatedBeforeExclusive, out var createdBeforeExclusive)) {
                if (metadata.CreatedTime == null || metadata.CreatedTime >= createdBeforeExclusive) {
                    return false;
                }
            }
            
            if (filter.ModifiedAfter != null && DateTime.TryParse(filter.ModifiedAfter, out var modifiedAfter)) {
                if (metadata.LastWriteTime == null || metadata.LastWriteTime < modifiedAfter) {
                    return false;
                }
            }
            
            if (filter.ModifiedBeforeExclusive != null &&
                DateTime.TryParse(filter.ModifiedBeforeExclusive, out var modifiedBeforeExclusive)) {
                if (metadata.LastWriteTime == null || metadata.LastWriteTime >= modifiedBeforeExclusive) {
                    return false;
                }
            }
            
            return true;
        }).ToList();
    }

    private List<(FileSystemNode node, Dictionary<string, double> matches)> ApplySizeFilter(
        List<(FileSystemNode node, Dictionary<string, double> matches)> candidates,
        SizeFilter filter,
        CancellationToken cancellationToken)
    {
        return candidates.Where(c => {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.node.IsDirectory) return true; // Don't filter folders by size
            var sizeBytes = c.node.Metadata?.SizeBytes;
            if (sizeBytes == null) return false;

            double sizeMb = sizeBytes.Value / (1024.0 * 1024.0);

            if (filter.MinMb.HasValue && sizeMb < filter.MinMb.Value) return false;
            if (filter.MaxMb.HasValue && sizeMb > filter.MaxMb.Value) return false;

            return true;
        }).ToList();
    }
    
    private List<FileSystemNode> GetAllChildren(
        FileSystemNode node,
        CancellationToken cancellationToken) {
        var result = new List<FileSystemNode>();
        var pending = new Stack<FileSystemNode>(node.Children.Reverse());

        while (pending.Count > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            var child = pending.Pop();
            result.Add(child);

            if (child.IsDirectory) {
                foreach (var descendant in child.Children.Reverse()) {
                    pending.Push(descendant);
                }
            }
        }

        return result;
    }
    
    private double CalculateScore(
        StructuredQuery query,
        FileSystemNode node,
        Dictionary<string, double> matches,
        IReadOnlyList<SearchTerm> searchTerms,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        double score = matches.Values.Sum() * 100;

        if (node.FullPath.Contains(Path.DirectorySeparatorChar)) {
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(node.FullPath) ?? "");
            var folderTokens = _tokenizer.Tokenize(parentFolder).ToHashSet();
            score += matches
                .Where(match => folderTokens.Contains(match.Key))
                .Sum(match => match.Value * 60);
        }

        if (query.FolderHints.Any()) {
            var pathLower = node.FullPath.ToLowerInvariant();
            foreach (var hint in query.FolderHints) {
                cancellationToken.ThrowIfCancellationRequested();
                if (pathLower.Contains(hint.Name.ToLowerInvariant())) {
                    score += 100 * Math.Clamp(hint.Weight, 0, 1);
                }
            }
        }

        if (query.TargetType != null) {
            if (node.IsDirectory && query.TargetType.PrefersFolder) {
                score += 150 * query.TargetType.Folder;
            }
            else if (!node.IsDirectory && query.TargetType.PrefersFile) {
                score += 100 * query.TargetType.File;
            }
        }

        if (!node.IsDirectory && query.SoftExtensions.Count > 0) {
            var extension = Path.GetExtension(node.Name).TrimStart('.');
            var softIndex = query.SoftExtensions.FindIndex(
                value => string.Equals(
                    value.TrimStart('.'),
                    extension,
                    StringComparison.OrdinalIgnoreCase));
            if (softIndex >= 0) {
                score += Math.Max(10, 35 - softIndex * 5);
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(node.Name);
        var normalizedName = NormalizeForComparison(fileName);
        foreach (var term in searchTerms.Where(term =>
                     term.Role == SearchTermRole.Phrase ||
                     (term.Role == SearchTermRole.Anchor && term.Category == SearchTermCategory.Exact))) {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTerm = NormalizeForComparison(term.Text);
            if (normalizedName == normalizedTerm) {
                score += 150 * term.Weight;
            }
            else if (normalizedTerm.Length > 1 && normalizedName.Contains(normalizedTerm)) {
                score += 75 * term.Weight;
            }
        }

        var nameTokens = _tokenizer.Tokenize(fileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextTokenWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in searchTerms.Where(term => term.Role == SearchTermRole.Context)) {
            foreach (var token in _tokenizer.Tokenize(term.Text)) {
                if (!contextTokenWeights.TryGetValue(token, out var current) || term.Weight > current) {
                    contextTokenWeights[token] = term.Weight;
                }
            }
        }
        score += contextTokenWeights
            .Where(item => nameTokens.Contains(item.Key))
            .Sum(item => item.Value * 30);

        var freq = node.Metadata?.OpenCount ?? 0;
        score += freq * 2;

        return score;
    }

    private string NormalizeForComparison(string value) =>
        string.Join(' ', _tokenizer.Tokenize(value));
}
