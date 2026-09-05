using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedWireContractTests
{
    [Fact]
    public void RequestLimit_StaysAtSixtyFourKibibytes()
    {
        Assert.Equal(64 * 1024, ChangeFeedProtocol.MaximumRequestBytes);
    }

    [Fact]
    public void PipeBuffers_DoNotTrackTheMessageLimits()
    {
        Assert.Equal(64 * 1024, ChangeFeedProtocol.PipeOutboundBufferBytes);
        Assert.Equal(64 * 1024, ChangeFeedProtocol.PipeInboundBufferBytes);
        Assert.True(
            ChangeFeedProtocol.PipeOutboundBufferBytes < ChangeFeedProtocol.MaximumResponseBytes,
            "Pipe tamponu yalnız bir ipucudur; mesaj sınırıyla birlikte büyürse " +
            "çekirdek tamponu her pipe örneği için gereksiz yere büyür.");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheRealPipe_IsCreatedWithTheBufferConstantsNotTheMessageLimits()
    {
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        using var pipe = ChangeFeedPipeFactory.CreateFirstInstance(pipeName);

        Assert.True(
            GetNamedPipeInfo(pipe.SafePipeHandle, out _, out var outBuffer, out var inBuffer, out _),
            $"GetNamedPipeInfo başarısız: {Marshal.GetLastWin32Error()}");

        Assert.Equal(ChangeFeedProtocol.PipeOutboundBufferBytes, (int)outBuffer);
        Assert.Equal(ChangeFeedProtocol.PipeInboundBufferBytes, (int)inBuffer);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeInfo(
        SafePipeHandle hNamedPipe,
        out uint lpFlags,
        out uint lpOutBufferSize,
        out uint lpInBufferSize,
        out uint lpMaxInstances);

    [Fact]
    public void RequestLimit_NeverGrowsWithTheResponseLimit()
    {
        Assert.True(ChangeFeedProtocol.MaximumRequestBytes <= ChangeFeedProtocol.MaximumResponseBytes);
        Assert.Equal(64 * 1024, ChangeFeedProtocol.MaximumRequestBytes);
    }

    [Fact]
    public async Task ReadRequest_RefusesALengthPrefixThatOnlyTheResponseLimitWouldAllow()
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(ChangeFeedProtocol.MaximumRequestBytes + 1));
        stream.Position = 0;

        await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public async Task Wire_CarriesTurkishPathsAsUtf8RatherThanEscapeSequences()
    {
        var path = @"C:\Projeler\Çalışmalar\Öğrenci Ödevleri";
        var payload = await SerializeAsync(
            new ChangeFeedResponse(
                ChangeFeedProtocol.Version,
                ChangeFeedResponseStatus.Ok,
                null,
                new[] { path }));

        var text = Encoding.UTF8.GetString(payload);

        Assert.Contains("Çalışmalar", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u00C7", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\u011F", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wire_KeepsTurkishPathsSubstantiallySmallerThanEscapedEncoding()
    {
        var turkish = new string('ç', 2000);
        var payload = await SerializeAsync(
            new ChangeFeedResponse(
                ChangeFeedProtocol.Version,
                ChangeFeedResponseStatus.Ok,
                null,
                new[] { turkish }));

        var escaped = 6 * turkish.Length;
        var actual = payload.Length;

        Assert.True(
            actual < escaped / 2,
            $"Türkçe yol kaçışlı kodlamaya yakın: {actual} bayt, kaçışlı yaklaşık {escaped}.");
    }

    [Fact]
    public async Task Wire_LeavesAsciiPathsUnchanged()
    {
        var ascii = new string('k', 2000);
        var payload = await SerializeAsync(
            new ChangeFeedResponse(
                ChangeFeedProtocol.Version,
                ChangeFeedResponseStatus.Ok,
                null,
                new[] { ascii }));

        Assert.InRange(payload.Length, ascii.Length, ascii.Length + 128);
    }

    [Fact]
    public async Task Response_PublishesOnlyTheAllowlistedFields()
    {
        var payload = await SerializeAsync(
            ChangeFeedResponse.Ok(new[] { @"C:\Projeler" }));

        Assert.Equal(
            new[] { "Message", "Roots", "Status", "Version" },
            FieldNames(payload));
    }

    [Fact]
    public async Task Request_PublishesOnlyTheAllowlistedFields()
    {
        var payload = await SerializeAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                @"C:\Projeler"));

        Assert.Equal(
            new[] { "Kind", "RootPath", "Version" },
            FieldNames(payload));
    }

    [Fact]
    public async Task QueueEntry_IsNotAWireShapeAndStillCarriesTheMeasuredLeak()
    {
        var entry = new ChangeFeedQueueEntry(
            7,
            @"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
            0x1D9F1E2A3B4C5D6E,
            1000,
            2000,
            new[]
            {
                new ChangeFeedRootDelivery(
                    @"C:\Projeler",
                    ChangeFeedBatch.Ok(
                        new[]
                        {
                            new ChangeFeedEvent(
                                ChangeFeedEventKind.Renamed,
                                @"C:\Projeler\Gizli\a.txt",
                                false,
                                @"C:\Projeler\Gizli\b.txt")
                        }),
                    ChangeFeedRootGeneration.New())
            });

        var fields = FieldNames(await SerializeAsync(entry));

        Assert.Contains("EventCount", fields);
        Assert.Contains("HasAnyGap", fields);
        Assert.Contains("IsPositional", fields);
        Assert.Contains("JournalId", fields);
        Assert.Contains("VolumeId", fields);
    }

    private static async Task<byte[]> SerializeAsync<T>(T message)
    {
        using var stream = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, message, CancellationToken.None);

        var frame = stream.ToArray();
        return frame[ChangeFeedProtocol.LengthPrefixBytes..];
    }

    private static string[] FieldNames(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
