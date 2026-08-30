using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Reads volume and file identity with <c>GetFileInformationByHandleEx</c>
/// (<c>FileIdInfo</c>), which returns a 128-bit identity on both NTFS and ReFS
/// and therefore matches what <c>USN_RECORD_V2</c> and <c>USN_RECORD_V3</c>
/// report.
/// </summary>
/// <remarks>
/// Opening a path for identity needs no elevation; only reading the journal does.
/// </remarks>
public sealed class UsnFileSystemIdentityProbe : IUsnIdentityProbe
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileIdInfoClass = 18;

    private readonly bool _followReparsePoints;

    public UsnFileSystemIdentityProbe(bool followReparsePoints = false)
    {
        _followReparsePoints = followReparsePoints;
    }

    public bool TryReadIdentity(string path, out UsnNodeIdentity identity)
    {
        identity = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var flags = FileFlagBackupSemantics;
        if (!_followReparsePoints)
        {
            flags |= FileFlagOpenReparsePoint;
        }

        using var handle = CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out var information,
                Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        identity = new UsnNodeIdentity(
            information.VolumeSerialNumber,
            new UsnFileReference(information.FileIdLow, information.FileIdHigh));

        return true;
    }

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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileIdInfo information,
        int bufferSize);

    /// <summary>
    /// <c>FILE_ID_INFO</c>. The 16-byte <c>FILE_ID_128</c> identifier is read as
    /// two little-endian words so the struct stays blittable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }
}
