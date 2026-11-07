using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

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

                if (IsNameSurrogate(childPath) ||
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

    private static bool CanEnterDirectory(
        string path,
        UsnFileReference reference,
        ulong volumeSerialNumber,
        IUsnIdentityProbe identityProbe) =>
        !IsNameSurrogate(path) &&
        identityProbe.TryReadIdentity(path, out var identity) &&
        identity.FileReference == reference &&
        identity.VolumeSerialNumber == volumeSerialNumber;

    internal static bool IsNameSurrogate(string path)
    {
        try { return IsReparsePoint(path) && Indexing.Ntfs.NtfsDirectoryReader.IsNameSurrogate(path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return true; }
    }

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
