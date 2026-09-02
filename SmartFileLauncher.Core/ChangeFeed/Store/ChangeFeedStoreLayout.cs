using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedStoreLayout
{
    public const string ProductFolderName = "OmniSpot";
    public const string StoreFolderName = "ChangeFeed";
    public const string SubscriptionFileName = "subscription.json";
    public const string SequenceFileName = "sequence.txt";
    public const string QueueFolderName = "queue";
    public const string StateFolderName = "state";

    private ChangeFeedStoreLayout(string ownerSid, string ownerDirectory)
    {
        OwnerSid = ownerSid;
        OwnerDirectory = ownerDirectory;
        SubscriptionPath = Path.Combine(ownerDirectory, SubscriptionFileName);
        SequencePath = Path.Combine(ownerDirectory, SequenceFileName);
        QueueDirectory = Path.Combine(ownerDirectory, QueueFolderName);
        StateDirectory = Path.Combine(ownerDirectory, StateFolderName);
    }

    public string OwnerSid { get; }

    public string OwnerDirectory { get; }

    public string SubscriptionPath { get; }

    public string SequencePath { get; }

    public string QueueDirectory { get; }

    public string StateDirectory { get; }

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductFolderName,
            StoreFolderName);

    public static ChangeFeedStoreLayout ForOwner(string ownerSid) =>
        ForOwner(DefaultRoot, ownerSid);

    public static IReadOnlyList<string> EnumerateOwners(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        if (!Directory.Exists(storeRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(storeRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    public static ChangeFeedStoreLayout ForOwner(string storeRoot, string ownerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);

        if (ownerSid.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"Sahip kimliği dizin adı olarak kullanılamaz: {ownerSid}",
                nameof(ownerSid));
        }

        return new ChangeFeedStoreLayout(ownerSid, Path.Combine(storeRoot, ownerSid));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(OwnerDirectory);
        Directory.CreateDirectory(QueueDirectory);
        Directory.CreateDirectory(StateDirectory);
    }
}
