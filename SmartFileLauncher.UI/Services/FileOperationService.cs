using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;

namespace SmartFileLauncher.UI.Services;

public sealed class FileOperationService : IFileOperationService
{
    private readonly IIndexLifecycleService _indexLifecycle;

    public FileOperationService(IIndexLifecycleService indexLifecycle)
    {
        _indexLifecycle = indexLifecycle ??
            throw new ArgumentNullException(nameof(indexLifecycle));
    }

    public FileItemKind GetItemKind(string path)
    {
        if (Directory.Exists(path))
        {
            return FileItemKind.Directory;
        }

        return File.Exists(path)
            ? FileItemKind.File
            : FileItemKind.Missing;
    }

    public void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
        _indexLifecycle.RecordOpened(path);
    }

    public PasteOperationResult Paste(
        string sourcePath,
        string targetFolder,
        bool move)
    {
        var sourceKind = GetItemKind(sourcePath);
        var destinationPath = GetUniquePath(
            Path.Combine(targetFolder, Path.GetFileName(sourcePath)));

        if (sourceKind == FileItemKind.Directory)
        {
            if (move)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                CopyDirectory(sourcePath, destinationPath);
            }
        }
        else if (sourceKind == FileItemKind.File)
        {
            if (move)
            {
                File.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Copy(sourcePath, destinationPath);
            }
        }

        return new PasteOperationResult(destinationPath, sourceKind);
    }

    public RenameOperationResult Rename(string path, string newName)
    {
        var destinationPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            newName);
        var sourceKind = GetItemKind(path);

        if (sourceKind == FileItemKind.Directory)
        {
            Directory.Move(path, destinationPath);
        }
        else if (sourceKind == FileItemKind.File)
        {
            File.Move(path, destinationPath);
        }

        return new RenameOperationResult(destinationPath, sourceKind);
    }

    public FileItemKind DeleteToRecycleBin(string path)
    {
        var kind = GetItemKind(path);
        if (kind == FileItemKind.Directory)
        {
            FileSystem.DeleteDirectory(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
        }
        else if (kind == FileItemKind.File)
        {
            FileSystem.DeleteFile(
                path,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
        }

        return kind;
    }

    public string CreateFolder(string targetFolder, string name)
    {
        var path = GetUniquePath(Path.Combine(targetFolder, name));
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateTextFile(string targetFolder, string name)
    {
        var path = GetUniquePath(Path.Combine(targetFolder, name));
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void OpenWith(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"shell32.dll,OpenAs_RunDLL {path}",
            UseShellExecute = true
        });
    }

    public void Reveal(string path)
    {
        Process.Start("explorer.exe", $"/select,{(char)34}{path}{(char)34}");
    }

    public void ShowProperties(string path)
    {
        Shell32Helper.ShowProperties(path);
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var counter = 1;
        string candidate;

        do
        {
            candidate = Path.Combine(
                directory,
                $"{nameWithoutExtension} ({counter}){extension}");
            counter++;
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(
                file,
                Path.Combine(destinationDirectory, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(
                directory,
                Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }
}
