namespace SmartFileLauncher.Core.Application.Settings;

public enum IndexScanWorkerMode { Serial, Adaptive, Manual }
public enum IndexSortMethod { Pivot, Merge, Ps5 }

public sealed record IndexBuildOptions(
    IndexScanWorkerMode ScanMode = IndexScanWorkerMode.Adaptive,
    int ScanWorkers = 1,
    IndexSortMethod SortMethod = IndexSortMethod.Ps5,
    bool AdaptiveSort = true,
    int SortWorkers = 1)
{
    public bool IsValid => Enum.IsDefined(ScanMode) && Enum.IsDefined(SortMethod) &&
        ScanWorkers is >= 1 and <= 16 && SortWorkers is >= 1 and <= 16;

    public static IndexBuildOptions Normalize(IndexBuildOptions? options) => options is { IsValid: true } ? options : new();

    internal IndexBuildOptions ForService() =>
        SortMethod == IndexSortMethod.Ps5 ? this with { SortMethod = IndexSortMethod.Pivot } : this;
}
