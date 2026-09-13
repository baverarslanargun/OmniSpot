using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Tests.ChangeFeed;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsInventorySecurityGuardTests
{
    [Theory]
    [InlineData(5UL, false)]
    [InlineData(10UL, false)]
    [InlineData(20UL, false)]
    [InlineData(30UL, false)]
    [InlineData(999UL, true)]
    public void InventoryProtectsAncestorsAndFileIdentitiesIncludingOutsideHardlinks(ulong changed, bool valid)
    {
        var journal = new Reader();
        using var guard = new NtfsInventorySecurityGuard(_ => journal);
        NtfsMftEntry[] entries = [Directory(5, 5, "."), Directory(10, 5, "parent"),
            Directory(20, 10, "root"), new(30, 20, "inside", false, FileAttributes.Normal, 1, 1, 2),
            new(30, 5, "outside-hardlink", false, FileAttributes.Normal, 1, 1, 2)];
        var inventory = new NtfsRootInventory((_, _) => entries);
        Assert.Equal(2, inventory.Read([NtfsRootInventoryTests.Root(20, @"C:\parent\root")], default, guard).Count());
        journal.SetChange(changed, UsnReason.SecurityChange);
        Assert.Equal(valid, guard.Validate(default));
    }

    [Fact]
    public void AncestorReparseChangesInvalidateTheInventory()
    {
        var reader = new Reader();
        using var guard = new NtfsInventorySecurityGuard(_ => reader);
        guard.BeginVolume(@"C:\");
        guard.Include(@"C:\", 5);
        reader.SetChange(5, UsnReason.ReparsePointChange);
        Assert.False(guard.Validate(default));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("wrap")]
    [InlineData("short")]
    [InlineData("malformed")]
    public void LostJournalContinuityCannotValidate(string failure)
    {
        var reader = new Reader();
        using var guard = new NtfsInventorySecurityGuard(_ => reader);
        guard.BeginVolume(@"C:\");
        reader.End = 200;
        if (failure == "journal") reader.JournalId++;
        if (failure == "wrap") reader.First = 101;
        if (failure == "short") reader.PageEnd = 100;
        if (failure == "malformed") reader.Records = [1];
        if (failure == "malformed") Assert.Throws<UsnRecordFormatException>(() => guard.Validate(default));
        else Assert.False(guard.Validate(default));
    }

    [Fact]
    public async Task NativeSessionUsesItsJournalGuardAcrossQueueAndGlobalStampChanges()
    {
        var reader = new Reader();
        using var sessions = new ChangeFeedInventorySessions((_, _, guard) =>
        {
            guard!.BeginVolume(@"C:\");
            guard.Include(@"C:\", 30);
            return [new IndexInventoryEntry(@"C:\root\file", false, FileAttributes.Normal, 1, 1, 2)];
        }, () => new NtfsInventorySecurityGuard(_ => reader));
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = sessions.Page(binding, token, _ => true, false, deadline.Token);
            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            token = response.Inventory!.Token;
            if (response.Inventory.Completed) break;
            await Task.Delay(10, deadline.Token);
        }
        Assert.False(reader.Disposed.Task.IsCompleted);
        var changedBinding = new ChangeFeedChainBinding(binding.OwnerSid, binding.ProtocolVersion,
            ChangeFeedQueueEpoch.New(), ChangeFeedSecurityStamp.New(), binding.Roots);
        reader.SetChange(999, UsnReason.SecurityChange);
        Assert.Equal(ChangeFeedResponseStatus.Ok, sessions.Page(changedBinding, token, _ => true, true, default).Status);
        reader.SetChange(30, UsnReason.SecurityChange);
        Assert.NotEqual(ChangeFeedResponseStatus.Ok, sessions.Page(changedBinding, token, _ => true, true, default).Status);
        sessions.Cancel(binding.OwnerSid, token);
        await reader.Disposed.Task.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task CancelKeepsJournalAliveUntilTheBlockedProducerExits()
    {
        var reader = new Reader();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var sessions = new ChangeFeedInventorySessions((_, ct, guard) =>
        {
            guard!.BeginVolume(@"C:\");
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            ct.ThrowIfCancellationRequested();
            return [];
        }, () => new NtfsInventorySecurityGuard(_ => reader));
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            sessions.Cancel(binding.OwnerSid, token);
            Assert.False(reader.Disposed.Task.IsCompleted);
        }
        finally { release.Set(); }
        await reader.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ChangeFeedChainBinding Binding() => new("owner", ChangeFeedProtocol.Version,
        ChangeFeedQueueEpoch.New(), ChangeFeedSecurityStamp.New(), [NtfsRootInventoryTests.Root(20, @"C:\root")]);

    private static NtfsMftEntry Directory(ulong reference, ulong parent, string name) =>
        new(reference, parent, name, true, FileAttributes.Directory, 0, 1, 2);

    private sealed class Reader : IUsnJournalReader
    {
        public ulong JournalId { get; set; } = 7;
        public long End { get; set; } = 100;
        public long First { get; set; }
        public long? PageEnd { get; set; }
        public byte[] Records { get; set; } = [];
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UsnJournalDescriptor QueryJournal() => new(JournalId, First, End, First, long.MaxValue, 0, 0);
        public UsnReadPage ReadPage(long startUsn, ulong journalId) => new(PageEnd ?? End, Records);
        public void SetChange(ulong reference, UsnReason reason)
        {
            End = 200;
            Records = new UsnRecordBuffer().AddVersion2(150, reference, 999, reason, "name").Build();
        }
        public void Dispose() => Disposed.TrySetResult();
    }
}
