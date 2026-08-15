using System.Diagnostics;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal static class SteadyMemoryProbe
{
    internal static IReadOnlyList<SteadyMemorySample> Run(
        SyntheticSearchFixture fixture,
        IReadOnlyList<int> idleSeconds,
        CancellationToken cancellationToken)
    {
        if (idleSeconds.Count == 0)
        {
            return Array.Empty<SteadyMemorySample>();
        }

        var orderedSeconds = idleSeconds
            .Distinct()
            .Order()
            .ToArray();
        if (orderedSeconds[0] < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(idleSeconds));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var state = SearchState.Create(fixture.Nodes, new BasicTokenizer());
        var samples = new List<SteadyMemorySample>(orderedSeconds.Length);
        var stopwatch = Stopwatch.StartNew();
        foreach (var targetSeconds in orderedSeconds)
        {
            var remaining = TimeSpan.FromSeconds(targetSeconds) - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                Task.Delay(remaining, cancellationToken).GetAwaiter().GetResult();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            samples.Add(new SteadyMemorySample(
                targetSeconds,
                GC.GetTotalMemory(forceFullCollection: false),
                process.PrivateMemorySize64));
        }

        GC.KeepAlive(state);
        return samples;
    }
}
