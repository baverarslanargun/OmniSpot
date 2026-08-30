using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>One parsed <c>USN_RECORD_V2</c> or <c>USN_RECORD_V3</c> entry.</summary>
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
