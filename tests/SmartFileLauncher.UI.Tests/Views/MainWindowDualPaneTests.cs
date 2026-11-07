using System.IO;
using System.Reflection;
using System.Windows;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowDualPaneTests
{
    private static readonly string First = Path.Combine(@"C:\Test", "Birinci");
    private static readonly string Second = Path.Combine(@"C:\Test", "İkinci");

    [Fact]
    public Task TheDualPaneWidensTheWindowOnlyWhileItIsOpen() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(First);

        await Call(fixture, "SetDualPaneAsync", true);

        Assert.Equal(1140, fixture.Window.Width);
        Assert.Equal(Visibility.Visible, fixture.Element("DualPane").Visibility);
        Assert.Equal(First, Field<string>(fixture, "_paneFolder"));
        Assert.Equal(2, fixture.Model.DesktopIcons.Count);
        Assert.Equal(2, ((System.Windows.Controls.ListBox)fixture.Element("DualPaneList")).Items.Count);

        await Call(fixture, "SetDualPaneAsync", false);
        Assert.Equal(680, fixture.Window.Width);
        Assert.Equal(Visibility.Collapsed, fixture.Element("DualPane").Visibility);
        Assert.Empty(((System.Windows.Controls.ListBox)fixture.Element("DualPaneList")).Items);
    });

    [Fact]
    public Task SwapExchangesTheTwoFolders() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(First);
        await Call(fixture, "SetDualPaneAsync", true);
        await Call(fixture, "ShowPaneFolderAsync", Second);

        typeof(MainWindow).GetMethod("DualPaneSwap_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Window, [null, null]);
        await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        Assert.Equal(Second, fixture.Model.CurrentFolderPath);
        Assert.Equal(First, Field<string>(fixture, "_paneFolder"));
        Assert.Equal(Visibility.Visible, fixture.Element("BackButton").Visibility);
    });

    private static Task Call(Fixture fixture, string name, params object?[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args)!;

    private static T? Field<T>(Fixture fixture, string name) =>
        (T?)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window);
}
