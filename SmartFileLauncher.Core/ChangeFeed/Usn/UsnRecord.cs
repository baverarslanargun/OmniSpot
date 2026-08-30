using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed record UsnRecord(
    long Usn,
    UsnFileReference FileReference,
    UsnFileReference ParentFileReference,
    UsnReason Reason,
    FileAttributes Attributes,
    string Name)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}
