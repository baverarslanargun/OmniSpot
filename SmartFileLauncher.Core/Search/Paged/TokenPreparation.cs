using System.Diagnostics;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    internal void PrepareTokenBatch(int maximumSlots, CancellationToken ct)
    {
        if (!Data.Mutable || maximumSlots < 1) throw new InvalidOperationException();
        if (Data.TermsReady) return;
        var end = (int)Math.Min(MaxId, (long)Data.TokenBuildCursor + maximumSlots);
        var clock = Stopwatch.StartNew();
        for (var at = Data.TokenBuildCursor; at < end; at++)
        {
            ct.ThrowIfCancellationRequested();
            var id = (uint)at;
            if (Alive(id)) AddTerms(id, Name(id));
            Data.TokenBuildCursor = at + 1;
            if ((at & 127) == 0 && clock.ElapsedMilliseconds >= 64) break;
        }
        Data.TermsReady = Data.TokenBuildCursor >= MaxId;
    }
}
