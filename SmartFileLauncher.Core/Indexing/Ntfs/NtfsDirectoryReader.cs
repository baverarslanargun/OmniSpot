using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class NtfsDirectoryReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer = new byte[64 * 1024];
    internal NtfsMftEntry Root { get; }
    internal string PhysicalPath { get; }
    internal string VolumeIdentity { get; }

    internal NtfsDirectoryReader(string path)
    {
        _handle = CreateFile(path, 0x81, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (_handle.IsInvalid) throw new IOException("Klasör açılamadı: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (!GetFileInformationByHandle(_handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var id = ((ulong)info.IndexHigh << 32) | info.IndexLow;
            VolumeIdentity = $"native-vsn32:{info.Volume:X8}";
            Root = new(id, id, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), true,
                (FileAttributes)info.Attributes, 0, FileTime(info.CreatedLow, info.CreatedHigh), FileTime(info.ModifiedLow, info.ModifiedHigh));
            var physical = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(_handle, physical, physical.Capacity, 0);
            if (length == 0 || length >= physical.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            PhysicalPath = physical.ToString();
        }
        catch { _handle.Dispose(); throw; }
    }

    internal IReadOnlyList<NtfsMftEntry> ReadNext()
    {
        if (!GetFileInformationByHandleEx(_handle, 10, _buffer, _buffer.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 18) return [];
            throw new IOException("Klasör kayıtları okunamadı.", new Win32Exception(error));
        }
        var entries = new List<NtfsMftEntry>();
        var offset = 0;
        while (true)
        {
            if (offset > _buffer.Length - 104) throw new InvalidDataException("Dizin kayıt sınırı geçersiz.");
            var record = _buffer.AsSpan(offset);
            var next = BinaryPrimitives.ReadInt32LittleEndian(record);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record[60..]);
            if (nameLength < 0 || nameLength % 2 != 0 || nameLength > record.Length - 104 ||
                next != 0 && (next < 104 + nameLength || next % 8 != 0 || next > record.Length))
                throw new InvalidDataException("Dizin adı veya kayıt uzunluğu geçersiz.");
            var name = new string(MemoryMarshal.Cast<byte, char>(record.Slice(104, nameLength)));
            if (name is not ("." or ".."))
            {
                var attributes = (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(record[56..]);
                entries.Add(new NtfsMftEntry(BinaryPrimitives.ReadUInt64LittleEndian(record[96..]),
                    Root.FileReference, name, (attributes & FileAttributes.Directory) != 0, attributes,
                    BinaryPrimitives.ReadInt64LittleEndian(record[40..]),
                    DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(record[8..])).Ticks,
                    DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(record[24..])).Ticks));
            }
            if (next == 0) return entries;
            offset += next;
        }
    }

    internal static NtfsMftEntry? ReadFile(string path, ulong parent)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            if (error is 32 or 33)
            {
                var parentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
                if (parentPath is not null)
                {
                    using var directory = new NtfsDirectoryReader(parentPath);
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    NtfsMftEntry? match = null;
                    while (true)
                    {
                        var entries = directory.ReadNext();
                        if (entries.Count == 0) return match;
                        foreach (var entry in entries)
                        {
                            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                                return entry with { ParentFileReference = parent };
                            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                                match = entry with { ParentFileReference = parent };
                        }
                    }
                }
            }
            throw new IOException("Dosya bilgisi okunamadı: " + path, new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = (FileAttributes)info.Attributes;
        return new(((ulong)info.IndexHigh << 32) | info.IndexLow, parent, Path.GetFileName(path),
            (attributes & FileAttributes.Directory) != 0, attributes,
            ((long)info.SizeHigh << 32) | info.SizeLow, FileTime(info.CreatedLow, info.CreatedHigh), FileTime(info.ModifiedLow, info.ModifiedHigh));
    }

    internal static string ReadPhysicalPath(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Klasör hedefi açılamadı.", new Win32Exception(Marshal.GetLastWin32Error()));
        var result = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, result, result.Capacity, 0);
        if (length == 0 || length >= result.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result.ToString();
    }

    internal static bool IsNameSurrogate(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Bağlantı bilgisi okunamadı.", new Win32Exception(Marshal.GetLastWin32Error()));
        var information = new byte[8];
        if (!GetFileInformationByHandleEx(handle, 9, information, information.Length))
            throw new IOException("Bağlantı türü okunamadı.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(4)) & 0x20000000) != 0;
    }

    internal bool HasNtfsIdentity()
    {
        var name = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(_handle, null, 0, out _, out _, out _, name, name.Capacity))
            throw new IOException("Dosya sistemi türü okunamadı.", new Win32Exception(Marshal.GetLastWin32Error()));
        return name.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    private static long FileTime(uint low, uint high) => DateTime.FromFileTimeUtc(((long)high << 32) | low).Ticks;
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        internal uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, ModifiedLow, ModifiedHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int information, [Out] byte[] buffer, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName, int volumeNameLength,
        out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystemName, int fileSystemNameLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
