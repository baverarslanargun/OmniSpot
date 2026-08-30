using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed record UsnDirectoryMapBuildResult(
    UsnNodeIdentity RootIdentity,
    IReadOnlyList<UsnDirectoryEntry> Directories,
    int SkippedDirectoryCount);

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
