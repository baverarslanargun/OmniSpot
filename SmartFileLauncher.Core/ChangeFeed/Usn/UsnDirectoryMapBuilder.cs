using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Result of walking a root to build its directory map.
/// </summary>
/// <remarks>
/// <see cref="SkippedDirectoryCount"/> counts directories the walk saw but could
/// not take into the map: unreadable listings, missing identities, cross-volume
/// entries and reparse points. Children hidden by the platform listing itself
/// are not visible to the walk and are therefore not counted; they are equally
/// invisible to the index scan that walks the same tree.
/// </remarks>
public sealed record UsnDirectoryMapBuildResult(
    UsnNodeIdentity RootIdentity,
    IReadOnlyList<UsnDirectoryEntry> Directories,
    int SkippedDirectoryCount);

/// <summary>
/// Builds the directory identity map a <see cref="UsnChangeFeed"/> needs to turn
/// journal records back into paths.
/// </summary>
/// <remarks>
/// Only directories are visited; files carry their own name and parent identity
/// in every journal record. Reparse points are not traversed, and a root that is
/// itself a reparse point is rejected because its journal records would appear
/// under the target path rather than under the root path.
/// </remarks>
public static class UsnDirectoryMapBuilder
{
    public static UsnDirectoryMapBuildResult Build(
        string rootPath,
        IUsnIdentityProbe identityProbe,
        CancellationToken cancellationToken = default) =>
        Build(rootPath, identityProbe, UsnDirectoryWalk.ListDirectories, cancellationToken);

    internal static UsnDirectoryMapBuildResult Build(
        string rootPath,
        IUsnIdentityProbe identityProbe,
        Func<string, string[]> listDirectories,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Kök yolu boş olamaz.", nameof(rootPath));
        }

        ArgumentNullException.ThrowIfNull(identityProbe);
        ArgumentNullException.ThrowIfNull(listDirectories);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Kök dizin bulunamadı: {normalizedRoot}");
        }

        if (UsnDirectoryWalk.IsReparsePoint(normalizedRoot))
        {
            throw new NotSupportedException(
                $"Yeniden ayrıştırma noktası olan kök USN akışıyla izlenemez: {normalizedRoot}");
        }

        if (!identityProbe.TryReadIdentity(normalizedRoot, out var rootIdentity))
        {
            throw new IOException($"Kök kimliği okunamadı: {normalizedRoot}");
        }

        var directories = new List<UsnDirectoryEntry>();
        var skipped = UsnDirectoryWalk.Walk(
            normalizedRoot,
            rootIdentity.FileReference,
            rootIdentity.VolumeSerialNumber,
            identityProbe,
            listDirectories,
            directories,
            cancellationToken);

        return new UsnDirectoryMapBuildResult(rootIdentity, directories, skipped);
    }
}
