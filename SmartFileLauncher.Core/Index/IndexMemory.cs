using System.Runtime;

namespace SmartFileLauncher.Core.Index;

internal static class IndexMemory
{
    internal static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
