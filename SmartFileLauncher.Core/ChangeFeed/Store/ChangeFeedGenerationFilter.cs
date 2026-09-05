namespace SmartFileLauncher.Core.ChangeFeed.Store;

public static class ChangeFeedGenerationFilter
{
    public static IReadOnlyList<ChangeFeedRootDelivery> Current(
        ChangeFeedSubscription? subscription,
        ChangeFeedQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (subscription is null)
        {
            return Array.Empty<ChangeFeedRootDelivery>();
        }

        return entry.Roots.Where(delivery => IsCurrent(subscription, delivery)).ToArray();
    }

    public static bool IsCurrent(
        ChangeFeedSubscription subscription,
        ChangeFeedRootDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);

        var root = subscription.Roots.FirstOrDefault(candidate => string.Equals(
            candidate.RootPath,
            delivery.RootPath,
            StringComparison.OrdinalIgnoreCase));

        return root is not null && root.Generation.Matches(delivery.Generation);
    }
}
