using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowShelfTests
{
    [Fact]
    public Task TheShelfChipAppearsOnlyWhileItemsAreCollected() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var chip = fixture.Element("ShelfChip");
        Assert.Equal(Visibility.Collapsed, chip.Visibility);

        Add(fixture, @"C:\Test\A\fatura.pdf", @"C:\Test\B\rapor.docx", @"c:\test\a\FATURA.pdf");

        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal("2", ((TextBlock)fixture.Element("ShelfCountText")).Text);

        Call(fixture, "ShelfClear_Click", null, null);
        Assert.Equal(Visibility.Collapsed, chip.Visibility);
        return Task.CompletedTask;
    });

    private static void Add(Fixture fixture, params string[] paths) =>
        Call(fixture, "AddToShelf", (IReadOnlyList<string>)paths);

    private static void Call(Fixture fixture, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args);
}
