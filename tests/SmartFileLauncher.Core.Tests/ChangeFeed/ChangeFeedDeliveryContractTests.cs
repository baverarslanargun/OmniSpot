using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedDeliveryContractTests
{
    private const string Root = @"C:\Kok";

    [Fact]
    public void TheDeliveryShape_PublishesOnlyItsAllowlistedFields()
    {
        Assert.Equal(
            new[] { "Roots", "HasMore", "Continuation", "Receipt" },
            Members(typeof(ChangeFeedDeliveryDto)));

        Assert.Equal(
            new[]
            {
                "RootPath",
                "Events",
                "ProducerGap",
                "ProducerFault",
                "AuthorizationGap",
                "PayloadTooLarge"
            },
            Members(typeof(ChangeFeedRootPageDto)));

        Assert.Equal(
            new[] { "Kind", "Path", "IsDirectory", "OldPath" },
            Members(typeof(ChangeFeedEventDto)));
    }

    [Fact]
    public void TheDeliveryBytes_CarryNoStoreModelField()
    {
        var page = new ChangeFeedDeliveryPage(
            new[]
            {
                new ChangeFeedRootPage(
                    Root,
                    new[]
                    {
                        new ChangeFeedEvent(
                            ChangeFeedEventKind.Renamed,
                            @"C:\Kok\yeni.txt",
                            false,
                            @"C:\Kok\eski.txt")
                    },
                    ChangeFeedGapReason.DeliveryQueueOverflow,
                    ChangeFeedFaultReason.NativeProtocolRejected,
                    true,
                    true)
            },
            completedThroughSequence: 41,
            hasMore: true);

        var payload = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            ChangeFeedResponse.Delivered(
                ChangeFeedDeliveryContract.ToWire(page, "devam", "makbuz"))));

        foreach (var forbidden in new[]
                 {
                     "VolumeId",
                     "JournalId",
                     "Diagnostics",
                     "IsPositional",
                     "HasGap",
                     "IsFaulted",
                     "HasAnyGap",
                     "EventCount",
                     "FromUsn",
                     "ToUsn",
                     "Sequence",
                     "CompletedThrough",
                     "FullPath"
                 })
        {
            Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("41", "41");
        Assert.DoesNotContain("41", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWireMeasure_LeavesRoomForRealContentInsideTheResponseLimit()
    {
        var measure = new ChangeFeedWireMeasure();

        Assert.True(
            measure.Envelope > 0,
            "Zarf ücretsiz olamaz.");
        Assert.True(
            measure.Envelope < ChangeFeedProtocol.MaximumResponseBytes / 100,
            $"Zarf yanıt sınırının yüzde birinden büyük: {measure.Envelope}");
        Assert.True(measure.Root(Root) > 0);
        Assert.True(
            measure.Event(new ChangeFeedEvent(ChangeFeedEventKind.Created, @"C:\Kok\a.txt", false))
                > 0);
    }

    [Fact]
    public void TheWireMeasure_NeverUnderstatesTheSerializedResponse()
    {
        var measure = new ChangeFeedWireMeasure();

        var events = Enumerable
            .Range(0, 500)
            .Select(index => new ChangeFeedEvent(
                ChangeFeedEventKind.Renamed,
                @"C:\Kok\" + new string('u', 80) + index + ".txt",
                false,
                @"C:\Kok\" + new string('e', 80) + index + ".txt"))
            .ToArray();

        var budgeted = measure.Envelope
            + measure.Root(Root)
            + events.Sum(change => measure.Event(change));

        var page = new ChangeFeedDeliveryPage(
            new[]
            {
                new ChangeFeedRootPage(
                    Root,
                    events,
                    ChangeFeedGapReason.None,
                    ChangeFeedFaultReason.None,
                    false,
                    false)
            },
            completedThroughSequence: 1,
            hasMore: false);

        var actual = ChangeFeedMessageChannel.MeasureResponse(
            ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
                page,
                new string('0', 64),
                new string('0', 64))));

        Assert.True(
            budgeted >= actual,
            $"Ölçü gerçek yanıtı olduğundan küçük gösterdi: {budgeted} < {actual}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void TheWireMeasure_NeverUnderstatesAManyRootedPage(int roots)
    {
        var measure = new ChangeFeedWireMeasure();

        Assert.Equal(
            "DeliveryQueueOverflow",
            Enum.GetNames<ChangeFeedGapReason>().OrderByDescending(name => name.Length).First());
        Assert.Equal(
            "NativeProtocolRejected",
            Enum.GetNames<ChangeFeedFaultReason>().OrderByDescending(name => name.Length).First());

        var pages = Enumerable
            .Range(0, roots)
            .Select(index => new ChangeFeedRootPage(
                @"C:\Kok" + new string('k', 40) + index,
                Array.Empty<ChangeFeedEvent>(),
                ChangeFeedGapReason.DeliveryQueueOverflow,
                ChangeFeedFaultReason.NativeProtocolRejected,
                false,
                false))
            .ToArray();

        var budgeted = measure.Envelope + pages.Sum(page => measure.Root(page.RootPath));

        var actual = ChangeFeedMessageChannel.MeasureResponse(
            ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
                new ChangeFeedDeliveryPage(pages, 1, true),
                null,
                new string('0', 64))));

        Assert.True(
            budgeted >= actual,
            $"{roots} kökte ölçü gerçek yanıttan küçük: {budgeted} < {actual}");
    }

    [Fact]
    public void APageBuiltToTheMeasure_FitsTheRealResponseLimit()
    {
        var measure = new ChangeFeedWireMeasure();
        var remaining = ChangeFeedProtocol.MaximumResponseBytes - measure.Envelope;
        var root = @"C:\Kok" + new string('k', 40);
        remaining -= measure.Root(root);

        var events = new List<ChangeFeedEvent>();
        var index = 0;

        while (true)
        {
            var change = new ChangeFeedEvent(
                ChangeFeedEventKind.Renamed,
                root + @"\" + new string('u', 60) + index + ".txt",
                false,
                root + @"\" + new string('e', 60) + index + ".txt");

            var cost = measure.Event(change);
            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            events.Add(change);
            index++;
        }

        var actual = ChangeFeedMessageChannel.MeasureResponse(
            ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
                new ChangeFeedDeliveryPage(
                    new[]
                    {
                        new ChangeFeedRootPage(
                            root,
                            events,
                            ChangeFeedGapReason.None,
                            ChangeFeedFaultReason.None,
                            false,
                            false)
                    },
                    1,
                    true),
                null,
                new string('0', 64))));

        Assert.True(events.Count > 1000, $"Kurulum sınıra yaklaşmadı: {events.Count}");
        Assert.True(
            actual <= ChangeFeedProtocol.MaximumResponseBytes,
            $"Bütçeye göre kurulmuş sayfa yanıt sınırını aştı: {actual}");
    }

    private static string[] Members(Type type) =>
        type.GetProperties().Select(property => property.Name).ToArray();
}
