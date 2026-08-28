using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.UI.Composition;
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
        Assert.Equal(0, inner.OpenCalls);
        Assert.Equal(0, inner.RebuildCalls);
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
        Assert.Equal(1, inner.OpenCalls);
        Assert.Equal(0, inner.RebuildCalls);
    }

    private sealed class RecordingMaintenanceService : IIndexMaintenanceService
    {
        public int OpenCalls { get; private set; }
        public int RebuildCalls { get; private set; }

        public IndexStorageStatus GetStatus() => new("index.db", true, 1);

        public bool OpenIndexFolder()
        {
            OpenCalls++;
            return true;
        }

        public void ScheduleRebuild() => RebuildCalls++;
    }
}
