namespace SmartFileLauncher.Core.Search;

internal readonly record struct QueryCatalogFilter(
    bool IncludeFiles,
    bool IncludeDirectories,
    bool FilterDirectoryDates,
    bool FilterFileSize,
    DateTime? CreatedAfter,
    DateTime? CreatedBeforeExclusive,
    DateTime? ModifiedAfter,
    DateTime? ModifiedBeforeExclusive,
    double? MinSizeMb,
    double? MaxSizeMb)
{
    internal bool Matches(SearchItem item) => Matches(
        item.IsDirectory,
        item.SizeBytes,
        item.CreatedTime,
        item.LastWriteTime);

    internal bool Matches(
        bool isDirectory,
        long? sizeBytes,
        DateTime? createdTime,
        DateTime? lastWriteTime)
    {
        if ((isDirectory && !IncludeDirectories) || (!isDirectory && !IncludeFiles))
        {
            return false;
        }

        if (!isDirectory || FilterDirectoryDates)
        {
            if (CreatedAfter is { } createdAfter &&
                (createdTime is null || createdTime.Value < createdAfter))
            {
                return false;
            }

            if (CreatedBeforeExclusive is { } createdBeforeExclusive &&
                (createdTime is null || createdTime.Value >= createdBeforeExclusive))
            {
                return false;
            }

            if (ModifiedAfter is { } modifiedAfter &&
                (lastWriteTime is null || lastWriteTime.Value < modifiedAfter))
            {
                return false;
            }

            if (ModifiedBeforeExclusive is { } modifiedBeforeExclusive &&
                (lastWriteTime is null || lastWriteTime.Value >= modifiedBeforeExclusive))
            {
                return false;
            }
        }

        if (isDirectory || !FilterFileSize)
        {
            return true;
        }

        if (sizeBytes is null)
        {
            return false;
        }

        var sizeMb = sizeBytes.Value / (1024.0 * 1024.0);
        return (!MinSizeMb.HasValue || sizeMb >= MinSizeMb.Value) &&
               (!MaxSizeMb.HasValue || sizeMb <= MaxSizeMb.Value);
    }
}
