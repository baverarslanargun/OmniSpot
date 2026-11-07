using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

internal sealed record UsnProjectionContext(
    UsnProjectionScope Scope,
    IUsnSubtreeReader SubtreeReader,
    ulong VolumeSerialNumber,
    CancellationToken CancellationToken,
    Func<string, bool>? IsExcluded = null);

internal sealed record UsnProjectionResult(
    IReadOnlyList<ChangeFeedEvent> Events,
    ChangeFeedGapReason GapReason,
    int SkippedSubtreeDirectoryCount = 0,
    bool SecurityChanged = false);

internal static class UsnEventProjector
{
    private const UsnReason ModifyReasons =
        UsnReason.DataOverwrite |
        UsnReason.DataExtend |
        UsnReason.DataTruncation |
        UsnReason.NamedDataOverwrite |
        UsnReason.NamedDataExtend |
        UsnReason.NamedDataTruncation |
        UsnReason.BasicInfoChange | UsnReason.SecurityChange | UsnReason.ReparsePointChange;

    private const UsnReason SecurityReasons =
        UsnReason.SecurityChange | UsnReason.ReparsePointChange;

    private const UsnReason RootBreakingReasons =
        UsnReason.FileDelete | UsnReason.RenameOldName | UsnReason.RenameNewName;

    public static UsnProjectionResult Project(
        UsnProjectionContext context,
        IReadOnlyList<UsnRecord> records)
    {
        var aggregates = new Dictionary<UsnFileReference, Aggregate>();
        var completed = new List<Aggregate>();
        var lastRenames = new Dictionary<UsnFileReference, (UsnFileReference Parent, string Name, bool Directory)>();
        var ordinal = 0;
        void Complete(UsnFileReference reference)
        {
            if (aggregates.Remove(reference, out var aggregate)) completed.Add(aggregate);
        }
        var scope = context.Scope;
        var skippedSubtreeDirectories = 0;
        var securityChanged = records.Any(record => (record.Reason & SecurityReasons) != 0 &&
            (context.IsExcluded is null ||
             !scope.TryResolve(record.ParentFileReference, out var securityParent) ||
             !UsnDirectoryNames.IsSingleSegment(record.Name) ||
             !context.IsExcluded(Path.Combine(securityParent, record.Name))));

        foreach (var record in records)
        {
            ordinal++;
            if (record.FileReference == scope.RootReference)
            {
                securityChanged |= (record.Reason & SecurityReasons) != 0 && context.IsExcluded?.Invoke(scope.RootPath) != true;
                if ((record.Reason & RootBreakingReasons) != 0)
                {
                    return new UsnProjectionResult(
                        Array.Empty<ChangeFeedEvent>(),
                        ChangeFeedGapReason.RootIdentityChanged,
                        SecurityChanged: securityChanged);
                }

                if ((record.Reason & SecurityReasons) != 0 && context.IsExcluded?.Invoke(scope.RootPath) != true)
                {
                    Accumulate(aggregates, record, scope.RootPath, record.Reason, ordinal);
                    if ((record.Reason & UsnReason.ReparsePointChange) != 0)
                    {
                        var subtree = context.SubtreeReader.ReadSubtree(scope.RootPath, scope.RootReference, context.VolumeSerialNumber, context.CancellationToken);
                        foreach (var entry in subtree.Directories) scope.Set(entry.Reference, entry.Name, entry.ParentReference);
                        skippedSubtreeDirectories += subtree.SkippedDirectoryCount;
                    }
                }
                continue;
            }

            if (!UsnDirectoryNames.IsSingleSegment(record.Name))
            {
                return Invalid(securityChanged);
            }

            string? recordPath = null;
            if (scope.TryResolve(record.ParentFileReference, out var parentPath))
            {
                if (!UsnRootScope.TryCanonicalize(
                        scope.RootPath,
                        Path.Combine(parentPath, record.Name),
                        out recordPath))
                {
                    return Invalid(securityChanged);
                }

            }

            if ((record.Reason & SecurityReasons) != 0 &&
                (recordPath is null || context.IsExcluded?.Invoke(recordPath) != true)) securityChanged = true;

            var oldOnly = IsOldNameOnly(record.Reason);
            var hasNewName = (record.Reason & UsnReason.RenameNewName) != 0;
            var pendingRename = aggregates.TryGetValue(record.FileReference, out var pending) && pending.RenameStarted;
            if (oldOnly)
            {
                if (pendingRename) return Invalid(securityChanged);
                Complete(record.FileReference);
                lastRenames.Remove(record.FileReference);
                aggregates[record.FileReference] = new Aggregate
                {
                    RenameStarted = true, RenamedFrom = recordPath, IsDirectory = record.IsDirectory,
                    RefreshSubtree = (record.Reason & SecurityReasons) != 0,
                    LastUsn = record.Usn, LastOrdinal = ordinal
                };
            }
            else
            {
                var duplicateClose = hasNewName && !pendingRename && (record.Reason & UsnReason.Close) != 0 &&
                    lastRenames.TryGetValue(record.FileReference, out var lastRename) &&
                    lastRename == (record.ParentFileReference, record.Name, record.IsDirectory);
                var reason = duplicateClose ? record.Reason & ~(UsnReason.RenameOldName | UsnReason.RenameNewName | UsnReason.Close)
                    : record.Reason;
                if (recordPath is not null && reason != 0)
                    Accumulate(aggregates, record, recordPath, reason, ordinal);
                if (hasNewName && !duplicateClose)
                {
                    if (aggregates.TryGetValue(record.FileReference, out var aggregate))
                    {
                        aggregate.Path = recordPath;
                        aggregate.LastUsn = record.Usn;
                        aggregate.LastOrdinal = ordinal;
                    }
                    Complete(record.FileReference);
                    lastRenames[record.FileReference] = (record.ParentFileReference, record.Name, record.IsDirectory);
                }
            }

            if (record.IsDirectory)
            {
                var mapped = UpdateDirectoryMap(context, record, recordPath);
                if (mapped < 0)
                {
                    return Invalid(securityChanged);
                }

                skippedSubtreeDirectories += mapped;
            }
        }

        completed.AddRange(aggregates.Values);
        return new UsnProjectionResult(
            Emit(completed),
            ChangeFeedGapReason.None,
            skippedSubtreeDirectories,
            securityChanged);
    }

    private static UsnProjectionResult Invalid(bool securityChanged) =>
        new(
            Array.Empty<ChangeFeedEvent>(),
            ChangeFeedGapReason.FeedStateInvalid,
            SecurityChanged: securityChanged);

    private static void Accumulate(
        Dictionary<UsnFileReference, Aggregate> aggregates,
        UsnRecord record,
        string path,
        UsnReason reason,
        int ordinal)
    {
        if (!aggregates.TryGetValue(record.FileReference, out var aggregate))
        {
            aggregate = new Aggregate();
            aggregates[record.FileReference] = aggregate;
        }

        aggregate.IsDirectory = record.IsDirectory;
        aggregate.RefreshSubtree |= (reason & SecurityReasons) != 0;
        aggregate.LastUsn = record.Usn;
        aggregate.LastOrdinal = ordinal;

        aggregate.Path = path;

        if ((reason & UsnReason.RenameNewName) != 0)
        {
            aggregate.HasNewName = true;
        }

        if ((reason & UsnReason.FileCreate) != 0)
        {
            aggregate.Created = true;
        }

        if ((reason & UsnReason.FileDelete) != 0)
        {
            aggregate.Deleted = true;
        }

        if ((reason & ModifyReasons) != 0)
        {
            aggregate.Modified = true;
        }
    }

    private static int UpdateDirectoryMap(
        UsnProjectionContext context,
        UsnRecord record,
        string? recordPath)
    {
        var scope = context.Scope;

        if (IsOldNameOnly(record.Reason))
        {
            return 0;
        }

        if ((record.Reason & UsnReason.FileDelete) != 0)
        {
            scope.Remove(record.FileReference);
            return 0;
        }

        if (recordPath is null)
        {
            if ((record.Reason & UsnReason.RenameNewName) != 0)
            {
                scope.Remove(record.FileReference);
            }

            return 0;
        }

        var entersRoot =
            (record.Reason & UsnReason.RenameNewName) != 0 &&
            !scope.Contains(record.FileReference);

        if ((record.Reason & UsnReason.ReparsePointChange) != 0 && (record.Attributes & FileAttributes.ReparsePoint) != 0 &&
            UsnDirectoryWalk.IsNameSurrogate(recordPath))
        {
            scope.Remove(record.FileReference);
            return 0;
        }

        scope.Set(record.FileReference, record.Name, record.ParentFileReference);

        if (!entersRoot && ((record.Reason & UsnReason.ReparsePointChange) == 0 || context.IsExcluded?.Invoke(recordPath) == true))
        {
            return 0;
        }

        var subtree = context.SubtreeReader.ReadSubtree(
            recordPath,
            record.FileReference,
            context.VolumeSerialNumber,
            context.CancellationToken);

        foreach (var entry in subtree.Directories)
        {
            if (!UsnDirectoryNames.IsSingleSegment(entry.Name))
            {
                return -1;
            }

            scope.Set(entry.Reference, entry.Name, entry.ParentReference);
        }

        return subtree.SkippedDirectoryCount;
    }

    private static bool IsOldNameOnly(UsnReason reason) =>
        (reason & UsnReason.RenameOldName) != 0 &&
        (reason & UsnReason.RenameNewName) == 0;

    private static List<ChangeFeedEvent> Emit(IEnumerable<Aggregate> aggregates)
    {
        var events = new List<ChangeFeedEvent>();

        foreach (var aggregate in aggregates.OrderBy(item => item.LastUsn).ThenBy(item => item.LastOrdinal))
        {
            var renamed =
                aggregate.RenamedFrom is not null &&
                aggregate.Path is not null &&
                !PathsMatch(aggregate.RenamedFrom, aggregate.Path);

            if (renamed)
            {
                events.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Renamed,
                    aggregate.Path!,
                    aggregate.IsDirectory,
                    aggregate.RenamedFrom, aggregate.RefreshSubtree));
            }

            if (aggregate.Deleted)
            {
                var deletedPath = aggregate.Path ?? aggregate.RenamedFrom;
                if (deletedPath is not null)
                {
                    events.Add(new ChangeFeedEvent(
                        ChangeFeedEventKind.Deleted,
                        deletedPath,
                        aggregate.IsDirectory));
                }

                continue;
            }

            if (renamed)
            {
                continue;
            }

            if (aggregate.Path is null)
            {
                if (aggregate.RenamedFrom is not null)
                {
                    events.Add(new ChangeFeedEvent(
                        ChangeFeedEventKind.Deleted,
                        aggregate.RenamedFrom,
                        aggregate.IsDirectory));
                }

                continue;
            }

            if (aggregate.Created || aggregate.HasNewName)
            {
                events.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Created,
                    aggregate.Path,
                    aggregate.IsDirectory, RefreshSubtree: aggregate.RefreshSubtree));
            }
            else if (aggregate.Modified)
            {
                events.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Modified,
                    aggregate.Path,
                    aggregate.IsDirectory, RefreshSubtree: aggregate.RefreshSubtree));
            }
        }

        return events;
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class Aggregate
    {
        public long LastUsn { get; set; }

        public int LastOrdinal { get; set; }

        public bool RenameStarted { get; init; }

        public string? Path { get; set; }

        public string? RenamedFrom { get; set; }

        public bool IsDirectory { get; set; }

        public bool Created { get; set; }

        public bool Deleted { get; set; }

        public bool Modified { get; set; }
        public bool RefreshSubtree { get; set; }

        public bool HasNewName { get; set; }
    }
}
