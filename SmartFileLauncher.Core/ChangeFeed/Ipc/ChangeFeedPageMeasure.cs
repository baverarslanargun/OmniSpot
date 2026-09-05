namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public interface IChangeFeedPageMeasure
{
    long Envelope { get; }

    long Root(string rootPath);

    long Event(ChangeFeedEvent change);
}
