using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Tests.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedInventoryTests
{
    [Fact]
    public async Task PagesAreBoundedAndApplyVisibilityBeforeEncoding()
    {
        using var sessions = new ChangeFeedInventorySessions((_, _) => Enumerable.Range(0, 1300)
            .Select(index => Entry(@"C:\root\" + index + new string('a', 600))));
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        var count = 0;
        var pages = 0;
        var completed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!completed)
        {
            var response = sessions.Page(binding, token, path => !path.Contains("\\0a"), false, deadline.Token);
            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            Assert.True(ChangeFeedMessageChannel.MeasureResponse(response) <= ChangeFeedProtocol.MaximumResponseBytes);
            var page = Assert.IsType<ChangeFeedInventoryPageDto>(response.Inventory);
            count += page.Entries.Count;
            pages++;
            token = page.Token;
            completed = page.Completed;
            if (page.Entries.Count == 0 && !completed) await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(1299, count);
        Assert.True(pages > 1);
        Assert.Equal(ChangeFeedResponseStatus.Ok, sessions.Page(binding, token, _ => true, true, default).Status);
    }

    [Fact]
    public void OwnerSecurityAndRootGenerationAreBoundToTheSession()
    {
        using var sessions = new ChangeFeedInventorySessions((_, _) => []);
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        var otherOwner = new ChangeFeedChainBinding("other", binding.ProtocolVersion, binding.Epoch,
            binding.Security, binding.Roots);
        Assert.NotEqual(ChangeFeedResponseStatus.Ok, sessions.Page(otherOwner, token, _ => true, false, default).Status);
        Assert.NotNull(sessions.Roots(binding.OwnerSid, token));
        var changed = new ChangeFeedChainBinding(binding.OwnerSid, binding.ProtocolVersion, binding.Epoch,
            ChangeFeedSecurityStamp.New(), binding.Roots);
        Assert.NotEqual(ChangeFeedResponseStatus.Ok, sessions.Page(changed, token, _ => true, false, default).Status);
        Assert.Null(sessions.Roots(binding.OwnerSid, token));
    }

    [Fact]
    public async Task CancelStopsABlockedProducerAndInvalidatesTheToken()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IEnumerable<IndexInventoryEntry> Read(CancellationToken ct)
        {
            try
            {
                while (true) { ct.ThrowIfCancellationRequested(); yield return Entry(@"C:\root\file"); }
            }
            finally { finished.TrySetResult(); }
        }
        using var sessions = new ChangeFeedInventorySessions((_, ct) => Read(ct));
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        await Task.Delay(30);
        sessions.Cancel(binding.OwnerSid, token);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(sessions.Roots(binding.OwnerSid, token));
    }

    [Fact]
    public async Task FailedProducerCannotBeValidatedAsComplete()
    {
        using var sessions = new ChangeFeedInventorySessions((_, _) => throw new IOException("private path"));
        var binding = Binding();
        var token = sessions.Start(binding, binding.Roots);
        await Task.Delay(30);
        var response = sessions.Page(binding, token, _ => true, true, default);
        Assert.NotEqual(ChangeFeedResponseStatus.Ok, response.Status);
        Assert.DoesNotContain("private path", response.Message);
    }

    [Fact]
    public async Task WireEncodingPreservesUnpairedUtf16Names()
    {
        var entry = Entry("C:\\root\\name\ud800");
        var response = new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
            Inventory: new ChangeFeedInventoryPageDto("token", [ChangeFeedInventoryEntryDto.FromEntry(entry)], true));
        using var stream = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, response, default);
        stream.Position = 0;
        var decoded = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(stream, default);
        Assert.Equal(entry, Assert.Single(decoded.Inventory!.Entries).ToEntry());
    }

    private static IndexInventoryEntry Entry(string path) => new(path, false, FileAttributes.Normal, 1, 2, 3);
    private static ChangeFeedChainBinding Binding() => new("owner", ChangeFeedProtocol.Version,
        ChangeFeedQueueEpoch.New(), ChangeFeedSecurityStamp.New(), [NtfsRootInventoryTests.Root(10, @"C:\root")]);
}
