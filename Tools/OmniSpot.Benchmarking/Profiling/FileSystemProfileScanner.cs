using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Security;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Profiling;

internal sealed class FileSystemProfileScanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly CultureInfo TurkishCulture = new("tr-TR");
    private static readonly char[] TurkishSpecificCharacters = "çÇğĞıİöÖşŞüÜ".ToCharArray();
    private static readonly char[] DottedOrDotlessICharacters = "Iİıi".ToCharArray();

    private readonly ITokenizer _tokenizer;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IReadOnlyList<ProfileRootRequest>, ProfileEnvironment> _captureEnvironment;

    internal FileSystemProfileScanner(
        ITokenizer tokenizer,
        Func<DateTimeOffset>? utcNow = null,
        Func<IReadOnlyList<ProfileRootRequest>, ProfileEnvironment>? captureEnvironment = null)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _captureEnvironment = captureEnvironment ?? ProfileEnvironmentProbe.Capture;
    }

    internal ProfileDocument Scan(
        IReadOnlyList<ProfileRootRequest> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one profile root is required.", nameof(roots));
        }

        var started = _utcNow();
        var stopwatch = Stopwatch.StartNew();
        var accumulator = new ProfileAccumulator(_tokenizer);
        var visitedPaths = new HashSet<string>(PathComparer);
        var rootMetrics = new List<MutableRootMetric>(roots.Count);

        foreach (var request in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootMetric = new MutableRootMetric(request.Kind, request.Ordinal);
            rootMetrics.Add(rootMetric);
            ScanRoot(request, rootMetric, visitedPaths, accumulator, cancellationToken);
        }

        stopwatch.Stop();
        var completed = _utcNow();
        var metrics = accumulator.CreateMetrics();
        return new ProfileDocument(
            SchemaMajor: 2,
            SchemaMinor: 0,
            ProfilerVersion: "0.3.1",
            MetricsFingerprint: ProfileJson.ComputeMetricsFingerprint(metrics),
            Manifest: new ProfileManifest(
                started.ToUnixTimeSeconds(),
                completed.ToUnixTimeSeconds(),
                stopwatch.ElapsedMilliseconds,
                rootMetrics.Select(metric => metric.ToImmutable()).ToArray(),
                _captureEnvironment(roots)),
            Metrics: metrics);
    }

    private static void ScanRoot(
        ProfileRootRequest request,
        MutableRootMetric rootMetric,
        ISet<string> visitedPaths,
        ProfileAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(request.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            accumulator.AddError(ProfileErrorKind.RootUnavailable);
            return;
        }

        if (!Directory.Exists(rootPath))
        {
            accumulator.AddError(ProfileErrorKind.RootUnavailable);
            return;
        }

        if (!visitedPaths.Add(rootPath))
        {
            rootMetric.OverlapSkippedCount++;
            return;
        }

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(rootPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            accumulator.AddError(ProfileErrorKind.RootMetadataUnavailable);
            return;
        }

        var rootName = new DirectoryInfo(rootPath).Name;
        accumulator.AddItem(
            rootName,
            rootPath,
            isDirectory: true,
            sizeBytes: 0,
            rootAttributes,
            depth: 0,
            rootMetric);

        var pending = new Queue<PendingDirectory>();
        pending.Enqueue(new PendingDirectory(rootPath, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            var fileChildren = 0L;
            var directoryChildren = 0L;
            var namesByCase = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var entry in EnumerateImmediateChildren(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        directoryChildren++;
                    }
                    else
                    {
                        fileChildren++;
                    }

                    if (namesByCase.TryGetValue(entry.Name, out var existingNames))
                    {
                        var existingVariantCount = existingNames.Count;
                        if (existingNames.Add(entry.Name))
                        {
                            accumulator.CaseOnlyPairCount += existingVariantCount;
                        }
                    }
                    else
                    {
                        namesByCase[entry.Name] = new HashSet<string>(StringComparer.Ordinal)
                        {
                            entry.Name
                        };
                    }

                    if (!visitedPaths.Add(entry.FullPath))
                    {
                        rootMetric.OverlapSkippedCount++;
                        continue;
                    }

                    accumulator.AddItem(
                        entry.Name,
                        entry.FullPath,
                        entry.IsDirectory,
                        entry.SizeBytes,
                        entry.Attributes,
                        directory.Depth + 1,
                        rootMetric);

                    if (entry.IsDirectory &&
                        !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Enqueue(new PendingDirectory(entry.FullPath, directory.Depth + 1));
                    }
                }

                accumulator.AddDirectoryChildren(fileChildren, directoryChildren);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                rootMetric.InaccessibleDirectoryCount++;
                accumulator.InaccessibleDirectoryCount++;
                accumulator.AddError(ProfileErrorKind.DirectoryInaccessible);
            }
            catch (IOException)
            {
                accumulator.AddError(ProfileErrorKind.DirectoryEnumerationFailed);
            }
        }
    }

    private static FileSystemEnumerable<EntrySnapshot> EnumerateImmediateChildren(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        return new FileSystemEnumerable<EntrySnapshot>(
            directoryPath,
            static (ref FileSystemEntry entry) => new EntrySnapshot(
                entry.FileName.ToString(),
                entry.ToFullPath(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.Attributes),
            options);
    }

    private sealed record EntrySnapshot(
        string Name,
        string FullPath,
        bool IsDirectory,
        long SizeBytes,
        FileAttributes Attributes);

    private readonly record struct PendingDirectory(string Path, int Depth);

    private sealed class MutableRootMetric
    {
        internal MutableRootMetric(ProfileRootKind kind, int ordinal)
        {
            Kind = kind;
            Ordinal = ordinal;
        }

        internal ProfileRootKind Kind { get; }
        internal int Ordinal { get; }
        internal long ItemCount { get; set; }
        internal long OverlapSkippedCount { get; set; }
        internal long InaccessibleDirectoryCount { get; set; }

        internal ProfileRootMetric ToImmutable() =>
            new(Kind, Ordinal, ItemCount, OverlapSkippedCount, InaccessibleDirectoryCount);
    }

    private sealed class ProfileAccumulator
    {
        private readonly ITokenizer _tokenizer;
        private readonly FrequencyDistribution _depth = new();
        private readonly FrequencyDistribution _fileNameLength = new();
        private readonly FrequencyDistribution _directoryNameLength = new();
        private readonly FrequencyDistribution _filesPerDirectory = new();
        private readonly FrequencyDistribution _directoriesPerDirectory = new();
        private readonly FrequencyDistribution _childrenPerDirectory = new();
        private readonly FrequencyDistribution _fileSize = new();
        private readonly FrequencyDistribution _tokensPerItem = new();
        private readonly Dictionary<string, long> _extensionCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _tokenDocumentFrequencies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ProfileErrorKind, long> _errorCounts = new();

        private long _totalItemCount;
        private long _fileCount;
        private long _directoryCount;
        private long _noExtensionFileCount;
        private long _itemsWithTokens;
        private long _letteredNameCount;
        private long _turkishSpecificNameCount;
        private long _dottedOrDotlessINameCount;
        private long _allUppercaseNameCount;
        private long _cultureFoldDifferenceCount;
        private long _asciiOnlyNameCount;
        private long _nonAsciiNameCount;
        private long _reparsePointCount;
        private long _longPathCount;
        private long _whitespaceNameCount;
        private long _percentNameCount;
        private long _hiddenItemCount;
        private long _systemItemCount;

        internal ProfileAccumulator(ITokenizer tokenizer)
        {
            _tokenizer = tokenizer;
        }

        internal long CaseOnlyPairCount { get; set; }
        internal long InaccessibleDirectoryCount { get; set; }

        internal void AddItem(
            string name,
            string fullPath,
            bool isDirectory,
            long sizeBytes,
            FileAttributes attributes,
            int depth,
            MutableRootMetric rootMetric)
        {
            _totalItemCount++;
            rootMetric.ItemCount++;
            _depth.Add(depth);

            if (isDirectory)
            {
                _directoryCount++;
                _directoryNameLength.Add(name.Length);
            }
            else
            {
                _fileCount++;
                _fileNameLength.Add(name.Length);
                _fileSize.Add(Math.Max(0, sizeBytes));
                AddExtension(name);
            }

            AddTokens(name);
            AddNameIndicators(name, isDirectory);

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _reparsePointCount++;
            }

            if (fullPath.Length > 260)
            {
                _longPathCount++;
            }

            if (name.Any(char.IsWhiteSpace))
            {
                _whitespaceNameCount++;
            }

            if (name.Contains('%'))
            {
                _percentNameCount++;
            }

            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                _hiddenItemCount++;
            }

            if (attributes.HasFlag(FileAttributes.System))
            {
                _systemItemCount++;
            }
        }

        internal void AddDirectoryChildren(long fileCount, long directoryCount)
        {
            _filesPerDirectory.Add(fileCount);
            _directoriesPerDirectory.Add(directoryCount);
            _childrenPerDirectory.Add(fileCount + directoryCount);
        }

        internal void AddError(ProfileErrorKind kind)
        {
            _errorCounts.TryGetValue(kind, out var count);
            _errorCounts[kind] = count + 1;
        }

        internal ProfileMetrics CreateMetrics()
        {
            var documentFrequency = new FrequencyDistribution();
            var fanOut = HistogramDefinitions.TokenFanOut
                .Select(bucket => new MutableTokenBucket(bucket))
                .ToArray();
            long tokenItemEdgeCount = 0;
            long singletonTokenCount = 0;
            long sharedTokenEdgeCount = 0;

            foreach (var frequency in _tokenDocumentFrequencies.Values)
            {
                documentFrequency.Add(frequency);
                tokenItemEdgeCount += frequency;
                if (frequency == 1)
                {
                    singletonTokenCount++;
                }

                if (frequency >= 2)
                {
                    sharedTokenEdgeCount += frequency;
                }

                var bucket = fanOut.First(candidate => candidate.Range.Contains(frequency));
                bucket.TokenCount++;
                bucket.TokenItemEdgeCount += frequency;
            }

            var uniqueTokenCount = _tokenDocumentFrequencies.Count;
            return new ProfileMetrics(
                _totalItemCount,
                _fileCount,
                _directoryCount,
                _depth.Summarize(HistogramDefinitions.Depth),
                _fileNameLength.Summarize(HistogramDefinitions.NameLength),
                _directoryNameLength.Summarize(HistogramDefinitions.NameLength),
                _filesPerDirectory.Summarize(HistogramDefinitions.Count),
                _directoriesPerDirectory.Summarize(HistogramDefinitions.Count),
                _childrenPerDirectory.Summarize(HistogramDefinitions.Count),
                _fileSize.Summarize(HistogramDefinitions.FileSize),
                CreateExtensionProfile(),
                new TokenProfile(
                    _itemsWithTokens,
                    Ratio(_itemsWithTokens, _totalItemCount),
                    uniqueTokenCount,
                    tokenItemEdgeCount,
                    _tokensPerItem.Summarize(HistogramDefinitions.Count),
                    documentFrequency.Summarize(HistogramDefinitions.TokenFanOut),
                    fanOut.Select(bucket => bucket.ToImmutable()).ToArray(),
                    Ratio(singletonTokenCount, uniqueTokenCount),
                    Ratio(tokenItemEdgeCount - uniqueTokenCount, tokenItemEdgeCount),
                    Ratio(sharedTokenEdgeCount, tokenItemEdgeCount)),
                new NameCultureProfile(
                    _totalItemCount,
                    _letteredNameCount,
                    _turkishSpecificNameCount,
                    Ratio(_turkishSpecificNameCount, _letteredNameCount),
                    _dottedOrDotlessINameCount,
                    Ratio(_dottedOrDotlessINameCount, _letteredNameCount),
                    _allUppercaseNameCount,
                    Ratio(_allUppercaseNameCount, _letteredNameCount),
                    _cultureFoldDifferenceCount,
                    Ratio(_cultureFoldDifferenceCount, _letteredNameCount),
                    _asciiOnlyNameCount,
                    Ratio(_asciiOnlyNameCount, _letteredNameCount),
                    _nonAsciiNameCount,
                    Ratio(_nonAsciiNameCount, _letteredNameCount)),
                new SpecialProfile(
                    _reparsePointCount,
                    JunctionCount: _reparsePointCount == 0 ? 0 : null,
                    SymbolicLinkCount: _reparsePointCount == 0 ? 0 : null,
                    OtherReparsePointCount: _reparsePointCount == 0 ? 0 : null,
                    CaseOnlyPairCount,
                    _longPathCount,
                    _whitespaceNameCount,
                    _percentNameCount,
                    _hiddenItemCount,
                    Ratio(_hiddenItemCount, _totalItemCount),
                    _systemItemCount,
                    Ratio(_systemItemCount, _totalItemCount),
                    InaccessibleDirectoryCount),
                Enum.GetValues<ProfileErrorKind>()
                    .Select(kind => new ProfileErrorMetric(
                        kind,
                        _errorCounts.GetValueOrDefault(kind)))
                    .ToArray());
        }

        private void AddExtension(string name)
        {
            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension))
            {
                _noExtensionFileCount++;
                return;
            }

            _extensionCounts.TryGetValue(extension, out var count);
            _extensionCounts[extension] = count + 1;
        }

        private void AddTokens(string name)
        {
            var tokens = _tokenizer.Tokenize(name)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _tokensPerItem.Add(tokens.Count);
            if (tokens.Count > 0)
            {
                _itemsWithTokens++;
            }

            foreach (var token in tokens)
            {
                _tokenDocumentFrequencies.TryGetValue(token, out var frequency);
                _tokenDocumentFrequencies[token] = frequency + 1;
            }
        }

        private void AddNameIndicators(string name, bool isDirectory)
        {
            var core = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            var letters = core.Where(char.IsLetter).ToArray();
            if (letters.Length == 0)
            {
                return;
            }

            _letteredNameCount++;
            if (core.IndexOfAny(TurkishSpecificCharacters) >= 0)
            {
                _turkishSpecificNameCount++;
            }

            if (core.IndexOfAny(DottedOrDotlessICharacters) >= 0)
            {
                _dottedOrDotlessINameCount++;
            }

            if (letters.All(char.IsUpper))
            {
                _allUppercaseNameCount++;
            }

            if (!StringComparer.Ordinal.Equals(
                    core.ToLower(TurkishCulture),
                    core.ToLowerInvariant()))
            {
                _cultureFoldDifferenceCount++;
            }

            if (core.All(character => character <= 0x7f))
            {
                _asciiOnlyNameCount++;
            }
            else
            {
                _nonAsciiNameCount++;
            }
        }

        private ExtensionProfile CreateExtensionProfile()
        {
            var published = _extensionCounts
                .Where(pair => pair.Value >= 50 && pair.Value * 1000 >= _fileCount)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ExtensionMetric(
                    pair.Key.ToLowerInvariant(),
                    pair.Value,
                    Ratio(pair.Value, _fileCount)))
                .ToArray();
            var publishedCount = published.Sum(metric => metric.Count);
            var extensionFileCount = _extensionCounts.Values.Sum();
            return new ExtensionProfile(
                published,
                OtherFileCount: extensionFileCount - publishedCount,
                _noExtensionFileCount);
        }

        private static double Ratio(long numerator, long denominator) =>
            denominator == 0 ? 0 : (double)numerator / denominator;

        private sealed class MutableTokenBucket
        {
            internal MutableTokenBucket(BucketRange range)
            {
                Range = range;
            }

            internal BucketRange Range { get; }
            internal long TokenCount { get; set; }
            internal long TokenItemEdgeCount { get; set; }

            internal TokenFrequencyBucket ToImmutable() =>
                new(Range.Label, TokenCount, TokenItemEdgeCount);
        }
    }
}
