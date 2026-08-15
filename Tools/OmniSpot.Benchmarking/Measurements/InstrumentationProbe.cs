using System.Diagnostics;

namespace OmniSpot.Benchmarking.Measurements;

internal static class InstrumentationProbe
{
    internal static (double TimestampPairNanoseconds, double AllocationPairNanoseconds) Measure(
        int repetitions = 1_000_000)
    {
        _ = Stopwatch.GetTimestamp();
        _ = GC.GetAllocatedBytesForCurrentThread();

        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < repetitions; index++)
        {
            _ = Stopwatch.GetTimestamp();
            _ = Stopwatch.GetTimestamp();
        }

        var timestampElapsed = Stopwatch.GetTimestamp() - started;
        started = Stopwatch.GetTimestamp();
        for (var index = 0; index < repetitions; index++)
        {
            _ = GC.GetAllocatedBytesForCurrentThread();
            _ = GC.GetAllocatedBytesForCurrentThread();
        }

        var allocationElapsed = Stopwatch.GetTimestamp() - started;
        return (
            ToNanoseconds(timestampElapsed) / repetitions,
            ToNanoseconds(allocationElapsed) / repetitions);
    }

    private static double ToNanoseconds(long ticks) =>
        ticks * 1_000_000_000d / Stopwatch.Frequency;
}
