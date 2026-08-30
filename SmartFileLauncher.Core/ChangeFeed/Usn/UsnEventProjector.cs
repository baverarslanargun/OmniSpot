using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

internal sealed record UsnProjectionContext(
    UsnProjectionScope Scope,
    IUsnSubtreeReader SubtreeReader,
    ulong VolumeSerialNumber,
    CancellationToken CancellationToken);

internal sealed record UsnProjectionResult(
    IReadOnlyList<ChangeFeedEvent> Events,
    ChangeFeedGapReason GapReason,
    int SkippedSubtreeDirectoryCount = 0);

/// <summary>
/// Turns raw journal records into root-scoped change events and buffers the
/// directory map updates the same batch implies.
/// </summary>
internal static class UsnEventProjector
{
    private const UsnReason ModifyReasons =
        UsnReason.DataOverwrite |
        UsnReason.DataExtend |
        UsnReason.DataTruncation |
        UsnReason.NamedDataOverwrite |
        UsnReason.NamedDataExtend |
        UsnReason.NamedDataTruncation |
        UsnReason.BasicInfoChange;

    private const UsnReason RootBreakingReasons =
        UsnReason.FileDelete | UsnReason.RenameOldName | UsnReason.RenameNewName;

    public static UsnProjectionResult Project(
        UsnProjectionContext context,
        IReadOnlyList<UsnRecord> records)
    {
        var aggregates = new Dictionary<UsnFileReference, Aggregate>();
        var scope = context.Scope;
        var skippedSubtreeDirectories = 0;

        foreach (var record in records)
        {
            if (record.FileReference == scope.RootReference)
            {
                if ((record.Reason & RootBreakingReasons) != 0)
                {
                    return new UsnProjectionResult(
                        Array.Empty<ChangeFeedEvent>(),
                        ChangeFeedGapReason.RootIdentityChanged);
                }

                continue;
            }

            string? recordPath = null;
            if (scope.TryResolve(record.ParentFileReference, out var parentPath))
            {
                recordPath = Path.Combine(parentPath, record.Name);
                Accumulate(aggregates, record, recordPath);
            }

            if (record.IsDirectory)
            {
                skippedSubtreeDirectories += UpdateDirectoryMap(context, record, recordPath);
            }
        }

        return new UsnProjectionResult(
            Emit(aggregates),
            ChangeFeedGapReason.None,
            skippedSubtreeDirectories);
    }

    private static void Accumulate(
        Dictionary<UsnFileReference, Aggregate> aggregates,
        UsnRecord record,
        string path)
    {
        if (!aggregates.TryGetValue(record.FileReference, out var aggregate))
        {
            aggregate = new Aggregate { FirstUsn = record.Usn };
            aggregates[record.FileReference] = aggregate;
        }

        aggregate.IsDirectory = record.IsDirectory;

        if (IsOldNameOnly(record.Reason))
        {
            aggregate.RenamedFrom ??= path;
            return;
        }

        aggregate.Path = path;

        if ((record.Reason & UsnReason.RenameNewName) != 0)
        {
            aggregate.HasNewName = true;
        }

        if ((record.Reason & UsnReason.FileCreate) != 0)
        {
            aggregate.Created = true;
        }

        if ((record.Reason & UsnReason.FileDelete) != 0)
        {
            aggregate.Deleted = true;
        }

        if ((record.Reason & ModifyReasons) != 0)
        {
            aggregate.Modified = true;
        }
    }

    /// <summary>
    /// Keeps the directory map in step with the batch and returns how many
    /// directories a moved-in subtree could not contribute.
    /// </summary>
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

        // A directory that arrives under a new name without already being known
        // came from outside the root. Its descendants were not touched on disk,
        // so the journal will never name them and the map has to learn them now.
        var entersRoot =
            (record.Reason & UsnReason.RenameNewName) != 0 &&
            !scope.Contains(record.FileReference);

        scope.Set(record.FileReference, record.Name, record.ParentFileReference);

        if (!entersRoot)
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
            scope.Set(entry.Reference, entry.Name, entry.ParentReference);
        }

        return subtree.SkippedDirectoryCount;
    }

    private static bool IsOldNameOnly(UsnReason reason) =>
        (reason & UsnReason.RenameOldName) != 0 &&
        (reason & UsnReason.RenameNewName) == 0;

    private static List<ChangeFeedEvent> Emit(Dictionary<UsnFileReference, Aggregate> aggregates)
    {
        var events = new List<ChangeFeedEvent>();

        foreach (var aggregate in aggregates.Values.OrderBy(item => item.FirstUsn))
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
                    aggregate.RenamedFrom));
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
                    aggregate.IsDirectory));
            }
            else if (aggregate.Modified)
            {
                events.Add(new ChangeFeedEvent(
                    ChangeFeedEventKind.Modified,
                    aggregate.Path,
                    aggregate.IsDirectory));
            }
        }

        return events;
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class Aggregate
    {
        public long FirstUsn { get; init; }

        public string? Path { get; set; }

        public string? RenamedFrom { get; set; }

        public bool IsDirectory { get; set; }

        public bool Created { get; set; }

        public bool Deleted { get; set; }

        public bool Modified { get; set; }

        public bool HasNewName { get; set; }
    }
}
