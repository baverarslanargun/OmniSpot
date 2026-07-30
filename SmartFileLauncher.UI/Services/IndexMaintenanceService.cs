using System.Diagnostics;
using System.IO;
using SmartFileLauncher.Core.Application.Indexing;

namespace SmartFileLauncher.UI.Services;

public sealed class IndexMaintenanceService : IIndexMaintenanceService
{
    internal const string RebuildFailedArgument = "--index-rebuild-failed";

    private readonly string _indexPath;

    public IndexMaintenanceService(string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        _indexPath = indexPath;
    }

    public IndexStorageStatus GetStatus()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                return new IndexStorageStatus(
                    _indexPath,
                    true,
                    new FileInfo(_indexPath).Length / 1024);
            }
        }
        catch
        {
        }

        return new IndexStorageStatus(_indexPath, false, 0);
    }

    public bool OpenIndexFolder()
    {
        var folderPath = Path.GetDirectoryName(_indexPath);
        if (string.IsNullOrEmpty(folderPath) ||
            !Directory.Exists(folderPath))
        {
            return false;
        }

        Process.Start("explorer.exe", folderPath);
        return true;
    }

    public void ScheduleRebuild()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(executablePath))
        {
            throw new InvalidOperationException("Uygulama yolu bulunamadı.");
        }

        var batchPath = Path.Combine(
            Path.GetTempPath(),
            $"omnispot_rebuild_index_{Environment.ProcessId}.bat");
        var processId = Environment.ProcessId;
        var commands = $@"@echo off
setlocal
set /a waitAttempts=0
set /a deleteAttempts=0

:wait_for_process
tasklist /FI ""PID eq {processId}"" /NH 2>nul | findstr /R /C:""[ ]{processId}[ ]"" >nul
if errorlevel 1 goto delete_index
set /a waitAttempts+=1
if %waitAttempts% GEQ 120 goto cleanup
timeout /t 1 /nobreak >nul
goto wait_for_process

:delete_index
set /a deleteAttempts+=1
del /f /q ""{_indexPath}"" 2>nul
del /f /q ""{_indexPath}-wal"" 2>nul
del /f /q ""{_indexPath}-shm"" 2>nul
if exist ""{_indexPath}"" goto retry_delete
if exist ""{_indexPath}-wal"" goto retry_delete
if exist ""{_indexPath}-shm"" goto retry_delete
start """" ""{executablePath}""
goto cleanup

:retry_delete
if %deleteAttempts% GEQ 10 goto restart_after_failure
timeout /t 1 /nobreak >nul
goto delete_index

:restart_after_failure
start """" ""{executablePath}"" {RebuildFailedArgument}

:cleanup
endlocal
del ""%~f0""
";
        File.WriteAllText(batchPath, commands);
        Process.Start(new ProcessStartInfo
        {
            FileName = batchPath,
            CreateNoWindow = true,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
