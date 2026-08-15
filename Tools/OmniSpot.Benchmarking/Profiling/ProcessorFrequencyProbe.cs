using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OmniSpot.Benchmarking.Profiling;

internal sealed record ProcessorFrequencySnapshot(
    int? ThrottleMaxAcPercent,
    int? ThrottleMaxDcPercent,
    int? NominalBaseMhz,
    int? LoadedMhz);

internal static class ProcessorFrequencyProbe
{
    internal const double DriftThresholdPercent = 2d;

    private const uint ErrorSuccess = 0;
    private const uint PdhFormatDouble = 0x00000200;
    private const string ProcessorPerformanceCounter =
        @"\Processor Information(_Total)\% Processor Performance";
    private static readonly Guid ProcessorSettingsSubgroup =
        new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorThrottleMaximum =
        new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    internal static ProcessorFrequencySnapshot Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessorFrequencySnapshot(null, null, null, null);
        }

        var (acPercent, dcPercent) = ReadThrottleMaximum();
        var nominalMhz = ReadNominalBaseMhz();
        return new ProcessorFrequencySnapshot(
            acPercent,
            dcPercent,
            nominalMhz,
            ReadLoadedMhz(nominalMhz));
    }

    internal static double? CalculateDriftPercent(int? startMhz, int? endMhz)
    {
        if (startMhz is not > 0 || endMhz is not > 0)
        {
            return null;
        }

        return Math.Abs(endMhz.Value - startMhz.Value) / (double)startMhz.Value * 100d;
    }

    internal static bool IsDrift(double? driftPercent) =>
        driftPercent is > DriftThresholdPercent;

    [SupportedOSPlatform("windows")]
    private static int? ReadNominalBaseMhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") switch
            {
                int value when value > 0 => value,
                uint value when value > 0 && value <= int.MaxValue => (int)value,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static (int? AcPercent, int? DcPercent) ReadThrottleMaximum()
    {
        IntPtr schemePointer = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePointer) != ErrorSuccess ||
                schemePointer == IntPtr.Zero)
            {
                return (null, null);
            }

            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            var subgroup = ProcessorSettingsSubgroup;
            var setting = ProcessorThrottleMaximum;
            var acResult = PowerReadAcValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref setting,
                out var acValue);
            var dcResult = PowerReadDcValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref setting,
                out var dcValue);
            return (
                acResult == ErrorSuccess && acValue <= 100 ? (int)acValue : null,
                dcResult == ErrorSuccess && dcValue <= 100 ? (int)dcValue : null);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
            {
                _ = LocalFree(schemePointer);
            }
        }
    }

    private static int? ReadLoadedMhz(int? nominalMhz)
    {
        if (nominalMhz is not > 0 ||
            PdhOpenQuery(null, IntPtr.Zero, out var query) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            if (PdhAddEnglishCounter(
                    query,
                    ProcessorPerformanceCounter,
                    IntPtr.Zero,
                    out var counter) != ErrorSuccess)
            {
                return null;
            }

            var stop = 0;
            var workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount))
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        while (Volatile.Read(ref stop) == 0)
                        {
                            Thread.SpinWait(256);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();
            try
            {
                if (PdhCollectQueryData(query) != ErrorSuccess)
                {
                    return null;
                }

                var samples = new List<double>(3);
                for (var index = 0; index < 3; index++)
                {
                    Thread.Sleep(300);
                    if (PdhCollectQueryData(query) != ErrorSuccess ||
                        PdhGetFormattedCounterValue(
                            counter,
                            PdhFormatDouble,
                            out _,
                            out var value) != ErrorSuccess ||
                        value.Status != ErrorSuccess ||
                        !double.IsFinite(value.DoubleValue) ||
                        value.DoubleValue <= 0)
                    {
                        return null;
                    }

                    samples.Add(value.DoubleValue);
                }

                samples.Sort();
                return (int)Math.Round(
                    nominalMhz.Value * samples[samples.Count / 2] / 100d,
                    MidpointRounding.AwayFromZero);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                _ = Task.WaitAll(workers, millisecondsTimeout: 2_000);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)]
        internal uint Status;

        [FieldOffset(8)]
        internal double DoubleValue;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(
        IntPtr userRootPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadACValueIndex")]
    private static extern uint PowerReadAcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadDCValueIndex")]
    private static extern uint PowerReadDcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(
        string? dataSource,
        IntPtr userData,
        out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(
        IntPtr query,
        string fullCounterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
