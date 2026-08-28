using System.Diagnostics;

namespace SmartFileLauncher.Core.Tests.TestInfrastructure;

internal static class WindowsDirectoryLink
{
    public static void CreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows junction test requires Windows.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Junction helper başlatılamadı.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            var output = process.StandardOutput.ReadToEnd();
            throw new InvalidOperationException(
                $"Windows junction oluşturulamadı: {error} {output}".Trim());
        }
    }

    public static void Delete(string linkPath)
    {
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath);
        }
    }
}
