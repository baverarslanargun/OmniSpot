using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.UI.ViewModels;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowBatchTests
{
    private static readonly string Folder = Path.Combine(@"C:\Test", "Belgeler");

    [Fact]
    public Task SelectionBarAppearsOnlyForMultipleItemsAndClearsOnRequest() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        var bar = fixture.Element("SelectionBar");
        var icons = fixture.Model.DesktopIcons;

        Select(fixture, icons[0]);
        Assert.Equal(Visibility.Collapsed, bar.Visibility);

        Select(fixture, icons[1]);
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("2 öğe seçildi", ((TextBlock)fixture.Element("SelectionCountText")).Text);

        Call(fixture, "ClearItemSelection");
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
        Assert.DoesNotContain(icons, icon => icon.IsTransferSelected);
    });

    [Fact]
    public Task SelectAllSelectsEveryItemInTheFolder() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);

        Call(fixture, "SelectAllItems");

        Assert.All(fixture.Model.DesktopIcons, icon => Assert.True(icon.IsTransferSelected));
        Assert.Equal(Visibility.Visible, fixture.Element("SelectionBar").Visibility);
    });

    [Fact]
    public Task KeyboardCommandsFollowTheSelectionInsteadOfTheHoveredItem() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        var icons = fixture.Model.DesktopIcons;
        var outside = Path.Combine(Folder, "başka.txt");

        Select(fixture, icons[0]);
        Assert.Equal([outside], Targets(fixture, "KeyboardTargets", outside));

        Select(fixture, icons[1]);
        Assert.Equal(icons.Select(icon => icon.FullPath), Targets(fixture, "KeyboardTargets", outside));
        Assert.Equal([outside], Targets(fixture, "MenuTargets", outside));
        Assert.Equal(icons.Select(icon => icon.FullPath), Targets(fixture, "MenuTargets", icons[0].FullPath));
    });

    [Fact]
    public Task SearchResultSelectionDrivesTheSameBar() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.StandardResults = [Fixture.Result("fatura-1"), Fixture.Result("fatura-2"), Fixture.Result("fatura-3")];
        await fixture.Search("fatura", naturalLanguage: false);
        var list = (ListView)fixture.Element("ResultsList");

        list.SelectedItems.Add(fixture.Model.SearchResults[0]);
        list.SelectedItems.Add(fixture.Model.SearchResults[2]);

        Assert.Equal(Visibility.Visible, fixture.Element("SelectionBar").Visibility);
        Assert.Equal(
            [fixture.Model.SearchResults[0].FullPath, fixture.Model.SearchResults[2].FullPath],
            Targets(fixture, "KeyboardTargets", null));
    });

    private static void Select(Fixture fixture, DesktopIconViewModel icon) =>
        Call(fixture, "SetTransferSelection", icon, true);

    private static IReadOnlyList<string> Targets(Fixture fixture, string method, string? path) =>
        (IReadOnlyList<string>)Call(fixture, method, path)!;

    private static object? Call(Fixture fixture, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args);
}
