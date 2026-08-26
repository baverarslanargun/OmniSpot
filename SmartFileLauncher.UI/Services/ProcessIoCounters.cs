using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmartFileLauncher.UI.Services;

public readonly record struct ProcessIoSnapshot(
    ulong ReadOperations,
    ulong WriteOperations,
    ulong OtherOperations,
    ulong ReadBytes,
    ulong WriteBytes,
    ulong OtherBytes);

[SupportedOSPlatform("windows")]
public static class ProcessIoCounters
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    public static ProcessIoSnapshot? TryRead(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (!GetProcessIoCounters(process.Handle, out var counters))
                return null;

            return new ProcessIoSnapshot(
                counters.ReadOperationCount,
                counters.WriteOperationCount,
                counters.OtherOperationCount,
                counters.ReadTransferCount,
                counters.WriteTransferCount,
                counters.OtherTransferCount);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
