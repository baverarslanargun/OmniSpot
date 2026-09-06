using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedWireEncodingTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string Root = @"C:\Çalışma Belgeleri";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation = ChangeFeedRootGeneration.New();

    [Fact]
    public async Task TheRealChannel_WritesTurkishTextWithoutEscapingIt()
    {
        var response = ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
            new ChangeFeedDeliveryPage(
                new[]
                {
                    new ChangeFeedRootPage(
                        Root,
                        new[]
                        {
                            new ChangeFeedEvent(
                                ChangeFeedEventKind.Renamed,
                                Root + @"\yeni ağırlıklı çözüm.txt",
                                false,
                                Root + @"\eski ölçüm şeması.txt")
                        },
                        ChangeFeedGapReason.None,
                        ChangeFeedFaultReason.None,
                        false,
                        false)
                },
                1,
                false),
            null,
            new string('0', ChangeFeedDeliveryLedger.TokenLength)));

        using var stream = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, response, CancellationToken.None);

        var written = stream.ToArray()[ChangeFeedProtocol.LengthPrefixBytes..];
        var text = Encoding.UTF8.GetString(written);

        Assert.Contains("ağırlıklı çözüm", text, StringComparison.Ordinal);
        Assert.Contains("ölçüm şeması", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u", text, StringComparison.Ordinal);

        var escaped = JsonSerializer.SerializeToUtf8Bytes(
            response,
            new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = JavaScriptEncoder.Default,
                Converters = { new JsonStringEnumConverter() }
            });

        Assert.Contains(@"\u", Encoding.UTF8.GetString(escaped), StringComparison.Ordinal);
        Assert.True(
            written.Length < escaped.Length,
            $"Gevşek encoder kazanç sağlamadı: {written.Length} >= {escaped.Length}");
    }

    [Fact]
    public async Task AFullBoundedRead_FitsOneRealResponse()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);

        store.WriteSubscription(new ChangeFeedSubscription(
            OwnerSid,
            new[]
            {
                new ChangeFeedSubscribedRoot(
                    Root,
                    new ChangeFeedRootIdentity("vol-1", "node-1"),
                    Generation)
            }));

        var usn = 0L;
        while (!store.ReadPending().HasMore)
        {
            store.Enqueue(VolumeId, JournalId, usn, usn + 10, new[] { Delivery(usn) });
            usn += 10;

            if (usn > 100_000)
            {
                throw new InvalidOperationException("Okuma bütçesi doldurulamadı.");
            }
        }

        var session = new ChangeFeedPullSession(
            store,
            new ChangeFeedDeliveryLedger(),
            (subscription, slice, start, cancellationToken) => new ChangeFeedDeliveryProjector(
                rootPath => new ChangeFeedPathAuthorizer(rootPath, _ => true),
                new ChangeFeedWireMeasure(),
                ChangeFeedProtocol.MaximumResponseBytes)
                .Walk(subscription, slice, start, cancellationToken));

        var pull = session.Pull(OwnerSid, null);

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, pull.Status);
        Assert.Null(pull.Continuation);

        using var stream = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(
            stream,
            ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
                pull.Page!,
                pull.Continuation,
                pull.Receipt)),
            CancellationToken.None);

        Assert.True(
            stream.Length - ChangeFeedProtocol.LengthPrefixBytes
                <= ChangeFeedProtocol.MaximumResponseBytes,
            $"Dolu bir okuma tek yanıta sığmadı: {stream.Length}");
    }

    private static ChangeFeedRootDelivery Delivery(long usn) =>
        new(
            Root,
            ChangeFeedBatch.Ok(Enumerable
                .Range(0, 200)
                .Select(index => new ChangeFeedEvent(
                    ChangeFeedEventKind.Renamed,
                    Root + @"\yeni " + new string('ö', 60) + usn + "-" + index + ".txt",
                    false,
                    Root + @"\eski " + new string('ş', 60) + usn + "-" + index + ".txt"))
                .ToArray()),
            Generation);
}
