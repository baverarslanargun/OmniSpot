using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedInventoryIdentityTests
{
    [Fact]
    public async Task FullFileReferenceParentAndUnpairedNameSurviveTheWire()
    {
        var entry = new IndexInventoryEntry("C:\\root\\name\ud800", false, FileAttributes.Archive,
            long.MaxValue, DateTime.MaxValue.Ticks, DateTime.MaxValue.Ticks,
            new NtfsInventoryIdentity("volume-\"\n\t", ulong.MaxValue, ulong.MaxValue - 1, "name\ud800"));
        var dto = ChangeFeedInventoryEntryDto.FromEntry(entry);
        Assert.True(ChangeFeedMessageChannel.MeasureResponse(dto) <= dto.EncodedSizeUpperBound);
        using var wire = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(wire, dto, default);
        wire.Position = 0;
        var decoded = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedInventoryEntryDto>(wire, default);
        Assert.Equal(entry, decoded.ToEntry());
    }

    [Fact]
    public void ExistingPathOnlyEntriesRemainValid()
    {
        var entry = new IndexInventoryEntry(@"C:\root\old.txt", false, FileAttributes.Normal, 1, 2, 3);
        var dto = ChangeFeedInventoryEntryDto.FromEntry(entry);
        Assert.Null(dto.Identity);
        Assert.Equal(entry, dto.ToEntry());
    }
}
