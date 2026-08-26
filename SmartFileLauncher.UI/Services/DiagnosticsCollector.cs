using System.Diagnostics;
using System.IO;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Diagnostics;

namespace SmartFileLauncher.UI.Services;

public sealed class DiagnosticsCollector
{
    public const string GroupProcess = "SÜREÇ";
    public const string GroupIndex = "İNDEKS";
    public const string GroupThumbnails = "KÜÇÜK RESİM";
    public const string GroupFolder = "SON KLASÖR";

    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly IThumbnailService _thumbnails;
    private readonly int _requestedThumbnailSize;
    private readonly int _folderItemLimit;
    private readonly Process _process = Process.GetCurrentProcess();

    public DiagnosticsCollector(
        IIndexLifecycleService indexLifecycle,
        IThumbnailService thumbnails,
        int requestedThumbnailSize,
        int folderItemLimit)
    {
        _indexLifecycle = indexLifecycle ?? throw new ArgumentNullException(nameof(indexLifecycle));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _requestedThumbnailSize = requestedThumbnailSize;
        _folderItemLimit = folderItemLimit;
    }

    public DiagnosticsMetrics Metrics { get; } = new();

    public void Refresh()
    {
        CollectProcess();
        CollectIndex();
        CollectThumbnails();
    }

    public void RecordFolder(string folderPath, int itemCount, bool truncated)
    {
        var name = Path.GetFileName(folderPath);
        Metrics.Set(GroupFolder, "yol", string.IsNullOrEmpty(name) ? folderPath : name);
        Metrics.Set(
            GroupFolder,
            "listelenen",
            itemCount.ToString("N0"),
            truncated ? DiagnosticsSeverity.Warning : DiagnosticsSeverity.Normal,
            itemCount);
        Metrics.Set(
            GroupFolder,
            "kesme sınırı",
            truncated ? $"{_folderItemLimit:N0} (kesildi)" : "yok");
        Metrics.Set(
            GroupFolder, "açılışta istenen", itemCount.ToString("N0"),
            DiagnosticsSeverity.Normal, itemCount);
    }

    private void CollectProcess()
    {
        try
        {
            _process.Refresh();
            var privateBytes = _process.PrivateMemorySize64;
            var workingSet = _process.WorkingSet64;
            Metrics.Set(
                GroupProcess, "private", FormatBytes(privateBytes),
                DiagnosticsSeverity.Normal, privateBytes);
            Metrics.Set(
                GroupProcess, "working set", FormatBytes(workingSet),
                DiagnosticsSeverity.Normal, workingSet);
            Metrics.Set(
                GroupProcess, "iş parçacığı", _process.Threads.Count.ToString("N0"),
                DiagnosticsSeverity.Normal, _process.Threads.Count);
            Metrics.Set(
                GroupProcess, "handle", _process.HandleCount.ToString("N0"),
                DiagnosticsSeverity.Normal, _process.HandleCount);
        }
        catch (Exception ex)
        {
            Metrics.Set(GroupProcess, "private", ex.GetType().Name, DiagnosticsSeverity.Warning);
        }

        var managed = GC.GetTotalMemory(false);
        Metrics.Set(
            GroupProcess, "yönetilen yığın", FormatBytes(managed),
            DiagnosticsSeverity.Normal, managed);
        Metrics.Set(
            GroupProcess,
            "GC 0/1/2",
            $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    }

    private void CollectIndex()
    {
        try
        {
            var stats = _indexLifecycle.GetStats();
            Metrics.Set(
                GroupIndex, "dosya", stats.FileCount.ToString("N0"),
                DiagnosticsSeverity.Normal, stats.FileCount);
            Metrics.Set(
                GroupIndex, "dizin", stats.DirectoryCount.ToString("N0"),
                DiagnosticsSeverity.Normal, stats.DirectoryCount);
            Metrics.Set(
                GroupIndex, "token", stats.TokenCount.ToString("N0"),
                DiagnosticsSeverity.Normal, stats.TokenCount);

            var status = _indexLifecycle.ReconciliationStatus;
            Metrics.Set(
                GroupIndex,
                "uzlaştırma",
                status.IsRunning ? $"çalışıyor %{status.Progress}" : "boşta",
                status.IsRunning ? DiagnosticsSeverity.Warning : DiagnosticsSeverity.Normal,
                status.IsRunning ? status.Progress : 0);
        }
        catch (Exception ex)
        {
            Metrics.Set(GroupIndex, "durum", ex.GetType().Name, DiagnosticsSeverity.Critical);
        }
    }

    private void CollectThumbnails()
    {
        var diagnostics = _thumbnails.GetDiagnostics();
        var fill = diagnostics.MemoryCacheLimit == 0
            ? 0d
            : (double)diagnostics.MemoryCacheCount / diagnostics.MemoryCacheLimit;

        Metrics.Set(
            GroupThumbnails,
            "önbellek",
            $"{diagnostics.MemoryCacheCount:N0}/{diagnostics.MemoryCacheLimit:N0}",
            fill >= 1d
                ? DiagnosticsSeverity.Critical
                : fill >= 0.8d
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
            diagnostics.MemoryCacheCount);
        Metrics.Set(
            GroupThumbnails, "önbellek boyutu", FormatBytes(diagnostics.DecodedBytes),
            DiagnosticsSeverity.Normal, diagnostics.DecodedBytes);
        Metrics.Set(
            GroupThumbnails, "istek", diagnostics.Requests.ToString("N0"),
            DiagnosticsSeverity.Normal, diagnostics.Requests);
        Metrics.Set(
            GroupThumbnails, "  bellekten", diagnostics.MemoryHits.ToString("N0"),
            DiagnosticsSeverity.Normal, diagnostics.MemoryHits);
        Metrics.Set(
            GroupThumbnails, "  diskten", diagnostics.DiskHits.ToString("N0"),
            DiagnosticsSeverity.Normal, diagnostics.DiskHits);
        Metrics.Set(
            GroupThumbnails, "  kabuktan", diagnostics.ShellGenerated.ToString("N0"),
            DiagnosticsSeverity.Normal, diagnostics.ShellGenerated);
        Metrics.Set(
            GroupThumbnails,
            "  başarısız",
            diagnostics.Failures.ToString("N0"),
            diagnostics.Failures > 0 ? DiagnosticsSeverity.Warning : DiagnosticsSeverity.Normal,
            diagnostics.Failures);
        Metrics.Set(
            GroupThumbnails,
            "istenen boyut",
            $"{_requestedThumbnailSize}×{_requestedThumbnailSize}");

        if (diagnostics.LastDecodedPixelWidth > 0)
        {
            Metrics.Set(
                GroupThumbnails,
                "çözülen boyut",
                $"{diagnostics.LastDecodedPixelWidth}×{diagnostics.LastDecodedPixelHeight}",
                diagnostics.LastDecodedPixelWidth > _requestedThumbnailSize
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Good,
                diagnostics.LastDecodedPixelWidth);
            var perImage = (long)diagnostics.LastDecodedPixelWidth
                * diagnostics.LastDecodedPixelHeight
                * 4;
            Metrics.Set(
                GroupThumbnails, "adet başına", FormatBytes(perImage),
                DiagnosticsSeverity.Normal, perImage);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N2} GB";
    }
}
