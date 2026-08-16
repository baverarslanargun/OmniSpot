using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Tests.Profiling;

internal sealed class ProfileTestFixture : IDisposable
{
    internal ProfileTestFixture()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "omnispot-b1-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    internal string RootPath { get; }

    internal string CreateRoot(string name)
    {
        var path = Path.Combine(RootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static ProfileDocument Scan(string rootPath)
    {
        var fixedTime = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var scanner = new FileSystemProfileScanner(
            new BasicTokenizer(),
            utcNow: () => fixedTime,
            captureEnvironment: _ => CreateEnvironment());
        return scanner.Scan(
        [
            new ProfileRootRequest(rootPath, ProfileRootKind.Custom, 1)
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    internal static ProfileEnvironment CreateEnvironment(IReadOnlyList<string>? labels = null) =>
        new(
            "test-os",
            "test-framework",
            "8.0.100",
            "x64",
            "test-cpu",
            8,
            16L * 1024 * 1024 * 1024,
            ServerGc: false,
            "Interactive",
            OmniSpotProcessRunning: false,
            RepoHead: "0000000000000000000000000000000000000000",
            RepoDirty: false,
            RepoDirtyEntryCount: 0,
            PowerPlanGuid: null,
            DefenderRealtimeEnabled: null,
            WindowsSearchRunning: null,
            DiskKind: null,
            ProcessorThrottleMaxAcStartPercent: 99,
            ProcessorThrottleMaxDcStartPercent: 99,
            ProcessorThrottleMaxAcEndPercent: 99,
            ProcessorThrottleMaxDcEndPercent: 99,
            ProcessorNominalBaseMhz: 3300,
            ProcessorFrequencyStartMhz: 3120,
            ProcessorFrequencyEndMhz: 3130,
            ProcessorFrequencyDriftPercent: 0.32,
            Labels: labels ?? Array.Empty<string>());
}
