using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Shared directory walk behind both the baseline map build and the subtree
/// learn that follows a directory moved into a root.
/// </summary>
/// <remarks>
/// <para>
/// The listing is materialised inside the guarded block so a directory that
/// cannot be opened is counted rather than thrown, whether the runtime reports
/// the failure when the enumeration is created or when it is walked.
/// </para>
/// <para>
/// The walk never leaves the volume it started on and never traverses a reparse
/// point, the start path included. A junction would otherwise pull directories
/// that live outside the root into the feed map, and a map keyed by identity
/// cannot represent a directory that is reachable through more than one path
/// anyway.
/// </para>
/// </remarks>
internal static class UsnDirectoryWalk
{
    public static string[] ListDirectories(string path) => Directory.GetDirectories(path);

    public static int Walk(
        string startPath,
        UsnFileReference startReference,
        ulong volumeSerialNumber,
        IUsnIdentityProbe identityProbe,
        Func<string, string[]> listDirectories,
        ICollection<UsnDirectoryEntry> destination,
        CancellationToken cancellationToken)
    {
        if (!CanEnterDirectory(startPath, startReference, volumeSerialNumber, identityProbe))
        {
            return 1;
        }

        var skipped = 0;
        var pending = new Stack<(string Path, UsnFileReference Reference)>();
        pending.Push((startPath, startReference));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (currentPath, currentReference) = pending.Pop();

            string[] children;
            try
            {
                children = listDirectories(currentPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var childPath in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReparsePoint(childPath) ||
                    !identityProbe.TryReadIdentity(childPath, out var childIdentity) ||
                    childIdentity.VolumeSerialNumber != volumeSerialNumber)
                {
                    skipped++;
                    continue;
                }

                destination.Add(new UsnDirectoryEntry(
                    childIdentity.FileReference,
                    Path.GetFileName(childPath),
                    currentReference));

                pending.Push((childPath, childIdentity.FileReference));
            }
        }

        return skipped;
    }

    /// <summary>
    /// Confirms that the walk may descend into <paramref name="path"/>: it must
    /// not be a reparse point, and it must still be the object the caller named.
    /// The identity check also covers the directory being moved away again
    /// between the journal record and this walk.
    /// </summary>
    private static bool CanEnterDirectory(
        string path,
        UsnFileReference reference,
        ulong volumeSerialNumber,
        IUsnIdentityProbe identityProbe) =>
        !IsReparsePoint(path) &&
        identityProbe.TryReadIdentity(path, out var identity) &&
        identity.FileReference == reference &&
        identity.VolumeSerialNumber == volumeSerialNumber;

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
