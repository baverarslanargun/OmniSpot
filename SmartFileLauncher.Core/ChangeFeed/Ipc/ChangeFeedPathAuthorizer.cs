using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedRootProjection
{
    public ChangeFeedRootProjection(
        string rootPath,
        IReadOnlyList<ChangeFeedEvent> events,
        bool withheld)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = rootPath;
        Events = events ?? throw new ArgumentNullException(nameof(events));
        Withheld = withheld;
    }

    public string RootPath { get; }

    public IReadOnlyList<ChangeFeedEvent> Events { get; }

    public bool Withheld { get; }
}

public readonly record struct ChangeFeedEventProjection(
    ChangeFeedEvent? Published,
    bool Withheld);

public sealed class ChangeFeedPathAuthorizer
{
    private readonly string _rootPath;
    private readonly Func<string, bool> _canList;
    private readonly Dictionary<string, bool> _decided =
        new(StringComparer.Ordinal);

    public ChangeFeedPathAuthorizer(string rootPath, Func<string, bool> canList)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(canList);

        _rootPath = Path.TrimEndingDirectorySeparator(rootPath);
        _canList = canList;
    }

    public static ChangeFeedPathAuthorizer ForCurrentCaller(string rootPath) =>
        new(rootPath, CanListAsCaller);

    public ChangeFeedRootProjection Project(IReadOnlyList<ChangeFeedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var published = new List<ChangeFeedEvent>(events.Count);
        var withheld = false;

        foreach (var change in events)
        {
            var projection = Project(change);

            if (projection.Published is { } visible)
            {
                published.Add(visible);
            }

            withheld |= projection.Withheld;
        }

        return new ChangeFeedRootProjection(_rootPath, published, withheld);
    }

    public ChangeFeedEventProjection Project(ChangeFeedEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var next = Publishable(change.FullPath);

        if (change.Kind != ChangeFeedEventKind.Renamed)
        {
            return next && change.OldPath is null
                ? new ChangeFeedEventProjection(
                    new ChangeFeedEvent(change.Kind, change.FullPath, change.IsDirectory),
                    false)
                : new ChangeFeedEventProjection(null, true);
        }

        var oldVisible = change.OldPath is not null && Publishable(change.OldPath);

        if (next && oldVisible)
        {
            return new ChangeFeedEventProjection(
                new ChangeFeedEvent(
                    ChangeFeedEventKind.Renamed,
                    change.FullPath,
                    change.IsDirectory,
                    change.OldPath),
                false);
        }

        if (oldVisible)
        {
            return new ChangeFeedEventProjection(
                new ChangeFeedEvent(
                    ChangeFeedEventKind.Deleted,
                    change.OldPath!,
                    change.IsDirectory),
                true);
        }

        if (next)
        {
            return new ChangeFeedEventProjection(
                new ChangeFeedEvent(
                    ChangeFeedEventKind.Created,
                    change.FullPath,
                    change.IsDirectory),
                true);
        }

        return new ChangeFeedEventProjection(null, true);
    }

    private bool Publishable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception failure)
            when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(candidate, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsUnderRoot(candidate))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(candidate);
        return parent is not null && ListableChain(parent);
    }

    private bool IsUnderRoot(string candidate)
    {
        if (string.Equals(candidate, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool ListableChain(string directory)
    {
        if (_decided.TryGetValue(directory, out var known))
        {
            return known;
        }

        bool decision;
        if (string.Equals(directory, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            decision = Listable(directory);
        }
        else if (!IsUnderRoot(directory))
        {
            decision = false;
        }
        else
        {
            var parent = Path.GetDirectoryName(directory);
            decision = parent is not null && ListableChain(parent) && Listable(directory);
        }

        _decided[directory] = decision;
        return decision;
    }

    private bool Listable(string directory)
    {
        try
        {
            return _canList(directory);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanListAsCaller(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
