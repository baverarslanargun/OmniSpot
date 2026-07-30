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
        List<(FileSystemNode node, HashSet<string> tokens)> candidates;
        
        if (query.FilterOnlyMode || !query.Keywords.Any()) {
            // Filter-only mode: get all files, apply filters (no keyword matching)
            candidates = GetAllFilesForFiltering(query, rootNode, cancellationToken);
        } else {
            // Keyword search mode: find files matching keywords
            candidates = GetCandidateNodes(
                query.Keywords,
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
        if (query.PredictedExtensions.Any()) {
            foreach (var ext in query.PredictedExtensions) {
                cancellationToken.ThrowIfCancellationRequested();
                allowedExtensions.Add(ext.StartsWith(".") ? ext : $".{ext}");
            }
        }
        // Priority 2: Fallback to general file types
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
        
        // 4. Apply date filter
        if (query.DateFilter != null) {
            candidates = ApplyDateFilter(candidates, query.DateFilter, cancellationToken);
        }

        // 5. Apply size filter
        if (query.SizeFilter != null) {
            candidates = ApplySizeFilter(candidates, query.SizeFilter, cancellationToken);
        }
        
        // 6. Expand folder contents if needed (only if user doesn't prefer folders)
        var finalResults = new List<(FileSystemNode node, HashSet<string> tokens)>();
        foreach (var (node, tokens) in candidates) {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.IsDirectory) {
                // If user prefers folders, add the folder itself
                if (userWantsFolders) {
                    finalResults.Add((node, tokens));
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
                        finalResults.Add((child, tokens)); // Inherit parent folder tokens
                    }
                }
            } else {
                finalResults.Add((node, tokens));
            }
        }
        
        // 7. Score and rank
        var pq = new PriorityQueue<SearchResult, double>();
        foreach (var (node, matchedTokens) in finalResults) {
            cancellationToken.ThrowIfCancellationRequested();
            double score = CalculateScore(
                query,
                node,
                matchedTokens,
                cancellationToken);
            pq.Enqueue(new SearchResult { 
                Name = node.Name, 
                FullPath = node.FullPath, 
                Score = score 
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
    
    private List<(FileSystemNode node, HashSet<string> tokens)> GetCandidateNodes(
        List<string> keywords,
        InvertedIndexSnapshot invertedIndex,
        FileSystemNode rootNode,
        CancellationToken cancellationToken) {
        if (!keywords.Any()) {
            // No keywords = return all nodes
            var allNodes = GetAllChildren(rootNode, cancellationToken);
            return allNodes.Select(n => (n, new HashSet<string>())).ToList();
        }
        
        var nodeMatches = new Dictionary<string, (FileSystemNode node, HashSet<string> tokens)>();
        
        foreach (var keyword in keywords) {
            cancellationToken.ThrowIfCancellationRequested();
            var keywordTokens = _tokenizer.Tokenize(keyword).ToList();
            foreach (var token in keywordTokens) {
                cancellationToken.ThrowIfCancellationRequested();
                bool foundAny = false;

                // 1. Exact match
                var exactMatches = invertedIndex.Get(token);
                if (exactMatches.Count > 0) {
                    foreach (var node in exactMatches) {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddMatch(nodeMatches, node, token);
                    }
                    foundAny = true;
                } 
                
                // 2. Partial match (Substring) - Solves "612" -> "FR612"
                // Only if token is long enough to avoid noise (e.g. don't match "a" in everything)
                if (token.Length >= 2) {
                    var partialMatches = invertedIndex.GetPartial(token, cancellationToken);
                    foreach (var node in partialMatches) {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddMatch(nodeMatches, node, token);
                        foundAny = true;
                    }
                }

                // 3. Fuzzy match (Fallback)
                // If we haven't found anything yet, try fuzzy matching
                if (!foundAny) {
                    var fuzzyMatches = invertedIndex.GetFuzzy(
                        token,
                        maxDistance: 2,
                        cancellationToken: cancellationToken);
                    foreach (var node in fuzzyMatches) {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddMatch(nodeMatches, node, token);
                    }
                }
            }
        }
        
        return nodeMatches.Values.ToList();
    }

    /// <summary>
    /// Gets all files for filter-only mode, applying folder hints if present.
    /// In filter-only mode, we return all files (not searching by keywords),
    /// just applying type/date/size filters.
    /// </summary>
    private List<(FileSystemNode node, HashSet<string> tokens)> GetAllFilesForFiltering(
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
        
        return allNodes.Select(n => (n, new HashSet<string>())).ToList();
    }

    private void AddMatch(Dictionary<string, (FileSystemNode node, HashSet<string> tokens)> matches, FileSystemNode node, string token) {
        if (!matches.ContainsKey(node.FullPath)) {
            matches[node.FullPath] = (node, new HashSet<string>());
        }
        matches[node.FullPath].tokens.Add(token);
    }
    
    private List<(FileSystemNode node, HashSet<string> tokens)> ApplyDateFilter(
        List<(FileSystemNode node, HashSet<string> tokens)> candidates, 
        DateFilter filter,
        CancellationToken cancellationToken) {
        
        return candidates.Where(c => {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = c.node.Metadata;
            if (metadata == null) return true;
            
            if (filter.CreatedAfter != null && DateTime.TryParse(filter.CreatedAfter, out var createdAfter)) {
                if (metadata.CreatedTime == null || metadata.CreatedTime < createdAfter) {
                    return false;
                }
            }
            
            if (filter.CreatedBefore != null && DateTime.TryParse(filter.CreatedBefore, out var createdBefore)) {
                if (metadata.CreatedTime == null || metadata.CreatedTime > createdBefore) {
                    return false;
                }
            }
            
            if (filter.ModifiedAfter != null && DateTime.TryParse(filter.ModifiedAfter, out var modifiedAfter)) {
                if (metadata.LastWriteTime == null || metadata.LastWriteTime < modifiedAfter) {
                    return false;
                }
            }
            
            if (filter.ModifiedBefore != null && DateTime.TryParse(filter.ModifiedBefore, out var modifiedBefore)) {
                if (metadata.LastWriteTime == null || metadata.LastWriteTime > modifiedBefore) {
                    return false;
                }
            }
            
            return true;
        }).ToList();
    }

    private List<(FileSystemNode node, HashSet<string> tokens)> ApplySizeFilter(
        List<(FileSystemNode node, HashSet<string> tokens)> candidates,
        SizeFilter filter,
        CancellationToken cancellationToken)
    {
        return candidates.Where(c => {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.node.IsDirectory) return true; // Don't filter folders by size
            var sizeBytes = c.node.Metadata?.SizeBytes;
            if (sizeBytes == null) return true;

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
        HashSet<string> matchedTokens,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        double score = 0;
        
        // Base score from matched tokens
        score += matchedTokens.Count * 50;
        
        // Bonus if folder name matches
        if (node.FullPath.Contains(Path.DirectorySeparatorChar)) {
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(node.FullPath) ?? "");
            var folderTokens = _tokenizer.Tokenize(parentFolder).ToHashSet();
            var folderMatchCount = matchedTokens.Count(t => folderTokens.Contains(t));
            score += folderMatchCount * 75; // Higher weight for folder matches
        }

        // Bonus from Folder Hints (AI)
        if (query.FolderHints.Any()) {
            var pathLower = node.FullPath.ToLowerInvariant();
            foreach (var hint in query.FolderHints) {
                cancellationToken.ThrowIfCancellationRequested();
                if (pathLower.Contains(hint.Name.ToLowerInvariant())) {
                    score += 100 * hint.Weight;
                }
            }
        }
        
        // Target type bonus - boost score based on file/folder preference
        if (query.TargetType != null) {
            if (node.IsDirectory && query.TargetType.PrefersFolder) {
                // User wants folders and this is a folder - bonus!
                score += 150 * query.TargetType.Folder;
            }
            else if (!node.IsDirectory && query.TargetType.PrefersFile) {
                // User wants files and this is a file - bonus!
                score += 100 * query.TargetType.File;
            }
        }
        
        // Frequency bonus
        var freq = node.Metadata?.OpenCount ?? 0;
        score += freq * 2;
        
        return score;
    }
}
