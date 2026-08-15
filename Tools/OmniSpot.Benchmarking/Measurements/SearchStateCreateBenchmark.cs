using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

public class SearchStateCreateBenchmark
{
    private IReadOnlyList<FileSystemNode> _nodes = Array.Empty<FileSystemNode>();
    private BasicTokenizer _tokenizer = null!;
    private long _canaryDelayNanoseconds;

    [GlobalSetup]
    public void Setup()
    {
        var itemCount = ReadInt32("OMNISPOT_BENCH_ITEM_COUNT", minimum: 1);
        var seed = ReadInt32("OMNISPOT_BENCH_SEED", minimum: int.MinValue);
        _canaryDelayNanoseconds = ReadInt64(
            "OMNISPOT_BENCH_CANARY_DELAY_NS",
            minimum: 0);
        var expectedFingerprint = Environment.GetEnvironmentVariable(
            "OMNISPOT_BENCH_FIXTURE_FINGERPRINT");
        var fixture = SyntheticSearchFixtureGenerator.Create(itemCount, seed);
        if (!string.Equals(
                fixture.Manifest.Fingerprint,
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sentetik fixture parmak izi eşleşmedi.");
        }

        _nodes = fixture.Nodes;
        _tokenizer = new BasicTokenizer();
    }

    [Benchmark]
    public SearchState CreateSearchState()
    {
        var state = SearchState.Create(_nodes, _tokenizer);
        CanaryDelay.Wait(_canaryDelayNanoseconds);
        return state;
    }

    private static int ReadInt32(string name, int minimum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed >= minimum
            ? parsed
            : throw new InvalidOperationException(name + " geçerli değil.");
    }

    private static long ReadInt64(string name, long minimum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && parsed >= minimum
            ? parsed
            : throw new InvalidOperationException(name + " geçerli değil.");
    }
}

internal static class CanaryDelay
{
    internal static void Wait(long nanoseconds)
    {
        if (nanoseconds <= 0)
        {
            return;
        }

        var targetTicks = Math.Max(
            1L,
            (long)Math.Ceiling(nanoseconds * Stopwatch.Frequency / 1_000_000_000d));
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - started < targetTicks)
        {
            Thread.SpinWait(32);
        }
    }
}
