using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace OmniSpot.UsnSmoke;

internal static class Program
{
    private const string ShowNamesSwitch = "--adlar";
    private const int SampleNameCount = 5;

    private static int Main(string[] args)
    {
        var target = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? @"C:\";
        var showNames = args.Any(
            argument => string.Equals(argument, ShowNamesSwitch, StringComparison.OrdinalIgnoreCase));

        string volumeRoot;
        try
        {
            volumeRoot = UsnVolumeJournalReader.ResolveVolumeRoot(target);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"Birim yolu çözülemedi: {Describe(failure)}");
            return 2;
        }

        Console.WriteLine("OmniSpot USN smoke");
        Console.WriteLine($"  Birim : {volumeRoot}");
        Console.WriteLine($"  Adlar : {(showNames ? "gösterilecek" : "gizli (--adlar ile açılır)")}");
        Console.WriteLine();

        UsnVolumeJournalReader reader;
        try
        {
            reader = new UsnVolumeJournalReader(volumeRoot);
        }
        catch (UnauthorizedAccessException failure)
        {
            Console.WriteLine(failure.Message);
            Console.WriteLine("Bu koşumu yönetici olarak açılmış bir konsolda çalıştırın.");
            return 2;
        }
        catch (Exception failure)
        {
            Console.WriteLine($"Birim açılamadı: {Describe(failure)}");
            return 2;
        }

        using (reader)
        {
            return Run(reader, volumeRoot, showNames);
        }
    }

    private static int Run(UsnVolumeJournalReader reader, string volumeRoot, bool showNames)
    {
        UsnJournalDescriptor descriptor;
        try
        {
            descriptor = reader.QueryJournal();
        }
        catch (Exception failure)
        {
            Console.WriteLine($"[1] FSCTL_QUERY_USN_JOURNAL BAŞARISIZ: {Describe(failure)}");
            return 2;
        }

        Console.WriteLine("[1] FSCTL_QUERY_USN_JOURNAL: başarılı");
        Console.WriteLine($"      JournalId       0x{descriptor.JournalId:X16}");
        Console.WriteLine($"      FirstUsn        {descriptor.FirstUsn}");
        Console.WriteLine($"      NextUsn         {descriptor.NextUsn}");
        Console.WriteLine($"      LowestValidUsn  {descriptor.LowestValidUsn}");
        Console.WriteLine($"      MaximumSize     {descriptor.MaximumSize / 1024 / 1024} MB");
        Console.WriteLine($"      AllocationDelta {descriptor.AllocationDelta / 1024 / 1024} MB");
        Console.WriteLine();

        var version1Works = ReadWithProductionReader(reader, descriptor, showNames);
        Console.WriteLine();
        var version0Works = ReadWithVersion0(volumeRoot, descriptor);
        Console.WriteLine();

        Console.WriteLine("[4] SONUÇ");
        if (version1Works)
        {
            Console.WriteLine("      READ_USN_JOURNAL_DATA_V1 kabul edildi.");
            Console.WriteLine("      Production okuyucu olduğu gibi kullanılabilir.");
            return 0;
        }

        if (version0Works)
        {
            Console.WriteLine("      V1 reddedildi, V0 kabul edildi.");
            Console.WriteLine("      Okuyucunun input struct'ı V0'a çekilmeli; V3 kayıt desteği kaybolur.");
            return 1;
        }

        Console.WriteLine("      Hem V1 hem V0 reddedildi.");
        Console.WriteLine("      Sorun input struct'ında değil; birim, yetki veya journal durumunda.");
        return 1;
    }

    private static bool ReadWithProductionReader(
        UsnVolumeJournalReader reader,
        UsnJournalDescriptor descriptor,
        bool showNames)
    {
        Console.WriteLine("[2] FSCTL_READ_USN_JOURNAL — production yol (READ_USN_JOURNAL_DATA_V1, min 2 max 3)");

        UsnReadPage page;
        try
        {
            page = reader.ReadPage(descriptor.FirstUsn, descriptor.JournalId);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"      BAŞARISIZ: {Describe(failure)}");
            return false;
        }

        var histogram = Histogram(page.Records.Span);
        Console.WriteLine($"      Kabul edildi. Sayfa {page.Records.Length} bayt, sonraki USN {page.NextUsn}.");
        Console.WriteLine(
            $"      Kayıt sürümleri: V2={histogram.Version2}  V3={histogram.Version3}  diğer={histogram.Other}");

        try
        {
            var records = UsnRecordParser.Parse(page.Records.Span);
            Console.WriteLine($"      Ayrıştırıcı {records.Count} kayıt okudu.");

            if (showNames)
            {
                foreach (var record in records.Take(SampleNameCount))
                {
                    Console.WriteLine(
                        $"        usn={record.Usn} dizin={record.IsDirectory} neden={record.Reason} ad={record.Name}");
                }
            }
        }
        catch (UsnRecordFormatException failure)
        {
            Console.WriteLine($"      Ayrıştırma BAŞARISIZ: {failure.Message}");
            return false;
        }

        try
        {
            var tail = reader.ReadPage(descriptor.NextUsn, descriptor.JournalId);
            Console.WriteLine($"      Kuyruk okuması: {tail.Records.Length} bayt, sonraki USN {tail.NextUsn}.");
        }
        catch (Exception failure)
        {
            Console.WriteLine($"      Kuyruk okuması BAŞARISIZ: {Describe(failure)}");
            return false;
        }

        return true;
    }

    private static bool ReadWithVersion0(string volumeRoot, UsnJournalDescriptor descriptor)
    {
        Console.WriteLine("[3] FSCTL_READ_USN_JOURNAL — karşılaştırma (READ_USN_JOURNAL_DATA_V0)");

        using var handle = Native.OpenVolume(volumeRoot);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"      Birim açılamadı: Win32 {Marshal.GetLastWin32Error()}");
            return false;
        }

        var buffer = new byte[64 * 1024];
        if (!Native.ReadWithVersion0(
                handle,
                descriptor.FirstUsn,
                descriptor.JournalId,
                buffer,
                out var bytesReturned))
        {
            Console.WriteLine($"      REDDEDİLDİ: Win32 {Marshal.GetLastWin32Error()}");
            return false;
        }

        if (bytesReturned < sizeof(long))
        {
            Console.WriteLine($"      Beklenmeyen çıktı: {bytesReturned} bayt.");
            return false;
        }

        var histogram = Histogram(buffer.AsSpan(sizeof(long), bytesReturned - sizeof(long)));
        Console.WriteLine($"      Kabul edildi. Sayfa {bytesReturned - sizeof(long)} bayt.");
        Console.WriteLine(
            $"      Kayıt sürümleri: V2={histogram.Version2}  V3={histogram.Version3}  diğer={histogram.Other}");
        return true;
    }

    private static RecordHistogram Histogram(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;
        var version2 = 0;
        var version3 = 0;
        var other = 0;

        while (offset + sizeof(uint) + sizeof(ushort) <= buffer.Length)
        {
            var length = BitConverter.ToUInt32(buffer[offset..]);
            if (length < sizeof(uint) + sizeof(ushort) || offset + length > buffer.Length)
            {
                break;
            }

            switch (BitConverter.ToUInt16(buffer[(offset + sizeof(uint))..]))
            {
                case 2:
                    version2++;
                    break;
                case 3:
                    version3++;
                    break;
                default:
                    other++;
                    break;
            }

            offset += (int)length;
        }

        return new RecordHistogram(version2, version3, other);
    }

    private static string Describe(Exception failure) => failure switch
    {
        UsnProtocolRejectedException rejection =>
            $"{rejection.Message} [Win32 {rejection.ErrorCode}]",
        UsnJournalUnavailableException unavailable when unavailable.InnerException is Win32Exception inner =>
            $"{unavailable.Message} [Win32 {inner.NativeErrorCode}]",
        Win32Exception win32 =>
            $"{win32.Message} [Win32 {win32.NativeErrorCode}]",
        _ => $"{failure.GetType().Name}: {failure.Message}"
    };

    private readonly record struct RecordHistogram(int Version2, int Version3, int Other);
}

internal static class Native
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FsctlReadUsnJournal = 0x000900BB;

    public static SafeFileHandle OpenVolume(string volumeRoot) =>
        CreateFile(
            @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar),
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

    public static bool ReadWithVersion0(
        SafeFileHandle volume,
        long startUsn,
        ulong journalId,
        byte[] buffer,
        out int bytesReturned)
    {
        var request = new ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = uint.MaxValue,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = journalId
        };

        var input = new byte[Marshal.SizeOf<ReadUsnJournalDataV0>()];
        MemoryMarshal.Write(input.AsSpan(), in request);

        return DeviceIoControl(
            volume,
            FsctlReadUsnJournal,
            input,
            input.Length,
            buffer,
            buffer.Length,
            out bytesReturned,
            IntPtr.Zero);
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
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }
}
