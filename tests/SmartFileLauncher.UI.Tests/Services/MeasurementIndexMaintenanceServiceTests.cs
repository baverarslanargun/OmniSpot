using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.UI.Composition;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class MeasurementIndexMaintenanceServiceTests
{
    [Fact]
    public void ProductionCopyBlocksFolderHandoffAndRebuildBeforeInnerService()
    {
        var inner = new RecordingMaintenanceService();
        var service = new ApplicationCompositionRoot.MeasurementIndexMaintenanceService(
            inner,
            "uretim-kopya",
            blockOpenIndexFolder: true);

        Assert.Throws<InvalidOperationException>(() => service.OpenIndexFolder());
        Assert.Throws<InvalidOperationException>(() => service.ScheduleRebuild());
        Assert.Throws<InvalidOperationException>(() => service.ScheduleRestart());
        Assert.Equal(0, inner.OpenCalls);
        Assert.Equal(0, inner.RebuildCalls);
        Assert.Equal(0, inner.RestartCalls);
    }

    [Fact]
    public void EmptyProductionKeepsFolderHandoffButBlocksRebuild()
    {
        var inner = new RecordingMaintenanceService();
        var service = new ApplicationCompositionRoot.MeasurementIndexMaintenanceService(
            inner,
            "bos-uretim",
            blockOpenIndexFolder: false);

        Assert.True(service.OpenIndexFolder());
        Assert.Throws<InvalidOperationException>(() => service.ScheduleRebuild());
        Assert.Throws<InvalidOperationException>(() => service.ScheduleRestart());
        Assert.Equal(1, inner.OpenCalls);
        Assert.Equal(0, inner.RebuildCalls);
        Assert.Equal(0, inner.RestartCalls);
    }

    [Fact]
    public void TheRestartScript_RelaunchesWithoutDeletingTheIndex()
    {
        var script = IndexMaintenanceService.BuildRestartScript(
            @"C:\Uygulama\OmniSpot.exe",
            4242);

        Assert.Contains("OmniSpot.exe", script);
        Assert.Contains("PID eq 4242", script);
        Assert.DoesNotContain("del /f /q", script);
    }

    [Fact]
    public void TheRestartScript_WaitsForTheOldProcessBeforeRelaunching()
    {
        var script = IndexMaintenanceService.BuildRestartScript(
            @"C:\Uygulama\OmniSpot.exe",
            4242);

        var bekleme = script.IndexOf("wait_for_process", StringComparison.Ordinal);
        var yeniden = script.IndexOf(":relaunch", StringComparison.Ordinal);

        Assert.True(bekleme >= 0, "Betik eski süreci beklemiyor.");
        Assert.True(
            bekleme < yeniden,
            "Yeniden başlatma, eski sürecin çıkışı beklenmeden yapılmamalı; " +
            "aksi halde iki örnek aynı indeks ve kira üzerinde çakışır.");
    }

    private sealed class RecordingMaintenanceService : IIndexMaintenanceService
    {
        public int OpenCalls { get; private set; }
        public int RebuildCalls { get; private set; }
        public int RestartCalls { get; private set; }

        public IndexStorageStatus GetStatus() => new("index.db", true, 1);

        public bool OpenIndexFolder()
        {
            OpenCalls++;
            return true;
        }

        public void ScheduleRestart() => RestartCalls++;

        public void ScheduleRebuild() => RebuildCalls++;
    }
}
