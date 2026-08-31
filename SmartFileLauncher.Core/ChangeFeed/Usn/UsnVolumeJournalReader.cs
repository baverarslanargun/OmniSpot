using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnVolumeJournalReader : IUsnJournalReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const int PageBufferSize = 256 * 1024;

    private const int ErrorInvalidFunction = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;

    private readonly SafeFileHandle _volumeHandle;
    private readonly byte[] _pageBuffer = new byte[PageBufferSize];
    private bool _disposed;

    public UsnVolumeJournalReader(string volumeRootPath)
    {
        if (string.IsNullOrWhiteSpace(volumeRootPath))
        {
            throw new ArgumentException("Birim yolu boş olamaz.", nameof(volumeRootPath));
        }

        VolumeRootPath = ResolveVolumeRoot(volumeRootPath);
        _volumeHandle = OpenVolume(VolumeRootPath);
    }

    public string VolumeRootPath { get; }

    public static string ResolveVolumeRoot(string path)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(volumeRoot) ||
            volumeRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"USN günlüğü yalnız yerel sürücü köklerinde okunabilir: {path}");
        }

        return volumeRoot;
    }

    public UsnJournalDescriptor QueryJournal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var output = new byte[Marshal.SizeOf<UsnJournalDataV0>()];
        if (!DeviceIoControl(
                _volumeHandle,
                FsctlQueryUsnJournal,
                null,
                0,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw TranslateFailure(Marshal.GetLastWin32Error(), "FSCTL_QUERY_USN_JOURNAL");
        }

        if (bytesReturned < output.Length)
        {
            throw new UsnJournalUnavailableException(
                $"FSCTL_QUERY_USN_JOURNAL {bytesReturned} bayt döndürdü; {output.Length} bekleniyordu.");
        }

        var data = MemoryMarshal.Read<UsnJournalDataV0>(output);
        return new UsnJournalDescriptor(
            data.UsnJournalId,
            data.FirstUsn,
            data.NextUsn,
            data.LowestValidUsn,
            data.MaxUsn,
            data.MaximumSize,
            data.AllocationDelta);
    }

    public UsnReadPage ReadPage(long startUsn, ulong journalId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new ReadUsnJournalDataV1
        {
            StartUsn = startUsn,
            ReasonMask = uint.MaxValue,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = journalId,
            MinMajorVersion = 2,
            MaxMajorVersion = 3
        };

        var input = new byte[Marshal.SizeOf<ReadUsnJournalDataV1>()];
        MemoryMarshal.Write(input.AsSpan(), in request);

        if (!DeviceIoControl(
                _volumeHandle,
                FsctlReadUsnJournal,
                input,
                input.Length,
                _pageBuffer,
                _pageBuffer.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw TranslateFailure(Marshal.GetLastWin32Error(), "FSCTL_READ_USN_JOURNAL");
        }

        if (bytesReturned < sizeof(long))
        {
            throw new UsnJournalUnavailableException(
                $"FSCTL_READ_USN_JOURNAL {bytesReturned} bayt döndürdü; en az 8 bekleniyordu.");
        }

        var nextUsn = BitConverter.ToInt64(_pageBuffer);
        var records = _pageBuffer
            .AsSpan(sizeof(long), bytesReturned - sizeof(long))
            .ToArray();

        return new UsnReadPage(nextUsn, records);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _volumeHandle.Dispose();
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var device = @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar);

        var handle = CreateFile(
            device,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();

        if (error == ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException(
                $"{device} birimi açılamadı; USN günlüğü okuma yetkisi gerekiyor.");
        }

        throw new Win32Exception(error, $"{device} birimi açılamadı.");
    }

    internal static Exception TranslateFailure(int errorCode, string operation) =>
        errorCode switch
        {
            ErrorInvalidParameter =>
                new UsnProtocolRejectedException(
                    $"{operation} çağrı sözleşmesini reddetti: {errorCode}",
                    errorCode,
                    new Win32Exception(errorCode)),
            ErrorJournalNotActive or
            ErrorJournalDeleteInProgress or
            ErrorJournalEntryDeleted or
            ErrorInvalidFunction or
            ErrorNotSupported =>
                new UsnJournalUnavailableException(
                    $"{operation} başarısız: {errorCode}",
                    new Win32Exception(errorCode)),
            _ => new Win32Exception(errorCode, $"{operation} başarısız."),
        };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV1
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }
}
