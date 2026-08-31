using System.Diagnostics;
using System.IO;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Diagnostics;

namespace SmartFileLauncher.UI.Services;

public sealed class DiagnosticsCollector
{
    public const string GroupProcess = "SÜREÇ";
    public const string GroupMemory = "BELLEK";
    public const string GroupIo = "G/Ç";
    public const string GroupIndex = "İNDEKS";
    public const string GroupThumbnails = "KÜÇÜK RESİM";
    public const string GroupSearch = "ARAMA";
    public const string GroupFolder = "SON KLASÖR";

    private const string RateKeyAllocated = "ayrılan";
    private const string RateKeyCpu = "cpu";
    private const string RateKeyReadOps = "okuma-islem";
    private const string RateKeyReadBytes = "okuma-bayt";

    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly IThumbnailService _thumbnails;
    private readonly int _requestedThumbnailSize;
    private readonly int _folderItemLimit;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DiagnosticsRateTracker _rates = new();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan? _forcedLiveMemoryInterval;

    private DateTime? _forcedLiveMemoryDueAt;
    private long _searchCount;

    private long _lastPrivateBytes;

    public DiagnosticsCollector(
        IIndexLifecycleService indexLifecycle,
        IThumbnailService thumbnails,
        int requestedThumbnailSize,
        int folderItemLimit,
        Func<DateTime>? clock = null,
        TimeSpan? forcedLiveMemoryInterval = null)
    {
        _indexLifecycle = indexLifecycle ?? throw new ArgumentNullException(nameof(indexLifecycle));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _requestedThumbnailSize = requestedThumbnailSize;
        _folderItemLimit = folderItemLimit;
        _clock = clock ?? (() => DateTime.Now);
        if (forcedLiveMemoryInterval is { } interval)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(forcedLiveMemoryInterval));
            _forcedLiveMemoryInterval = interval;
        }
    }

    public DiagnosticsMetrics Metrics { get; } = new();

    public void Refresh()
    {
        var now = _clock();
        CollectProcess(now);
        CollectMemory(now);
        CollectForcedLiveMemory(now);
        CollectIo(now);
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

    public void RecordSearch(int queryLength, TimeSpan duration, int resultCount)
    {
        var count = Interlocked.Increment(ref _searchCount);

        Metrics.Set(
            GroupSearch, "sorgu", count.ToString("N0"),
            DiagnosticsSeverity.Normal, count);
        Metrics.Set(
            GroupSearch, "son sorgu uzunluğu", queryLength.ToString("N0"),
            DiagnosticsSeverity.Normal, queryLength);
        Metrics.Set(
            GroupSearch,
            "son süre",
            FormatDuration(duration),
            duration.TotalMilliseconds >= 500d
                ? DiagnosticsSeverity.Warning
                : DiagnosticsSeverity.Good,
            duration.TotalMilliseconds);
        Metrics.Set(
            GroupSearch, "son sonuç", resultCount.ToString("N0"),
            DiagnosticsSeverity.Normal, resultCount);
    }

    private void CollectProcess(DateTime now)
    {
        try
        {
            _process.Refresh();
            var privateBytes = _process.PrivateMemorySize64;
            _lastPrivateBytes = privateBytes;
            var workingSet = _process.WorkingSet64;
            var peakWorkingSet = _process.PeakWorkingSet64;
            Metrics.Set(
                GroupProcess, "private", FormatBytes(privateBytes),
                DiagnosticsSeverity.Normal, privateBytes);
            Metrics.Set(
                GroupProcess, "working set", FormatBytes(workingSet),
                DiagnosticsSeverity.Normal, workingSet);
            Metrics.Set(
                GroupProcess, "tepe working set", FormatBytes(peakWorkingSet),
                DiagnosticsSeverity.Normal, peakWorkingSet);
            Metrics.Set(
                GroupProcess, "iş parçacığı", _process.Threads.Count.ToString("N0"),
                DiagnosticsSeverity.Normal, _process.Threads.Count);
            Metrics.Set(
                GroupProcess, "handle", _process.HandleCount.ToString("N0"),
                DiagnosticsSeverity.Normal, _process.HandleCount);

            var uptime = now - _process.StartTime;
            Metrics.Set(
                GroupProcess, "çalışma süresi", FormatUptime(uptime),
                DiagnosticsSeverity.Normal, uptime.TotalSeconds);

            var cpu = _process.TotalProcessorTime;
            Metrics.Set(
                GroupProcess, "CPU (toplam)", FormatUptime(cpu),
                DiagnosticsSeverity.Normal, cpu.TotalSeconds);

            var cpuRate = _rates.Update(RateKeyCpu, cpu.TotalSeconds, now);
            if (cpuRate.HasValue)
            {
                var percent = cpuRate.Value / Environment.ProcessorCount * 100d;
                Metrics.Set(
                    GroupProcess,
                    "CPU %",
                    $"{percent:N1} %",
                    percent >= 25d ? DiagnosticsSeverity.Warning : DiagnosticsSeverity.Normal,
                    percent);
            }

            Metrics.Set(GroupProcess, "durum", "iyi", DiagnosticsSeverity.Good);
        }
        catch (Exception ex)
        {
            Metrics.Set(GroupProcess, "durum", ex.GetType().Name, DiagnosticsSeverity.Warning);
        }
    }

    private void CollectMemory(DateTime now)
    {
        var managed = GC.GetTotalMemory(false);
        Metrics.Set(
            GroupMemory, "yönetilen yığın", FormatBytes(managed),
            DiagnosticsSeverity.Normal, managed);

        if (_lastPrivateBytes > 0)
        {
            var native = Math.Max(0L, _lastPrivateBytes - managed);
            Metrics.Set(
                GroupMemory, "native pay", FormatBytes(native),
                DiagnosticsSeverity.Normal, native);
        }

        var info = GC.GetGCMemoryInfo();
        if (info.Index > 0)
        {
            Metrics.Set(
                GroupMemory, "yığın (son GC)", FormatBytes(info.HeapSizeBytes),
                DiagnosticsSeverity.Normal, info.HeapSizeBytes);
            Metrics.Set(
                GroupMemory, "ayrılmış (son GC)", FormatBytes(info.TotalCommittedBytes),
                DiagnosticsSeverity.Normal, info.TotalCommittedBytes);

            var fragmentation = info.HeapSizeBytes == 0
                ? 0d
                : (double)info.FragmentedBytes / info.HeapSizeBytes;
            Metrics.Set(
                GroupMemory,
                "parçalanma",
                $"{FormatBytes(info.FragmentedBytes)} (%{fragmentation * 100d:N1})",
                fragmentation >= 0.3d
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
                info.FragmentedBytes);
            Metrics.Set(
                GroupMemory,
                "GC duraklama %",
                $"{info.PauseTimePercentage:N2} %",
                info.PauseTimePercentage >= 5d
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
                info.PauseTimePercentage);
        }

        Metrics.Set(
            GroupMemory,
            "GC 0/1/2",
            $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        Metrics.Set(
            GroupMemory, "toplam ayrılan", FormatBytes(allocated),
            DiagnosticsSeverity.Normal, allocated);

        var allocationRate = _rates.Update(RateKeyAllocated, allocated, now);
        if (allocationRate.HasValue)
        {
            Metrics.Set(
                GroupMemory,
                "ayırma hızı",
                $"{FormatBytes((long)allocationRate.Value)}/sn",
                allocationRate.Value >= 20d * 1024 * 1024
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
                allocationRate.Value);
        }
    }
    private void CollectForcedLiveMemory(DateTime now)
    {
        if (_forcedLiveMemoryInterval is not { } interval) return;

        if (_forcedLiveMemoryDueAt is null)
        {
            _forcedLiveMemoryDueAt = now + interval;
            Metrics.Set(
                GroupMemory, "canlı yığın (zorlanmış)", "bekleniyor",
                DiagnosticsSeverity.Normal);
            return;
        }

        if (now < _forcedLiveMemoryDueAt.Value) return;

        _forcedLiveMemoryDueAt = now + interval;

        var before = GC.GetTotalMemory(false);
        var stopwatch = Stopwatch.StartNew();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        stopwatch.Stop();
        var live = GC.GetTotalMemory(false);
        var reclaimed = before - live;

        Metrics.Set(
            GroupMemory, "canlı yığın (zorlanmış)", FormatBytes(live),
            DiagnosticsSeverity.Normal, live);
        Metrics.Set(
            GroupMemory,
            "  toplanan",
            before <= 0
                ? FormatBytes(reclaimed)
                : $"{FormatBytes(reclaimed)} (%{(double)reclaimed / before * 100d:N1})",
            DiagnosticsSeverity.Normal,
            reclaimed);
        Metrics.Set(
            GroupMemory,
            "  toplama süresi",
            $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms",
            stopwatch.Elapsed.TotalMilliseconds >= 500d
                ? DiagnosticsSeverity.Warning
                : DiagnosticsSeverity.Normal,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private void CollectIo(DateTime now)
    {
        var io = ProcessIoCounters.TryRead(_process);
        if (io == null)
        {
            Metrics.Set(GroupIo, "durum", "okunamadı", DiagnosticsSeverity.Warning);
            return;
        }

        var counters = io.Value;
        Metrics.Set(
            GroupIo, "okuma işlemi", counters.ReadOperations.ToString("N0"),
            DiagnosticsSeverity.Normal, counters.ReadOperations);
        Metrics.Set(
            GroupIo, "okunan", FormatBytes((long)counters.ReadBytes),
            DiagnosticsSeverity.Normal, counters.ReadBytes);
        Metrics.Set(
            GroupIo, "diğer işlem", counters.OtherOperations.ToString("N0"),
            DiagnosticsSeverity.Normal, counters.OtherOperations);
        Metrics.Set(
            GroupIo, "yazma işlemi", counters.WriteOperations.ToString("N0"),
            DiagnosticsSeverity.Normal, counters.WriteOperations);
        Metrics.Set(
            GroupIo, "yazılan", FormatBytes((long)counters.WriteBytes),
            DiagnosticsSeverity.Normal, counters.WriteBytes);

        var opsRate = _rates.Update(RateKeyReadOps, counters.ReadOperations, now);
        if (opsRate.HasValue)
        {
            Metrics.Set(
                GroupIo, "okuma işlemi/sn", $"{opsRate.Value:N0}",
                DiagnosticsSeverity.Normal, opsRate.Value);
        }

        var bytesRate = _rates.Update(RateKeyReadBytes, counters.ReadBytes, now);
        if (bytesRate.HasValue)
        {
            Metrics.Set(
                GroupIo, "okuma hızı", $"{FormatBytes((long)bytesRate.Value)}/sn",
                DiagnosticsSeverity.Normal, bytesRate.Value);
        }

        Metrics.Set(GroupIo, "durum", "okundu", DiagnosticsSeverity.Good);
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

            var report = _indexLifecycle.GetDiagnosticsReport();
            Metrics.Set(
                GroupIndex, "  tur", report.ReconciliationRuns.ToString("N0"),
                DiagnosticsSeverity.Normal, report.ReconciliationRuns);
            Metrics.Set(
                GroupIndex,
                "  son tur",
                report.LastReconciliationAt?.ToString("HH:mm:ss") ?? "yok");
            Metrics.Set(
                GroupIndex,
                "  son değişiklik",
                report.LastReconciliationChanges.ToString("N0"),
                report.LastReconciliationChanges > 0
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
                report.LastReconciliationChanges);
            Metrics.Set(
                GroupIndex, "  tarama süresi",
                FormatDuration(report.LastReconciliationScanDuration),
                DiagnosticsSeverity.Normal,
                report.LastReconciliationScanDuration.TotalMilliseconds);
            Metrics.Set(
                GroupIndex, "  tur süresi",
                FormatDuration(report.LastReconciliationDuration),
                DiagnosticsSeverity.Normal,
                report.LastReconciliationDuration.TotalMilliseconds);
            Metrics.Set(
                GroupIndex, "yeniden yayım", report.RepublishCount.ToString("N0"),
                DiagnosticsSeverity.Normal, report.RepublishCount);
            Metrics.Set(
                GroupIndex,
                "  son yayım",
                report.LastRepublishAt?.ToString("HH:mm:ss") ?? "yok");
            Metrics.Set(
                GroupIndex, "  yayım süresi",
                FormatDuration(report.LastRepublishDuration),
                report.LastRepublishDuration.TotalMilliseconds >= 1000d
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
                report.LastRepublishDuration.TotalMilliseconds);
            Metrics.Set(
                GroupIndex,
                "  son turda yayım",
                report.RepublishedDuringLastReconciliation ? "evet" : "hayır",
                report.RepublishedDuringLastReconciliation
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Good,
                report.RepublishedDuringLastReconciliation ? 1 : 0);
            Metrics.Set(
                GroupIndex, "yayımlanan girdi", report.SearchStateItemCount.ToString("N0"),
                DiagnosticsSeverity.Normal, report.SearchStateItemCount);
            Metrics.Set(GroupIndex, "durum", "iyi", DiagnosticsSeverity.Good);
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
        var byteFill = diagnostics.MemoryCacheByteLimit == 0
            ? 0d
            : (double)diagnostics.DecodedBytes / diagnostics.MemoryCacheByteLimit;
        Metrics.Set(
            GroupThumbnails,
            "önbellek boyutu",
            $"{FormatBytes(diagnostics.DecodedBytes)} / "
                + FormatBytes(diagnostics.MemoryCacheByteLimit),
            byteFill >= 1d
                ? DiagnosticsSeverity.Critical
                : byteFill >= 0.8d
                    ? DiagnosticsSeverity.Warning
                    : DiagnosticsSeverity.Normal,
            diagnostics.DecodedBytes);
        Metrics.Set(
            GroupThumbnails,
            "tahliye",
            diagnostics.Evictions.ToString("N0"),
            diagnostics.Evictions > 0
                ? DiagnosticsSeverity.Warning
                : DiagnosticsSeverity.Normal,
            diagnostics.Evictions);
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
            "işlemde / kuyrukta",
            $"{diagnostics.ActiveGenerations:N0} / {diagnostics.QueuedGenerations:N0}",
            diagnostics.QueuedGenerations > 0
                ? DiagnosticsSeverity.Warning
                : DiagnosticsSeverity.Normal,
            diagnostics.QueuedGenerations);
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

        if (diagnostics.DiskCacheMeasuredAt.HasValue)
        {
            Metrics.Set(
                GroupThumbnails,
                "disk önbelleği",
                $"{diagnostics.DiskCacheFileCount:N0} dosya · "
                + FormatBytes(diagnostics.DiskCacheBytes),
                DiagnosticsSeverity.Normal,
                diagnostics.DiskCacheBytes);
            Metrics.Set(
                GroupThumbnails,
                "  ölçüm",
                diagnostics.DiskCacheMeasuredAt.Value.ToString("HH:mm:ss"));
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:N1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / (1024d * 1024 * 1024):N2} GB";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "yok";
        if (duration.TotalMilliseconds < 1000d) return $"{duration.TotalMilliseconds:N0} ms";
        if (duration.TotalSeconds < 60d) return $"{duration.TotalSeconds:N1} sn";
        return $"{(int)duration.TotalMinutes} dk {duration.Seconds} sn";
    }

    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        return uptime.TotalDays >= 1d
            ? $"{(int)uptime.TotalDays}g {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}"
            : $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
    }
}
