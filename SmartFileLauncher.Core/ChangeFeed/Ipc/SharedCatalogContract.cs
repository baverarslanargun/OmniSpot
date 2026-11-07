using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record SharedCatalogOptions(IReadOnlyList<string> RootsUtf16, IndexScanMethod Method, bool Rebuild = false,
    string CacheKey = "default", IndexBuildOptions? BuildOptions = null);
public sealed record SharedCatalogPart(string RootUtf16, string Directory, string Frame, long Generation,
    int Items, bool ExcludeRoot = false);
public sealed record SharedCatalogBuildMetrics(double ReadAndIngestMilliseconds, double PrepareMilliseconds,
    double CommitMilliseconds, double LinkedFoldersMilliseconds);
public sealed record SharedCatalogStatus(string Session, string Phase, int Items, long ElapsedMilliseconds,
    bool Ready, long Revision, IReadOnlyList<SharedCatalogPart> Parts, string? Failure = null,
    int Files = 0, int Directories = 0, int Tokens = 0,
    long AcceptedOperation = 0, long CompletedOperation = 0, bool Closed = false, SharedCatalogBuildMetrics? Build = null,
    IndexBuildOptions? AppliedBuildOptions = null, bool TokenIndexReady = true);
