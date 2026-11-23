using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;

namespace SmartFileLauncher.UI.Services;

public sealed class FileOperationService : IFileOperationService
{
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly Action<string>? _recordOpened;

    public FileOperationService(IIndexLifecycleService indexLifecycle, Action<string>? recordOpened = null)
    {
        _indexLifecycle = indexLifecycle ??
            throw new ArgumentNullException(nameof(indexLifecycle));
        _recordOpened = recordOpened;
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
        _recordOpened?.Invoke(path);
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
        if (IsVolumeRoot(path)) throw new IOException("Sürücü kökü yeniden adlandırılamaz.");
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

    internal static bool IsVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                string.Equals(path.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public FileItemKind DeleteToRecycleBin(string path)
    {
        if (IsVolumeRoot(path)) throw new IOException("Sürücü kökü silinemez.");
        var kind = GetItemKind(path);
        if (kind == FileItemKind.Missing) return kind;

        var operation = new ShellFileOperation
        {
            Window = OwnerWindow(),
            Function = DeleteFunction,
            From = path + '\0',
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi | WantNukeWarning
        };
        var result = SHFileOperation(ref operation);
        if (operation.AnyOperationsAborted)
            throw new OperationCanceledException("Kalıcı silme iptal edildi.");
        if (result != 0)
            throw new IOException($"“{Path.GetFileName(path)}” silinemedi (Windows kodu 0x{result:X}).");
        return kind;
    }

    private static IntPtr OwnerWindow()
    {
        var application = System.Windows.Application.Current;
        return application?.Dispatcher.CheckAccess() == true && application.MainWindow is { } window
            ? new WindowInteropHelper(window).Handle
            : IntPtr.Zero;
    }

    private const uint DeleteFunction = 0x0003;
    private const ushort Silent = 0x0004;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;
    private const ushort WantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

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
