using System.Reflection;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task ACostlyAiRefreshPausesFurtherRefreshesInProportionToItsDuration() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");
        fixture.LocalDelay = TimeSpan.FromMilliseconds(150);

        await fixture.Change(new() { AffectedNames = ["yeni.pdf"] });
        Assert.Equal(2, fixture.LocalCalls);
        await WaitUntil(() => fixture.Clock.LastDelay >= TimeSpan.FromMilliseconds(1500));
        Assert.True(fixture.Clock.LastDelay < TimeSpan.FromSeconds(10));

        await fixture.Change(new() { AffectedNames = ["ikinci.pdf"] });
        await fixture.Change(new() { AffectedNames = ["ucuncu.pdf"] });
        Assert.Equal(2, fixture.LocalCalls);

        fixture.LocalDelay = TimeSpan.Zero;
        fixture.Clock.Elapse();
        await WaitUntil(() => Field(fixture.Window, "_searchRefreshPause") == null);
        await CallAsync(fixture.Window, "ProcessFileChangeAsync");
        Assert.Equal(3, fixture.LocalCalls);
    });

    [Fact]
    public Task AQuickRefreshDoesNotPauseTheNextOne() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");

        await fixture.Change(new() { AffectedNames = ["yeni.pdf"] });
        await fixture.Change(new() { AffectedNames = ["ikinci.pdf"] });

        Assert.Equal(3, fixture.LocalCalls);
        Assert.Null(Field(fixture.Window, "_searchRefreshPause"));
    });

    [Fact]
    public Task AConcealedWindowKeepsTheRefreshUntilItIsShownAgain() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");
        Call(fixture.Window, "UpdateWindowConcealment");
        Assert.Equal(true, Field(fixture.Window, "_windowConcealed"));

        await fixture.Change(new() { AffectedNames = ["yeni.pdf"] });
        await fixture.Change(new() { AffectedNames = ["ikinci.pdf"] });
        Assert.Equal(1, fixture.LocalCalls);

        typeof(MainWindow).GetField("_windowConcealed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(fixture.Window, false);
        Call(fixture.Window, "ResumeDeferredSearchRefresh");
        await CallAsync(fixture.Window, "ProcessFileChangeAsync");
        Assert.Equal(2, fixture.LocalCalls);
    });

    [Fact]
    public Task AnAiResultIgnoresChangedFilesOfAnotherType() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");

        await fixture.Change(new() { AffectedNames = ["cache.tmp", "notlar.txt"] });
        Assert.Equal(1, fixture.LocalCalls);

        await fixture.Change(new() { AffectedNames = ["cache.tmp", "fatura-2026.pdf"] });
        Assert.Equal(2, fixture.LocalCalls);
    });

    private static object? Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    private static object? Call(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

    private static Task CallAsync(MainWindow window, string name) => (Task)Call(window, name)!;
}
