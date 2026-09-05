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
            var next = Publishable(change.FullPath);

            if (change.Kind != ChangeFeedEventKind.Renamed)
            {
                if (next && change.OldPath is null)
                {
                    published.Add(new ChangeFeedEvent(
                        change.Kind,
                        change.FullPath,
                        change.IsDirectory));
                }
                else
                {
                    withheld = true;
                }

                continue;
            }

            var oldVisible = change.OldPath is not null && Publishable(change.OldPath);

            if (next && oldVisible)
            {
                published.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Renamed,
                    change.FullPath,
                    change.IsDirectory,
                    change.OldPath));
                continue;
            }

            if (oldVisible)
            {
                published.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Deleted,
                    change.OldPath!,
                    change.IsDirectory));
                withheld = true;
                continue;
            }

            if (next)
            {
                published.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Created,
                    change.FullPath,
                    change.IsDirectory));
                withheld = true;
                continue;
            }

            withheld = true;
        }

        return new ChangeFeedRootProjection(_rootPath, published, withheld);
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
