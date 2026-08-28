using System.ComponentModel;
using SmartFileLauncher.Core.IO;

namespace SmartFileLauncher.Core.Application.Files;

public sealed class RootScopedFileOperationService : IFileOperationService
{
    private readonly IFileOperationService _inner;
    private readonly FileSystemPathGuard _pathGuard;
    private readonly string _rootPath;
    private readonly string _rootPrefix;

    public RootScopedFileOperationService(
        IFileOperationService inner,
        string rootPath)
        : this(inner, rootPath, FileSystemPathGuard.Default)
    {
    }

    internal RootScopedFileOperationService(
        IFileOperationService inner,
        string rootPath,
        FileSystemPathGuard pathGuard)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Kök yolu boş olamaz.", nameof(rootPath));

        var canonicalRoot = _pathGuard.Canonicalize(rootPath);
        RejectReparsePoint(canonicalRoot);
        _rootPath = _pathGuard.ResolvePhysicalPath(canonicalRoot);
        RejectReparsePoint(_rootPath);
        _rootPrefix = _rootPath + Path.DirectorySeparatorChar;
    }

    public FileItemKind GetItemKind(string path)
    {
        return IsWithinRoot(path)
            ? _inner.GetItemKind(path)
            : FileItemKind.Missing;
    }

    public void OpenFile(string path)
    {
        _inner.OpenFile(RequireWithinRoot(path));
    }

    public PasteOperationResult Paste(
        string sourcePath,
        string targetFolder,
        bool move)
    {
        var safeSource = RequireWithinRoot(sourcePath);
        if (_pathGuard.FindReparsePointInTree(safeSource) != null)
        {
            throw CreateBoundaryException();
        }

        return _inner.Paste(
            safeSource,
            RequireWithinRoot(targetFolder),
            move);
    }

    public RenameOperationResult Rename(string path, string newName)
    {
        var safePath = RequireWithinRoot(path);
        var parent = Path.GetDirectoryName(safePath) ?? _rootPath;
        RequireWithinRoot(Path.Combine(parent, newName));
        return _inner.Rename(safePath, newName);
    }

    public FileItemKind DeleteToRecycleBin(string path)
    {
        return _inner.DeleteToRecycleBin(RequireWithinRoot(path));
    }

    public string CreateFolder(string targetFolder, string name)
    {
        var safeTarget = RequireWithinRoot(targetFolder);
        RequireWithinRoot(Path.Combine(safeTarget, name));
        return _inner.CreateFolder(safeTarget, name);
    }

    public string CreateTextFile(string targetFolder, string name)
    {
        var safeTarget = RequireWithinRoot(targetFolder);
        RequireWithinRoot(Path.Combine(safeTarget, name));
        return _inner.CreateTextFile(safeTarget, name);
    }

    public void OpenWith(string path)
    {
        _inner.OpenWith(RequireWithinRoot(path));
    }

    public void Reveal(string path)
    {
        _inner.Reveal(RequireWithinRoot(path));
    }

    public void ShowProperties(string path)
    {
        _inner.ShowProperties(RequireWithinRoot(path));
    }

    private string RequireWithinRoot(string path)
    {
        if (!TryResolveSafePath(path, out var canonicalPath) ||
            !IsCanonicalPathWithinRoot(canonicalPath))
        {
            throw CreateBoundaryException();
        }

        return canonicalPath;
    }

    private bool IsWithinRoot(string path)
    {
        return TryResolveSafePath(path, out var canonicalPath) &&
               IsCanonicalPathWithinRoot(canonicalPath);
    }

    private bool IsCanonicalPathWithinRoot(string canonicalPath)
    {
        return canonicalPath.Equals(_rootPath, StringComparison.OrdinalIgnoreCase) ||
               canonicalPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveSafePath(string path, out string canonicalPath)
    {
        try
        {
            var lexicalPath = _pathGuard.Canonicalize(path);
            if (_pathGuard.FindReparsePointInExistingPath(lexicalPath) != null)
            {
                canonicalPath = string.Empty;
                return false;
            }

            canonicalPath = _pathGuard.ResolvePhysicalPath(lexicalPath);
            if (_pathGuard.FindReparsePointInExistingPath(canonicalPath) != null)
            {
                canonicalPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException or Win32Exception)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private void RejectReparsePoint(string path)
    {
        if (_pathGuard.FindReparsePointInExistingPath(path) != null)
        {
            throw CreateBoundaryException();
        }
    }

    private static UnauthorizedAccessException CreateBoundaryException()
    {
        return new UnauthorizedAccessException(
            "Dosya işlemi ölçüm corpus'u dışına veya yeniden yönlendirilmiş bir yola çıkamaz.");
    }
}
