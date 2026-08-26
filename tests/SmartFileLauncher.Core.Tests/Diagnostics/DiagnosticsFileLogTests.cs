using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public class DiagnosticsFileLogTests
{
    private static readonly DateTime FixedStart = new(2026, 8, 26, 21, 34, 56, DateTimeKind.Local);

    [Fact]
    public void StartCreatesFileNamedWithSessionTimestamp()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);

        Assert.True(log.Start(directory.Path));

        Assert.Equal(
            Path.Combine(directory.Path, "omnispot-20260826-213456.log"),
            log.CurrentFilePath);
        Assert.True(File.Exists(log.CurrentFilePath));
    }

    [Fact]
    public void StartWritesStampsIntoHeader()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);

        log.Start(directory.Path, new[] {
            new KeyValuePair<string, string>("sürüm", "1.2.3"),
            new KeyValuePair<string, string>("süreç", "4242")
        });
        log.Stop();

        var contents = File.ReadAllText(log.CurrentFilePath!);
        Assert.Contains("2026-08-26 21:34:56.000", contents);
        Assert.Contains("1.2.3", contents);
        Assert.Contains("4242", contents);
    }

    [Fact]
    public void WrittenLinesAppearVerbatim()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);
        log.Start(directory.Path);

        log.Write("[21:34:57.001] ilk");
        log.Write("[21:34:57.002] ikinci");
        log.Stop();

        var lines = File.ReadAllLines(log.CurrentFilePath!);
        Assert.Contains("[21:34:57.001] ilk", lines);
        Assert.Contains("[21:34:57.002] ikinci", lines);
        Assert.Equal(2, log.WrittenLines);
    }

    [Fact]
    public void WriteBeforeStartIsIgnored()
    {
        using var log = new DiagnosticsFileLog(() => FixedStart);

        log.Write("kayıp");

        Assert.False(log.IsWriting);
        Assert.Equal(0, log.WrittenLines);
        Assert.Null(log.CurrentFilePath);
    }

    [Fact]
    public void WriteAfterStopIsIgnored()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);
        log.Start(directory.Path);
        var path = log.CurrentFilePath!;
        log.Stop();

        log.Write("durduktan sonra");

        Assert.DoesNotContain("durduktan sonra", File.ReadAllText(path));
    }

    [Fact]
    public void StopWritesClosingLineWithCount()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);
        log.Start(directory.Path);
        log.Write("a");
        log.Write("b");
        var path = log.CurrentFilePath!;

        log.Stop();

        Assert.Contains("2 satır", File.ReadAllText(path));
        Assert.False(log.IsWriting);
    }

    [Fact]
    public void RestartingClosesPreviousFile()
    {
        using var directory = new TemporaryDirectory();
        var moment = FixedStart;
        using var log = new DiagnosticsFileLog(() => moment);

        log.Start(directory.Path);
        var first = log.CurrentFilePath!;
        log.Write("birinci oturum");

        moment = FixedStart.AddMinutes(1);
        log.Start(directory.Path);
        var second = log.CurrentFilePath!;

        Assert.NotEqual(first, second);
        Assert.Contains("satır", File.ReadAllText(first));
        Assert.Equal(0, log.WrittenLines);
    }

    [Fact]
    public void StartOnUnusableDirectoryReportsFailure()
    {
        using var directory = new TemporaryDirectory();
        var blocker = directory.CreateFile("engel");
        using var log = new DiagnosticsFileLog(() => FixedStart);

        Assert.False(log.Start(blocker));
        Assert.False(log.IsWriting);
        Assert.NotNull(log.LastError);
    }

    [Fact]
    public void DisposeClosesTheFile()
    {
        using var directory = new TemporaryDirectory();
        string path;
        using (var log = new DiagnosticsFileLog(() => FixedStart))
        {
            log.Start(directory.Path);
            log.Write("tek satır");
            path = log.CurrentFilePath!;
        }

        Assert.Contains("1 satır", File.ReadAllText(path));
    }

    [Fact]
    public void ConcurrentWritersLoseNoLines()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsFileLog(() => FixedStart);
        log.Start(directory.Path);

        Parallel.For(0, 8, worker =>
        {
            for (var index = 0; index < 100; index++)
            {
                log.Write($"w{worker}-{index}");
            }
        });

        var path = log.CurrentFilePath!;
        var error = log.LastError;
        log.Stop();

        var lines = File.ReadAllLines(path);
        var written = lines.Count(line => line.StartsWith('w'));
        if (written != 800)
        {
            var odd = lines
                .Where(line => line.StartsWith('w') && line.Count(c => c == '-') != 1)
                .Take(5)
                .ToArray();
            File.Copy(path, Path.Combine(Path.GetTempPath(), "omnispot-eksik-satir.log"), true);
            Assert.Fail(
                $"dosyada {written} satır var, beklenen 800. sayaç={log.WrittenLines}, "
                + $"hata={error ?? "yok"}, bozuk örnek=[{string.Join(" | ", odd)}]");
        }

        Assert.Equal(800, log.WrittenLines);
    }
}
