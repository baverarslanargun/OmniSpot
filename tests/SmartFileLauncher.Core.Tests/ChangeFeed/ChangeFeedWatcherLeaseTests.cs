using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedWatcherLeaseTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ALeaseIsHeldUntilItExpires()
    {
        var lease = ChangeFeedWatcherLease.Until(Now, TimeSpan.FromMinutes(1));

        Assert.True(lease.IsHeld(Now));
        Assert.True(lease.IsHeld(Now.AddSeconds(59)));
        Assert.False(lease.IsHeld(Now.AddMinutes(1)));
        Assert.False(lease.IsHeld(Now.AddMinutes(5)));
    }

    [Fact]
    public void AnAbsentLeaseIsNeverHeld()
    {
        Assert.False(ChangeFeedWatcherLease.None.IsHeld(Now));
        Assert.False(new ChangeFeedWatcherLease(-1).IsHeld(Now));
    }

    [Fact]
    public void ALeaseReachingFurtherAheadThanTheMaximumIsNotHeld()
    {
        var forged = new ChangeFeedWatcherLease(
            Now.Add(ChangeFeedWatcherLease.MaximumDuration).Ticks + 1);

        Assert.False(
            forged.IsHeld(Now),
            "Saat geriye atlarsa veya değer kurcalanırsa kira sınırsız tutulmamalı.");
    }

    [Fact]
    public void ADurationBeyondTheMaximumIsCapped()
    {
        var lease = ChangeFeedWatcherLease.Until(Now, TimeSpan.FromDays(1));

        Assert.Equal(
            Now.Add(ChangeFeedWatcherLease.MaximumDuration).Ticks,
            lease.ExpiresAtUtcTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveDurationProducesNoLease(int seconds)
    {
        Assert.Equal(
            ChangeFeedWatcherLease.None,
            ChangeFeedWatcherLease.Until(Now, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void APersistedLeaseRoundTrips()
    {
        var lease = ChangeFeedWatcherLease.Until(Now, TimeSpan.FromMinutes(2));

        Assert.Equal(
            lease,
            ChangeFeedWatcherLease.FromPersistedValue(lease.ToPersistedValue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bozuk")]
    [InlineData("-5")]
    public void AnUnreadablePersistedValueBecomesNoLease(string? value)
    {
        Assert.Equal(
            ChangeFeedWatcherLease.None,
            ChangeFeedWatcherLease.FromPersistedValue(value));
    }

    [Fact]
    public void TheStoreKeepsALeaseAcrossInstances()
    {
        using var directory = new TemporaryDirectory();

        var held = CreateStore(directory).HoldLease(TimeSpan.FromMinutes(5));
        var restored = CreateStore(directory).ReadLease();

        Assert.Equal(held, restored);
        Assert.True(restored.IsHeld(DateTime.UtcNow));
    }

    [Fact]
    public void TheStoreReportsNoLeaseBeforeOneIsTaken()
    {
        using var directory = new TemporaryDirectory();

        Assert.Equal(ChangeFeedWatcherLease.None, CreateStore(directory).ReadLease());
    }

    [Fact]
    public void ReleasingALeaseClearsIt()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.HoldLease(TimeSpan.FromMinutes(5));

        store.ReleaseLease();

        Assert.Equal(ChangeFeedWatcherLease.None, CreateStore(directory).ReadLease());
    }

    [Fact]
    public void ReleasingALeaseThatWasNeverTakenIsHarmless()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        store.ReleaseLease();

        Assert.Equal(ChangeFeedWatcherLease.None, store.ReadLease());
    }

    [Fact]
    public void TheStoreRefusesANonPositiveLease()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.HoldLease(TimeSpan.Zero));
        Assert.Equal(ChangeFeedWatcherLease.None, store.ReadLease());
    }

    [Fact]
    public void TheLeaseLivesInsideTheOwnersOwnDirectory()
    {
        var mine = ChangeFeedStoreLayout.ForOwner(@"C:\Depo", OwnerSid);
        var other = ChangeFeedStoreLayout.ForOwner(@"C:\Depo", "S-1-5-21-99-99-99-1234");

        Assert.Equal(
            Path.Combine(mine.OwnerDirectory, ChangeFeedStoreLayout.LeaseFileName),
            mine.LeasePath);
        Assert.NotEqual(mine.LeasePath, other.LeasePath);
    }

    private static FileSystemChangeFeedStore CreateStore(TemporaryDirectory directory) =>
        new(ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid));
}
