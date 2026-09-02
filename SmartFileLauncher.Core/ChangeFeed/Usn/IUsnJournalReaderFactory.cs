namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public interface IUsnJournalReaderFactory
{
    IUsnJournalReader Open(string volumeRootPath);
}

public sealed class UsnVolumeJournalReaderFactory : IUsnJournalReaderFactory
{
    public IUsnJournalReader Open(string volumeRootPath) =>
        new UsnVolumeJournalReader(volumeRootPath);
}
