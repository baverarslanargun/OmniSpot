using System.IO;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class DiagnosticsRedactionTests
{
    [Theory]
    [InlineData(@"C:\Users\sentinel\Documents\secret.txt")]
    [InlineData(@"D:\custom\sentinel.txt")]
    [InlineData(@"\\server\share\sentinel.txt")]
    [InlineData(@"%USERPROFILE%\sentinel.txt")]
    public void ProductionApplicationLogRedactsAbsoluteSentinel(string path)
    {
        var log = new ApplicationLog(redactPaths: true);

        log.Write($"index error: {path}");

        var message = Assert.Single(log.GetSnapshot());
        Assert.DoesNotContain(path, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<gizli-path>", message);
    }

    [Fact]
    public void FileSinkReceivesRedactedFolderEventAndHeaderManagedPath()
    {
        using var directory = new TemporaryDirectory();
        var log = new ApplicationLog(redactPaths: true);
        using var fileLog = new DiagnosticsFileLog(() => new DateTime(2026, 8, 28, 10, 0, 0));
        var managedPath = Path.Combine(directory.Path, "uretim-kopya-data", "index", "index.db");
        Assert.True(fileLog.Start(
            directory.Path,
            new[]
            {
                new KeyValuePair<string, string>("ölçüm profili", "uretim-kopya"),
                new KeyValuePair<string, string>("index.db", managedPath),
                new KeyValuePair<string, string>("indexed roots", "<gizli-path> (1 kök)")
            }));
        log.MessageWritten += fileLog.Write;
        log.Write(@"klasör olayı: C:\Users\sentinel\Documents\secret.txt");
        log.MessageWritten -= fileLog.Write;
        fileLog.Stop();

        var content = File.ReadAllText(fileLog.CurrentFilePath!);
        Assert.DoesNotContain(@"C:\Users\sentinel\Documents\secret.txt", content);
        Assert.Contains("uretim-kopya", content);
        Assert.Contains("<gizli-path>", content);
    }

    [Fact]
    public void MetricSinkRedactsFolderAndCustomPathEvents()
    {
        using var directory = new TemporaryDirectory();
        using var metricLog = new DiagnosticsMetricLog(
            () => new DateTime(2026, 8, 28, 10, 0, 0),
            DiagnosticPathRedactor.Redact);
        Assert.True(metricLog.Start(directory.Path));
        metricLog.WriteEvent(
            "klasör açıldı",
            @"C:\Users\sentinel\Documents\secret.txt");
        metricLog.WriteEvent(
            "custom root",
            @"\\server\share\secret.txt");
        metricLog.Stop();

        var content = File.ReadAllText(metricLog.CurrentFilePath!);
        Assert.DoesNotContain(@"C:\Users\sentinel\Documents\secret.txt", content);
        Assert.DoesNotContain(@"\\server\share\secret.txt", content);
        Assert.Contains("<gizli-path>", content);
    }

    [Fact]
    public void ProductionCrashArtifactRedactsNestedSentinelPaths()
    {
        using var directory = new TemporaryDirectory();
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "sentinel",
            "private.txt");
        var customPath = @"D:\custom\private.txt";
        var uncPath = @"\\server\share\private.txt";
        var inner = new InvalidOperationException($"inner failure: {userPath}");
        var outer = new ApplicationException(
            $"outer failure: {customPath} {uncPath}",
            inner);

        var artifactPath = Path.Combine(directory.Path, "omnispot_crash.log");
        File.WriteAllText(
            artifactPath,
            App.FormatCrash("Dispatcher", outer, new DateTime(2026, 8, 28, 10, 0, 0), true));
        var content = File.ReadAllText(artifactPath);

        Assert.DoesNotContain(userPath, content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(customPath, content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<gizli-path>", content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OmniSpot.UI.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
