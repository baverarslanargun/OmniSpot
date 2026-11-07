using System.Diagnostics;
using System.Text.Json;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

internal sealed class SharedCatalogProfile
{
    private readonly Dictionary<string, Timing> _timings = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    internal Scope Measure(string stage, string? path = null) => new(this, stage, path);
    private void Add(string stage, string? path, long started)
    {
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Record(stage, milliseconds, path);
    }
    internal void Record(string stage, double milliseconds, string? path = null)
    {
        if (!_timings.TryGetValue(stage, out var timing)) _timings.Add(stage, timing = new());
        timing.Calls++;
        timing.Milliseconds += milliseconds;
        if (milliseconds > timing.MaximumMilliseconds)
        {
            timing.MaximumMilliseconds = milliseconds;
            timing.SlowestPath = path;
        }
    }
    internal void Save(string directory, string phase, long sequence = 0, long events = 0)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, phase == "service-ready" ? "usn-startup-profile.json" : "usn-profile.json"), JsonSerializer.Serialize(new
            {
                Process = Environment.ProcessId, UpdatedUtc = DateTime.UtcNow, Phase = phase,
                ElapsedMilliseconds = _elapsed.Elapsed.TotalMilliseconds, Sequence = sequence, Events = events,
                Timings = _timings
            }));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("USN süre dökümü yazılamadı: {0}", failure.Message); }
    }
    private sealed class Timing
    {
        public long Calls { get; set; }
        public double Milliseconds { get; set; }
        public double MaximumMilliseconds { get; set; }
        public string? SlowestPath { get; set; }
    }
    internal readonly struct Scope(SharedCatalogProfile owner, string stage, string? path) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => owner.Add(stage, path, _started);
    }
}
