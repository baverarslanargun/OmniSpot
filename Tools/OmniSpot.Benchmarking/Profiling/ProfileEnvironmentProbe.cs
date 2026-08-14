using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OmniSpot.Benchmarking.Profiling;

internal static partial class ProfileEnvironmentProbe
{
    internal static ProfileEnvironment Capture(IReadOnlyList<ProfileRootRequest> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var (repoHead, repoDirty, dirtyCount) = ReadRepositoryState();
        return new ProfileEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            ReadDotnetSdkVersion(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            ReadProcessorModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            IsOmniSpotRunning(),
            repoHead,
            repoDirty,
            dirtyCount,
            ReadPowerPlanGuid(),
            ReadDefenderRealtimeState(),
            ReadWindowsSearchState(),
            ReadDiskKind(roots));
    }

    private static string ReadProcessorModel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unknown";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim()
                ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? ReadDotnetSdkVersion()
    {
        var result = RunProcess("dotnet", "--version");
        var version = result.StandardOutput.Trim();
        return result.ExitCode == 0 && DotnetSdkRegex().IsMatch(version)
            ? version
            : null;
    }

    private static bool IsOmniSpotRunning()
    {
        foreach (var processName in new[] { "OmniSpot", "SmartFileLauncher.UI" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    private static (string? Head, bool? Dirty, int? DirtyCount) ReadRepositoryState()
    {
        var head = RunProcess("git", "rev-parse", "HEAD");
        if (head.ExitCode != 0 || !CommitHashRegex().IsMatch(head.StandardOutput.Trim()))
        {
            return (null, null, null);
        }

        var status = RunProcess(
            "git",
            "status",
            "--porcelain=v1",
            "--untracked-files=normal");
        if (status.ExitCode != 0)
        {
            return (head.StandardOutput.Trim(), null, null);
        }

        var count = CountRepositoryStatusEntries(status.StandardOutput);
        return (head.StandardOutput.Trim(), count > 0, count);
    }

    internal static int CountRepositoryStatusEntries(string statusOutput) =>
        statusOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private static string? ReadPowerPlanGuid()
    {
        var result = RunProcess("powercfg.exe", "/GETACTIVESCHEME");
        if (result.ExitCode != 0)
        {
            return null;
        }

        return GuidRegex().Match(result.StandardOutput) is { Success: true } match
            ? match.Value.ToLowerInvariant()
            : null;
    }

    private static bool? ReadWindowsSearchState()
    {
        var result = RunProcess("sc.exe", "query", "WSearch");
        if (result.ExitCode != 0)
        {
            return null;
        }

        var state = ServiceStateRegex().Match(result.StandardOutput);
        return state.Success ? state.Groups[1].Value == "4" : null;
    }

    private static bool? ReadDefenderRealtimeState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var result = RunPowerShell(
            "$status = Get-MpComputerStatus -ErrorAction Stop; " +
            "if ($status.RealTimeProtectionEnabled) " +
            "{ [Console]::Out.Write('1') } else { [Console]::Out.Write('0') }");
        return result.ExitCode == 0
            ? ParseBooleanProbe(result.StandardOutput)
            : null;
    }

    internal static bool? ParseBooleanProbe(string output) =>
        output.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

    private static string? ReadDiskKind(IReadOnlyList<ProfileRootRequest> roots)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var driveLetters = new SortedSet<char>();
        foreach (var root in roots)
        {
            string? pathRoot;
            try
            {
                pathRoot = Path.GetPathRoot(Path.GetFullPath(root.Path));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (pathRoot is not { Length: >= 2 } || pathRoot[1] != ':' ||
                !char.IsAsciiLetter(pathRoot[0]))
            {
                return null;
            }

            driveLetters.Add(char.ToUpperInvariant(pathRoot[0]));
        }

        if (driveLetters.Count == 0)
        {
            return null;
        }

        var mediaTypeOutput = new StringBuilder();
        foreach (var driveLetter in driveLetters)
        {
            var result = RunPowerShell(
                "Get-Partition -DriveLetter " + driveLetter + " -ErrorAction Stop | " +
                "Get-Disk -ErrorAction Stop | Get-PhysicalDisk -ErrorAction Stop | " +
                "ForEach-Object { [Console]::Out.WriteLine($_.MediaType.ToString()) }");
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            mediaTypeOutput.AppendLine(result.StandardOutput.Trim());
        }

        return ParseDiskKind(mediaTypeOutput.ToString());
    }

    internal static string? ParseDiskKind(string output)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kind = line.ToLowerInvariant() switch
            {
                "3" or "hdd" => "hdd",
                "4" or "ssd" => "ssd",
                "5" or "scm" => "scm",
                _ => null
            };
            if (kind is null)
            {
                return null;
            }

            kinds.Add(kind);
        }

        return kinds.Count switch
        {
            0 => null,
            1 => kinds.Single(),
            _ => "mixed"
        };
    }

    private static ProcessResult RunPowerShell(string command) =>
        RunProcess(
            "powershell.exe",
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            command);

    private static ProcessResult RunProcess(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return ProcessResult.Failed;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
                catch
                {
                }

                return ProcessResult.Failed;
            }

            if (!Task.WaitAll([standardOutput, standardError], 1000))
            {
                return ProcessResult.Failed;
            }

            return new ProcessResult(process.ExitCode, standardOutput.Result);
        }
        catch
        {
            return ProcessResult.Failed;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitHashRegex();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"STATE\s*:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceStateRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetSdkRegex();

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput)
    {
        internal static ProcessResult Failed { get; } = new(-1, string.Empty);
    }
}
