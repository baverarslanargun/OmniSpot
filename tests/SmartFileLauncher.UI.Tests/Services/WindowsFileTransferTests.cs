using System.IO;
using System.Windows;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class WindowsFileTransferTests
{
    private const DragDropEffects CopyAndMove = DragDropEffects.Copy | DragDropEffects.Move;

    [Theory]
    [InlineData(@"C:\Hedef", DragDropKeyStates.None, CopyAndMove, DragDropEffects.Move)]
    [InlineData(@"D:\Hedef", DragDropKeyStates.None, CopyAndMove, DragDropEffects.Copy)]
    [InlineData(@"C:\Hedef", DragDropKeyStates.ControlKey, CopyAndMove, DragDropEffects.Copy)]
    [InlineData(@"D:\Hedef", DragDropKeyStates.ShiftKey, CopyAndMove, DragDropEffects.Move)]
    [InlineData(@"D:\Hedef", DragDropKeyStates.ShiftKey, DragDropEffects.Copy, DragDropEffects.None)]
    [InlineData(@"C:\Kaynak\Alt", DragDropKeyStates.ControlKey, CopyAndMove, DragDropEffects.None)]
    [InlineData(@"C:\Kaynak", DragDropKeyStates.None, CopyAndMove, DragDropEffects.None)]
    [InlineData(@"C:\Kaynak2", DragDropKeyStates.None, CopyAndMove, DragDropEffects.Move)]
    public void DropRespectsTargetModifiersAndSourceCapabilities(
        string destination, DragDropKeyStates keys, DragDropEffects allowed, DragDropEffects expected)
    {
        Assert.Equal(expected, WindowsFileTransfer.ChooseDropEffect(
            [@"C:\Kaynak"], destination, keys, allowed, DragDropEffects.None));
    }

    [Theory]
    [InlineData(DragDropKeyStates.None, CopyAndMove, DragDropEffects.None, DragDropEffects.None)]
    [InlineData(DragDropKeyStates.ShiftKey, CopyAndMove, DragDropEffects.None, DragDropEffects.None)]
    [InlineData(DragDropKeyStates.None, DragDropEffects.Move, DragDropEffects.None, DragDropEffects.None)]
    [InlineData(DragDropKeyStates.None, CopyAndMove, DragDropEffects.Move, DragDropEffects.None)]
    [InlineData(DragDropKeyStates.ControlKey, CopyAndMove, DragDropEffects.None, DragDropEffects.Copy)]
    [InlineData(DragDropKeyStates.None, CopyAndMove, DragDropEffects.Copy, DragDropEffects.Copy)]
    public void SameFolderRejectsMovesWhileAllowingExplicitCopies(
        DragDropKeyStates keys, DragDropEffects allowed, DragDropEffects preferred, DragDropEffects expected)
    {
        Assert.Equal(expected, WindowsFileTransfer.ChooseDropEffect(
            [@"C:\Kaynak\dosya.txt"], @"c:\kaynak\", keys, allowed, preferred));
    }

    [Fact]
    public Task FileDataContainsNativeMovePreferenceAndMultiplePaths() => RunSta(() =>
    {
        string[] paths = [@"C:\Kaynak\çağrı.txt", @"D:\Belgeler"];
        var data = WindowsFileTransfer.CreateData(paths, DragDropEffects.Move);

        Assert.Equal(paths, WindowsFileTransfer.ReadPaths(data));
        Assert.Equal(DragDropEffects.Move, WindowsFileTransfer.ReadPreferredEffect(data));
        Assert.Equal(DragDropEffects.None,
            WindowsFileTransfer.ReadPreferredEffect(WindowsFileTransfer.CreateData(paths)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellTransfersAFileAndAFolder(bool move) => RunSta(() =>
    {
        var root = Directory.CreateTempSubdirectory("OmniSpot-FileTransfer-");
        try
        {
            var sourceFolder = root.CreateSubdirectory("Kaynak");
            var destination = root.CreateSubdirectory("Hedef");
            var file = Path.Combine(sourceFolder.FullName, "çağrı.txt");
            var folder = sourceFolder.CreateSubdirectory("Alt klasör");
            File.WriteAllText(file, "dosya");
            File.WriteAllText(Path.Combine(folder.FullName, "içerik.txt"), "klasör");

            Assert.True(WindowsFileTransfer.Transfer([file, folder.FullName], destination.FullName, move, IntPtr.Zero));
            Assert.Equal("dosya", File.ReadAllText(Path.Combine(destination.FullName, "çağrı.txt")));
            Assert.Equal("klasör", File.ReadAllText(Path.Combine(destination.FullName, folder.Name, "içerik.txt")));
            Assert.Equal(!move, File.Exists(file));
            Assert.Equal(!move, Directory.Exists(folder.FullName));
        }
        finally { root.Delete(recursive: true); }
    });

    [Fact]
    public Task AStaleSourceDoesNotMoveOtherItemsInTheBatch() => RunSta(() =>
    {
        var root = Directory.CreateTempSubdirectory("OmniSpot-FileTransfer-");
        try
        {
            var file = Path.Combine(root.FullName, "var.txt");
            var missing = Path.Combine(root.FullName, "yok.txt");
            var destination = root.CreateSubdirectory("Hedef");
            File.WriteAllText(file, "kaynak");

            Assert.Throws<FileNotFoundException>(() =>
                WindowsFileTransfer.Transfer([file, missing], destination.FullName, move: true, IntPtr.Zero));
            Assert.True(File.Exists(file));
            Assert.Empty(destination.EnumerateFileSystemInfos());
        }
        finally { root.Delete(recursive: true); }
    });

    [Fact]
    public Task SameFolderMoveIsRejectedBeforeAnyFileIsMoved() => RunSta(() =>
    {
        var root = Directory.CreateTempSubdirectory("OmniSpot-FileTransfer-");
        try
        {
            var source = root.CreateSubdirectory("Kaynak");
            var target = root.CreateSubdirectory("Hedef");
            var elsewhere = Path.Combine(source.FullName, "diger.txt");
            var alreadyHere = Path.Combine(target.FullName, "var.txt");
            File.WriteAllText(elsewhere, "başka klasör");
            File.WriteAllText(alreadyHere, "mevcut");

            Assert.Equal(DragDropEffects.None, WindowsFileTransfer.ChooseDropEffect(
                [elsewhere, alreadyHere], target.FullName, DragDropKeyStates.None, CopyAndMove, DragDropEffects.None));
            var error = Assert.Throws<IOException>(() =>
                WindowsFileTransfer.Transfer([elsewhere, alreadyHere], target.FullName, move: true, IntPtr.Zero));
            Assert.Equal("Öğe zaten hedef klasörde.", error.Message);
            Assert.Equal("başka klasör", File.ReadAllText(elsewhere));
            Assert.Equal("mevcut", File.ReadAllText(alreadyHere));
            Assert.Single(target.EnumerateFiles());
        }
        finally { root.Delete(recursive: true); }
    });

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
