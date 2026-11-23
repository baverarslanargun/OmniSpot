using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowDeletionTests
{
    private static readonly string Folder = Path.Combine(@"C:\Test", "Belgeler");

    [Fact]
    public Task DeletingMovesToTheBinWithoutAskingAndUndoRestoresIt() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var deleted = new List<string>();
        fixture.Files = (name, args) => name == "DeleteToRecycleBin" ? Delete(deleted, args) : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        var icon = fixture.Model.DesktopIcons.Single(item => item.Name == "not.txt");
        var requested = new TaskCompletionSource<(IReadOnlyList<string> Paths, DateTime After)>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Window.RestoreFromRecycleBin = (paths, after) =>
        {
            requested.TrySetResult((paths, after));
            return new RecycleRestoreResult(paths, [], []);
        };
        var before = DateTime.UtcNow;

        Call(fixture, "DeleteItems", (object)new[] { icon.FullPath });

        Assert.Equal([icon.FullPath], deleted);
        Assert.DoesNotContain(icon, fixture.Model.DesktopIcons);
        var toast = Toast(fixture);
        Assert.Equal(Visibility.Visible, toast.Visibility);
        Assert.Equal("“not.txt” Geri Dönüşüm Kutusu'na taşındı.", ToastText(fixture));
        Assert.Equal("Geri al", ToastAction(fixture).Content);
        Assert.Equal(Visibility.Visible, fixture.Element("ToastShortcut").Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Element("ToastErrorIcon").Visibility);

        ToastAction(fixture).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var (paths, after) = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([icon.FullPath], paths);
        Assert.InRange(after, before, DateTime.UtcNow);

        Assert.Equal("“not.txt” geri alındı.", await WaitForToast(fixture, "“not.txt” geri alındı."));
        Assert.Equal(Visibility.Collapsed, fixture.Element("ToastErrorIcon").Visibility);
        for (var attempt = 0; attempt < 300 && fixture.Model.DesktopIcons.All(item => item.Name != "not.txt"); attempt++)
            await Task.Delay(10);
        Assert.Contains(fixture.Model.DesktopIcons, item => item.Name == "not.txt");
    });

    [Fact]
    public Task SeveralItemsShareOneNoticeAndAPartialUndoIsReported() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var deleted = new List<string>();
        fixture.Files = (name, args) => name == "DeleteToRecycleBin" ? Delete(deleted, args) : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        var paths = fixture.Model.DesktopIcons.Select(item => item.FullPath).ToArray();
        fixture.Window.RestoreFromRecycleBin = (requested, _) => new RecycleRestoreResult([requested[0]], [requested[1]], []);

        Call(fixture, "DeleteItems", (object)paths);

        Assert.Equal(paths, deleted);
        Assert.Empty(fixture.Model.DesktopIcons);
        Assert.Equal("2 öğe Geri Dönüşüm Kutusu'na taşındı.", ToastText(fixture));

        ToastAction(fixture).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var expected = "2 öğeden 1 tanesi geri alındı. Öğe Geri Dönüşüm Kutusu'nda bulunamadı.";
        Assert.Equal(expected, await WaitForToast(fixture, expected));
        Assert.Equal(Visibility.Visible, fixture.Element("ToastErrorIcon").Visibility);
    });

    [Fact]
    public Task DeletingEverythingShowsTheEmptyFolderAtOnceAndTheNoticeOpensOnlyWithTheWindow() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var deleted = new List<string>();
        fixture.Files = (name, args) => name == "DeleteToRecycleBin" ? Delete(deleted, args) : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        var notice = fixture.Element("EmptyFolderPanel");
        Assert.Equal(Visibility.Hidden, notice.Visibility);

        Call(fixture, "DeleteItems", (object)fixture.Model.DesktopIcons.Select(item => item.FullPath).ToArray());

        Assert.Equal(Visibility.Visible, notice.Visibility);
        Assert.Equal("'Belgeler' klasörü boş", ((TextBlock)fixture.Element("EmptyFolderTitle")).Text);
        Assert.Equal(Visibility.Visible, Toast(fixture).Visibility);
        Assert.False(((Popup)fixture.Element("ToastPopup")).IsOpen);
    });

    [Fact]
    public void TheNoticeIsCenteredBelowTheWindowOrAboveItWhenThereIsNoRoom()
    {
        var place = typeof(MainWindow).GetMethod("PlaceToast", BindingFlags.NonPublic | BindingFlags.Static)!;
        var placements = (CustomPopupPlacement[])place.Invoke(null,
            [new System.Windows.Size(400, 56), new System.Windows.Size(680, 483), new System.Windows.Point()])!;

        Assert.Equal(new System.Windows.Point(140, 483), placements[0].Point);
        Assert.Equal(new System.Windows.Point(140, -56), placements[1].Point);
    }

    [Fact]
    public Task DecliningAPermanentDeleteKeepsTheItem() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Files = (name, _) => name == "DeleteToRecycleBin"
            ? throw new OperationCanceledException()
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        var icon = fixture.Model.DesktopIcons.Single(item => item.Name == "not.txt");

        Call(fixture, "DeleteItems", (object)new[] { icon.FullPath });

        Assert.Contains(icon, fixture.Model.DesktopIcons);
        Assert.Equal(Visibility.Collapsed, Toast(fixture).Visibility);
    });

    [Fact]
    public Task AFailedDeleteIsShownAsAnErrorWithoutUndo() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Files = (name, _) => name == "DeleteToRecycleBin"
            ? throw new IOException("Dosya başka bir işlemde açık.")
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        var icon = fixture.Model.DesktopIcons.Single(item => item.Name == "not.txt");

        Call(fixture, "DeleteItems", (object)new[] { icon.FullPath });

        Assert.Contains(icon, fixture.Model.DesktopIcons);
        Assert.Equal("Silinemedi. not.txt: Dosya başka bir işlemde açık.", ToastText(fixture));
        Assert.Equal(Visibility.Visible, fixture.Element("ToastErrorIcon").Visibility);
        Assert.Equal(Visibility.Collapsed, ToastAction(fixture).Visibility);
    });

    [Fact]
    public Task DriveRootsCannotBeDeletedRenamedOrOfferedInTheMenu() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var deleted = new List<string>();
        fixture.Files = (name, args) => name == "DeleteToRecycleBin" ? Delete(deleted, args) : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);

        Call(fixture, "DeleteItems", (object)new[] { @"C:\" });
        Assert.Empty(deleted);
        Assert.Equal(@"Silinemedi. C:\: Sürücü kökü silinemez.", ToastText(fixture));
        Assert.Equal(Visibility.Collapsed, ToastAction(fixture).Visibility);

        Call(fixture, "RenameItem", @"C:\");
        Assert.Equal("Sürücüler OmniSpot içinden yeniden adlandırılamaz.", ToastText(fixture));

        var menu = (ContextMenu)fixture.Window.FindResource("FileContextMenu");
        MenuItem Item(string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
        Call(fixture, "UpdateBatchMenuItems", menu, @"C:\");
        Assert.All(["RenameMenuItem", "DeleteMenuItem", "CutMenuItem", "ZipMenuItem", "MoveToNewFolderMenuItem"],
            name => Assert.Equal(Visibility.Collapsed, Item(name).Visibility));
        Assert.Equal(Visibility.Visible, Item("CopyMenuItem").Visibility);

        Call(fixture, "UpdateBatchMenuItems", menu, Path.Combine(Folder, "not.txt"));
        Assert.Equal(Visibility.Visible, Item("RenameMenuItem").Visibility);
        Assert.Equal(Visibility.Visible, Item("DeleteMenuItem").Visibility);
    });

    private static object Delete(List<string> deleted, object?[] args)
    {
        deleted.Add((string)args[0]!);
        return FileItemKind.File;
    }

    private static FrameworkElement Toast(Fixture fixture) => fixture.Element("Toast");

    private static string ToastText(Fixture fixture) => ((TextBlock)fixture.Element("ToastText")).Text;

    private static Button ToastAction(Fixture fixture) => (Button)fixture.Element("ToastAction");

    private static async Task<string> WaitForToast(Fixture fixture, string expected)
    {
        for (var attempt = 0; attempt < 300 && ToastText(fixture) != expected; attempt++) await Task.Delay(10);
        return ToastText(fixture);
    }

    private static object? Call(Fixture fixture, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args);
}
