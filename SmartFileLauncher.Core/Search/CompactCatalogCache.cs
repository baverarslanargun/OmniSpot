using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmartFileLauncher.Core.Search;

internal static class CompactCatalogCache
{
    internal sealed record Loaded(CompactSearchState State, string? SingleRootPath, int DetachedCount);
    private sealed record Manifest(int Version, string Contract, string DatabaseHash,
        string CatalogHash, string BaseId, string[] Roots, string? SingleRoot, int DetachedCount,
        int ItemCount, int TokenCount, byte[] Delta);

    internal static bool TryLoad(string databasePath, IReadOnlyList<string> roots,
        ITokenizer tokenizer, CancellationToken cancellationToken, out Loaded? loaded)
    {
        loaded = null;
        if (Contract(tokenizer) is not { } contract) return false;
        try
        {
            var metadataPath = databasePath + ".catalog.meta";
            if (!File.Exists(metadataPath) || HasWal(databasePath)) return false;
            var info = new FileInfo(metadataPath);
            if (info.Length < 32 || info.Length > 64 * 1024 * 1024) return false;
            var bytes = File.ReadAllBytes(metadataPath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)),
                    bytes.AsSpan(bytes.Length - 32))) return false;
            var manifest = JsonSerializer.Deserialize<Manifest>(bytes.AsSpan(0, bytes.Length - 32));
            if (manifest is null || manifest.Version != 1 || manifest.Contract != contract ||
                !Guid.TryParseExact(manifest.BaseId, "N", out var baseId) ||
                manifest.DetachedCount < 0 || !manifest.Roots.Select(Decode).Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(roots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return false;
            using (var database = new FileStream(databasePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
                if (Hash(database, cancellationToken) != manifest.DatabaseHash) return false;
            if (HasWal(databasePath)) return false;
            var catalogPath = BasePath(databasePath, baseId);
            if (ReadCatalogHash(catalogPath) != manifest.CatalogHash) return false;
            cancellationToken.ThrowIfCancellationRequested();
            var state = CompactSearchState.OpenMapped(catalogPath).RestoreCheckpointDelta(manifest.Delta);
            if (state.ItemCount != manifest.ItemCount || state.TokenCount != manifest.TokenCount) return false;
            loaded = new Loaded(state, manifest.SingleRoot is null ? null : Decode(manifest.SingleRoot),
                manifest.DetachedCount);
            DeleteOlderBases(databasePath, catalogPath);
            return true;
        }
        catch (Exception error) when (IsCacheFailure(error)) { return false; }
    }

    internal static bool TrySave(string databasePath, IReadOnlyList<string> roots,
        ITokenizer tokenizer, CompactSearchState state, string? singleRootPath, int detachedCount,
        Action<Exception>? onFailure = null)
    {
        if (Contract(tokenizer) is not { } contract) return false;
        var baseId = Guid.NewGuid();
        var catalogPath = BasePath(databasePath, baseId);
        var temporaryBase = catalogPath + ".tmp";
        var temporaryMetadata = temporaryBase + ".meta";
        try
        {
            if (HasWal(databasePath)) return false;
            using var database = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var databaseHash = Hash(database, CancellationToken.None);
            state.WriteCheckpointBase(temporaryBase);
            var manifest = new Manifest(1, contract, databaseHash, ReadCatalogHash(temporaryBase), baseId.ToString("N"),
                roots.Select(Encode).ToArray(), singleRootPath is null ? null : Encode(singleRootPath),
                detachedCount, state.ItemCount, state.TokenCount, state.ExportCheckpointDelta());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            using (var stream = new FileStream(temporaryMetadata, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Write(SHA256.HashData(bytes));
                stream.Flush(flushToDisk: true);
            }
            if (HasWal(databasePath)) return false;
            File.Move(temporaryBase, catalogPath);
            File.Move(temporaryMetadata, databasePath + ".catalog.meta", overwrite: true);
            DeleteOlderBases(databasePath, catalogPath);
            return true;
        }
        catch (Exception error) when (IsCacheFailure(error)) { onFailure?.Invoke(error); return false; }
        finally
        {
            DeleteTemporary(temporaryBase);
            DeleteTemporary(temporaryMetadata);
        }
    }

    private static string? Contract(ITokenizer tokenizer) => tokenizer.GetType() == typeof(BasicTokenizer)
        ? $"{typeof(CompactSearchState).Module.ModuleVersionId:N}|{CultureInfo.CurrentCulture.Name}|{TimeZoneInfo.Local.ToSerializedString()}"
        : null;

    private static string BasePath(string databasePath, Guid id) => databasePath + ".catalog." + id.ToString("N") + ".bin";

    private static void DeleteOlderBases(string databasePath, string keepPath)
    {
        var prefix = Path.GetFileName(databasePath) + ".catalog.";
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, prefix + "*.bin"))
            {
                var name = Path.GetFileName(path);
                if (path != keepPath && Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) DeleteTemporary(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool HasWal(string databasePath) =>
        File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length != 0;

    private static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ReadCatalogHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length < 32) throw new InvalidDataException("Katalog sağlama toplamı eksik.");
        stream.Seek(-32, SeekOrigin.End);
        Span<byte> hash = stackalloc byte[32];
        stream.ReadExactly(hash);
        return Convert.ToHexString(hash);
    }

    private static string Encode(string value) => Convert.ToBase64String(MemoryMarshal.AsBytes(value.AsSpan()));
    private static string Decode(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length % 2 != 0) throw new InvalidDataException("Katalog yolu geçersiz.");
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    private static bool IsCacheFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or FormatException or OverflowException;

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
