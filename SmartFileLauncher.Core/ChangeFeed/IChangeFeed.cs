namespace SmartFileLauncher.Core.ChangeFeed;

public interface IChangeFeed : IDisposable
{
    string ProviderId { get; }

    string RootPath { get; }

    ChangeFeedRootIdentity RootIdentity { get; }

    ChangeFeedBatch Read(CancellationToken cancellationToken = default);

    void Accept();
}
