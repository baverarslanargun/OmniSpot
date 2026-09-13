using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class HierarchyProbeCaptureTests
{
    [Fact]
    public void SinkIoFailureDoesNotBecomeAFileSystemAccessWarning()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        using var manager = IndexManager.CreateWithDatabasePath(Path.Combine(workspace.Path, "index.db"),
            enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        var error = Assert.Throws<InvalidOperationException>(() =>
            manager.CaptureHierarchyProbe(root, _ => throw new IOException("write failed"), default));
        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public async Task StreamingCaptureMatchesBootstrapMetadataAndExclusions()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(@"root\inner\İş 📄.txt", "metadata");
        var hidden = workspace.CreateFile(@"root\hidden.txt", "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        using var manager = IndexManager.CreateWithDatabasePath(Path.Combine(workspace.Path, "index.db"),
            enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        var captured = new List<IndexManager.HierarchyProbeEntry>();
        Assert.Empty(manager.CaptureHierarchyProbe(root, captured.Add, default));
        await manager.BootstrapHierarchyProbeAsync(root, null, default);
        var expected = manager.CurrentSearchState.GetAllItems().OrderBy(item => item.FullPath).ToArray();
        Assert.Equal(expected, captured.Select(entry => entry.Item).OrderBy(item => item.FullPath).ToArray());
        Assert.DoesNotContain(captured, entry => entry.Item.FullPath == hidden);
    }
}
