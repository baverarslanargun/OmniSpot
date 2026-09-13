using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record ChangeFeedEventDto(
    ChangeFeedEventKind Kind,
    string Path,
    bool IsDirectory,
    string? OldPath);

public sealed record ChangeFeedRootPageDto(
    string RootPath,
    IReadOnlyList<ChangeFeedEventDto> Events,
    ChangeFeedGapReason ProducerGap,
    ChangeFeedFaultReason ProducerFault,
    bool AuthorizationGap,
    bool PayloadTooLarge,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AuthorizationScopesUtf16 = null);

public sealed record ChangeFeedDeliveryDto(
    IReadOnlyList<ChangeFeedRootPageDto> Roots,
    bool HasMore,
    string? Continuation,
    string? Receipt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StableBatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long CompletedThroughSequence = 0);

public static class ChangeFeedDeliveryContract
{
    public static ChangeFeedDeliveryDto ToWire(
        ChangeFeedDeliveryPage page,
        string? continuation,
        string? receipt)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ChangeFeedDeliveryDto(
            page.Roots.Select(ToWire).ToArray(),
            page.HasMore,
            continuation,
            receipt, page.StableBatchId, page.CompletedThroughSequence);
    }

    public static ChangeFeedRootPageDto ToWire(ChangeFeedRootPage root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new ChangeFeedRootPageDto(
            root.RootPath,
            root.Events.Select(ToWire).ToArray(),
            root.ProducerGap,
            root.ProducerFault,
            root.AuthorizationGap,
            root.PayloadTooLarge,
            root.AuthorizationScopes?.Select(EncodeScope).ToArray());
    }

    internal static string EncodeScope(string path) =>
        Convert.ToBase64String(MemoryMarshal.AsBytes(path.AsSpan()));

    internal static string DecodeScope(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new InvalidDataException("Uzlaştırma kapsamı geçersiz.");
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    public static ChangeFeedEventDto ToWire(ChangeFeedEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new ChangeFeedEventDto(
            change.Kind,
            change.FullPath,
            change.IsDirectory,
            change.OldPath);
    }
}
