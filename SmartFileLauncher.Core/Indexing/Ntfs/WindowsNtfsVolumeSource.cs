using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class WindowsNtfsVolumeSource : INtfsVolumeSource
{
    private const uint GetNtfsVolumeData = 0x00090064;
    private const uint GetNtfsFileRecord = 0x00090068;
    private readonly SafeFileHandle _handle;

    public WindowsNtfsVolumeSource(string volumeRootPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Ham MFT okuma yalnız Windows üzerinde destekleniyor.");
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRootPath);
        var root = Path.GetPathRoot(volumeRootPath);
        if (!Path.IsPathFullyQualified(volumeRootPath) || root is null || root.Length != 3 ||
            root[1] != ':' ||
            !string.Equals(Path.TrimEndingDirectorySeparator(volumeRootPath),
                Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Ham MFT okuyucusu yerel sürücü kökü bekliyor.");

        _handle = CreateFile(@"\\.\" + root[..2], 0x80000000, 7,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "MFT için birim okuma tutamağı açılamadı.");
        }
        try { Geometry = ReadGeometry(); }
        catch { _handle.Dispose(); throw; }
    }

    public NtfsVolumeGeometry Geometry { get; }

    public byte[]? ReadFileRecord(ulong reference)
    {
        var input = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, reference);
        var output = new byte[12 + Geometry.BytesPerRecord];
        var count = Control(GetNtfsFileRecord, input, output);
        if (count < 12 || BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)) != Geometry.BytesPerRecord ||
            count < output.Length)
            throw new InvalidDataException("İstenen MFT segmenti dönmedi veya yanıt tamamlanmadı.");
        var returned = BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000FFFFFFFFFFFF;
        var requested = reference & 0x0000FFFFFFFFFFFF;
        if (returned < requested)
            return null;
        if (returned != requested)
            throw new InvalidDataException("İstenen MFT segmentinden farklı bir kayıt döndü.");
        return output.AsSpan(12, Geometry.BytesPerRecord).ToArray();
    }

    public void ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var sectorSize = Geometry.BytesPerSector;
        var prefix = (int)(offset % sectorSize);
        if (prefix == 0 && destination.Length % sectorSize == 0)
        {
            ReadExactly(offset, destination);
            return;
        }
        var length = checked((prefix + destination.Length + sectorSize - 1) / sectorSize * sectorSize);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            ReadExactly(offset - prefix, buffer.AsSpan(0, length));
            buffer.AsSpan(prefix, destination.Length).CopyTo(destination);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public void Dispose() => _handle.Dispose();

    private NtfsVolumeGeometry ReadGeometry()
    {
        var output = new byte[128];
        var count = Control(GetNtfsVolumeData, null, output);
        if (count < 104 || BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(100)) != 3 ||
            BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(102)) > 1)
            throw new NotSupportedException("MFT okuyucusu NTFS 3.0/3.1 birimi bekliyor.");
        var geometry = new NtfsVolumeGeometry(
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(40))),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(44))),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(48))),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(56)));
        if (!IsPowerOfTwo(geometry.BytesPerSector) || geometry.BytesPerSector is < 512 or > 65536 ||
            !IsPowerOfTwo(geometry.BytesPerCluster) || geometry.BytesPerCluster < geometry.BytesPerSector ||
            !IsPowerOfTwo(geometry.BytesPerRecord) || geometry.BytesPerRecord is < 512 or > 65536 ||
            geometry.TotalClusters <= 0 || geometry.TotalClusters > long.MaxValue / geometry.BytesPerCluster)
            throw new InvalidDataException("NTFS birim geometrisi geçersiz.");
        return geometry;
    }

    private int Control(uint code, byte[]? input, byte[] output)
    {
        if (!DeviceIoControl(_handle, code, input, input?.Length ?? 0,
                output, output.Length, out var count, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "NTFS metadata çağrısı başarısız.");
        if (count < 0 || count > output.Length)
            throw new InvalidDataException("NTFS yanıt boyutu geçersiz.");
        return count;
    }

    private void ReadExactly(long offset, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var count = RandomAccess.Read(_handle, destination, offset);
            if (count == 0)
                throw new EndOfStreamException("MFT okunurken birim verisi beklenmedik biçimde bitti.");
            destination = destination[count..];
            offset = checked(offset + count);
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte[]? inputBuffer, int inputBufferSize,
        byte[] outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);
}
