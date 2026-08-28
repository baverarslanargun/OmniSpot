using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartFileLauncher.Core.IO;

internal sealed class FileSystemPathGuard
{
    internal readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        uint NumberOfLinks);

    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;

    private readonly Func<string, FileAttributes?> _readAttributes;
    private readonly Func<string, IReadOnlyList<string>> _enumerateEntries;
    private readonly Func<string, string> _resolveExistingPath;

    public static FileSystemPathGuard Default { get; } = new(
        ReadAttributes,
        path => Directory.GetFileSystemEntries(path),
        ResolveExistingPath);

    internal FileSystemPathGuard(
        Func<string, FileAttributes?> readAttributes,
        Func<string, IReadOnlyList<string>> enumerateEntries,
        Func<string, string> resolveExistingPath)
    {
        _readAttributes = readAttributes ?? throw new ArgumentNullException(nameof(readAttributes));
        _enumerateEntries = enumerateEntries ?? throw new ArgumentNullException(nameof(enumerateEntries));
        _resolveExistingPath = resolveExistingPath ?? throw new ArgumentNullException(nameof(resolveExistingPath));
    }

    public string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Yol boş olamaz.", nameof(path));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    public string ResolvePhysicalPath(string path)
    {
        var canonicalPath = Canonicalize(path);
        var missingSegments = new Stack<string>();
        var existingPath = canonicalPath;

        while (_readAttributes(existingPath) == null)
        {
            var parent = Path.GetDirectoryName(existingPath);
            if (string.IsNullOrEmpty(parent) ||
                parent.Equals(existingPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"Yolun var olan bir üst dizini bulunamadı: {canonicalPath}");
            }

            missingSegments.Push(Path.GetFileName(existingPath));
            existingPath = parent;
        }

        var resolvedPath = Canonicalize(_resolveExistingPath(existingPath));
        while (missingSegments.Count > 0)
        {
            resolvedPath = Path.Combine(resolvedPath, missingSegments.Pop());
        }

        return Canonicalize(resolvedPath);
    }

    public string? FindReparsePointInExistingPath(string path)
    {
        var currentPath = Canonicalize(path);
        while (true)
        {
            var attributes = _readAttributes(currentPath);
            if (attributes.HasValue &&
                (attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                return currentPath;
            }

            var parent = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(parent) ||
                parent.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            currentPath = parent;
        }
    }

    public string? FindReparsePointInTree(string path)
    {
        var canonicalPath = Canonicalize(path);
        var ancestorReparsePoint = FindReparsePointInExistingPath(canonicalPath);
        if (ancestorReparsePoint != null)
        {
            return ancestorReparsePoint;
        }

        var pending = new Stack<string>();
        pending.Push(canonicalPath);

        while (pending.Count > 0)
        {
            var currentPath = pending.Pop();
            var attributes = _readAttributes(currentPath);
            if (!attributes.HasValue)
            {
                continue;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                return currentPath;
            }

            if ((attributes.Value & FileAttributes.Directory) == 0)
            {
                continue;
            }

            foreach (var entry in _enumerateEntries(currentPath))
            {
                var entryAttributes = _readAttributes(entry);
                if (!entryAttributes.HasValue)
                {
                    continue;
                }

                if ((entryAttributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    return Canonicalize(entry);
                }

                if ((entryAttributes.Value & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return null;
    }

    public bool HasMultipleLinks(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = CreateFile(
            Canonicalize(path),
            0,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return information.NumberOfLinks > 1;
    }

    internal FileIdentity GetFileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileIdentity(0, 0, 0, 1);
        }

        using var handle = CreateFile(
            Canonicalize(path),
            0,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            information.NumberOfLinks);
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string ResolveExistingPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        using var handle = CreateFile(
            path,
            0,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (length < capacity)
            {
                return RemoveDevicePrefix(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }
    }

    private static string RemoveDevicePrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";

        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
