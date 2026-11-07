using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowTabTests
{
    private static readonly string First = Path.Combine(@"C:\Test", "Birinci");
    private static readonly string Second = Path.Combine(@"C:\Test", "İkinci");

    [Fact]
    public Task EachTabKeepsItsOwnLocationAndHistory() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(First);
        Assert.Equal(Visibility.Collapsed, fixture.Element("TabStrip").Visibility);

        await OpenTab(fixture, null, activate: true);
        Assert.Equal(Visibility.Visible, fixture.Element("TabStrip").Visibility);
        Assert.Null(fixture.Model.CurrentFolderPath);
        Assert.Equal(Visibility.Collapsed, fixture.Element("BackButton").Visibility);

        await fixture.Navigate(Second);
        await Activate(fixture, 0);
        Assert.Equal(First, fixture.Model.CurrentFolderPath);
        Assert.Equal(Visibility.Visible, fixture.Element("BackButton").Visibility);

        await Activate(fixture, 1);
        Assert.Equal(Second, fixture.Model.CurrentFolderPath);
        await fixture.Back();
        Assert.Null(fixture.Model.CurrentFolderPath);

        await Close(fixture, 1);
        Assert.Equal(First, fixture.Model.CurrentFolderPath);
        Assert.Equal(Visibility.Collapsed, fixture.Element("TabStrip").Visibility);
    });

    [Fact]
    public Task SwitchingTabsRestoresASearchWithoutRunningItAgain() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Search("fatura", naturalLanguage: false);
        var result = Assert.Single(fixture.Model.SearchResults);

        await OpenTab(fixture, null, activate: true);
        Assert.Equal(string.Empty, fixture.SearchBox.Text);
        await Activate(fixture, 0);

        Assert.Equal("fatura", fixture.SearchBox.Text);
        Assert.Same(result, Assert.Single(fixture.Model.SearchResults));
        Assert.Equal(1, fixture.StandardCalls);
    });

    [Fact]
    public Task ABackgroundTabWaitsUntilItIsActivated() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(First);

        await OpenTab(fixture, Second, activate: false);

        Assert.Equal(First, fixture.Model.CurrentFolderPath);
        var labels = ((StackPanel)fixture.Element("TabStripItems")).Children.OfType<Button>()
            .Select(button => button.Content is StackPanel { Children: [TextBlock title, ..] } ? title.Text : null)
            .OfType<string>();
        Assert.Equal(["Birinci", "İkinci"], labels);

        await Activate(fixture, 1);
        Assert.Equal(Second, fixture.Model.CurrentFolderPath);
    });

    private static Task OpenTab(Fixture fixture, string? folder, bool activate)
    {
        var entryType = typeof(MainWindow).GetNestedType("NavigationEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, folder, 0d, null);
        return Call(fixture, "OpenTabAsync", entry, activate);
    }

    private static Task Activate(Fixture fixture, int index) => Call(fixture, "ActivateTabAsync", index);

    private static Task Close(Fixture fixture, int index) => Call(fixture, "CloseTabAsync", index);

    private static Task Call(Fixture fixture, string name, params object?[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args)!;
}
