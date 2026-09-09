using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartFileLauncher.UI.Services;

public static class SystemMemoryReader
{
    public static SystemMemorySnapshot Read()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return default;
        using var process = Process.GetCurrentProcess();
        return new((long)Math.Min(status.TotalPhysical, (ulong)long.MaxValue),
            (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue), process.WorkingSet64);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
