using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SmartFileLauncher.Core.Models;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record SyntheticSearchFixture(
    IReadOnlyList<FileSystemNode> Nodes,
    SearchFixtureManifest Manifest);

internal static class SyntheticSearchFixtureGenerator
{
    private static readonly string[] FirstTokens =
    [
        "rapor", "butce", "fatura", "fotograf", "arsiv", "proje", "sunum",
        "toplanti", "Istanbul", "İzmir", "ışık", "inceleme"
    ];

    private static readonly string[] SecondTokens =
    [
        "yillik", "taslak", "final", "eski", "yeni", "kisisel", "ortak",
        "2024", "2025", "2026", "A", "B"
    ];

    private static readonly string[] Extensions =
    [
        ".pdf", ".docx", ".txt", ".xlsx", ".jpg", ".png", ".zip", ".md"
    ];

    internal static SyntheticSearchFixture Create(int itemCount, int seed)
    {
        if (itemCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        }

        var nodes = new FileSystemNode[itemCount];
        var random = new StableRandom(unchecked((uint)seed));
        for (var index = 0; index < itemCount; index++)
        {
            var directoryBucket = index / 128;
            var isDirectory = index % 20 == 0;
            var first = FirstTokens[random.Next(FirstTokens.Length)];
            var second = SecondTokens[random.Next(SecondTokens.Length)];
            var suffix = index.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
            var extension = isDirectory ? string.Empty : Extensions[random.Next(Extensions.Length)];
            var name = first + "-" + second + "-" + suffix + extension;
            var path = Path.Combine(
                @"C:\OmniSpotSynthetic",
                "d" + directoryBucket.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                name);
            nodes[index] = new FileSystemNode(name, path, isDirectory);
        }

        var fingerprint = ComputeFingerprint(nodes);
        return new SyntheticSearchFixture(
            nodes,
            new SearchFixtureManifest(
                MeasurementConstants.FixtureGeneratorVersion,
                seed,
                itemCount,
                fingerprint));
    }

    private static string ComputeFingerprint(IReadOnlyList<FileSystemNode> nodes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var node in nodes)
        {
            AppendString(hash, length, node.Name);
            AppendString(hash, length, node.FullPath);
            hash.AppendData([node.IsDirectory ? (byte)1 : (byte)0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(
        IncrementalHash hash,
        Span<byte> length,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private struct StableRandom
    {
        private uint _state;

        internal StableRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        internal int Next(int exclusiveMaximum)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (int)(_state % (uint)exclusiveMaximum);
        }
    }
}
