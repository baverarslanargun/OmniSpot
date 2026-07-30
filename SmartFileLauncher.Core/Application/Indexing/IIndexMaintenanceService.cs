namespace SmartFileLauncher.Core.Application.Indexing;

public sealed record IndexStorageStatus(
    string Path,
    bool Exists,
    long SizeKilobytes);

public interface IIndexMaintenanceService
{
    IndexStorageStatus GetStatus();
    bool OpenIndexFolder();
    void ScheduleRebuild();
}
