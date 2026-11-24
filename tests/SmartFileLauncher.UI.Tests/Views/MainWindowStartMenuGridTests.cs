using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task AllApplicationsFollowTheGridViewSettingAndMoveInFourDirections() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(Enumerable.Range(0, 120)
            .Select(i => new StartMenuApplication($"app-{i:D3}", $"App {i:D3}", "")).ToArray());
        using var fixture = new Fixture(gridView: true, startMenuShell: shell);
        using var keyboardSource = ApplicationKeySource();
        await ReadStartMenuApplications(fixture);
        OpenAllApplications(fixture);
        var loading = ApplicationView(fixture).BrowserLoading;
        Assert.Equal(Visibility.Visible, loading.Visibility);
        Assert.Empty(ApplicationList(fixture).Items);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, loading.Visibility);

        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        Assert.True(view.IsGrid);
        Assert.Equal(120, apps.Items.Count);
        Layout(view);
        Assert.InRange(Realized(apps), 8, 24);
        Point At(int index) => ApplicationContainer(apps, index).TranslatePoint(new Point(), apps);
        Assert.Equal(At(0).Y, At(3).Y);
        Assert.True(At(1).X > At(0).X && At(3).X > At(2).X);
        Assert.Equal(At(0).X, At(4).X);
        Assert.True(At(4).Y > At(0).Y);
        Assert.Equal("TileItem", StyleKey(view, apps.ItemContainerStyle));
        var scrollBar = FindFirst<System.Windows.Controls.Primitives.ScrollBar>(apps,
            bar => bar.Orientation == Orientation.Vertical);
        Assert.False(RaiseApplicationMenu(scrollBar).Handled);
        Assert.Null(apps.SelectedItem);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        void Press(Key key) => SendApplicationKey(search, keyboardSource, key);
        Press(Key.Right);
        Assert.Null(apps.SelectedItem);
        Press(Key.Down);
        Assert.Equal("app-000", apps.SelectedValue);
        Press(Key.Right);
        Assert.Equal("app-001", apps.SelectedValue);
        Press(Key.Down);
        Assert.Equal("app-005", apps.SelectedValue);
        Press(Key.Left);
        Assert.Equal("app-004", apps.SelectedValue);
        Press(Key.Up);
        Press(Key.Up);
        Assert.Equal("app-000", apps.SelectedValue);
        Press(Key.Left);
        Assert.Equal("app-000", apps.SelectedValue);
        for (int i = 0; i < 40; i++) Press(Key.Down);
        Assert.Equal("app-116", apps.SelectedValue);
        Layout(view);
        Assert.NotNull(apps.ItemContainerGenerator.ContainerFromIndex(116));
        Assert.Null(apps.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.InRange(Realized(apps), 8, 24);

        typeof(MainWindow).GetMethod("DisableGridView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Window, null);
        Assert.False(view.IsGrid);
        Assert.Equal("RowItem", StyleKey(view, apps.ItemContainerStyle));
        Assert.Equal("app-116", apps.SelectedValue);
        Press(Key.Down);
        Assert.Equal("app-117", apps.SelectedValue);
    });

    [Fact]
    public Task TheApplicationMenuOffersOnlySupportedActionsAndRunsThemOffTheInterfaceThread() => RunSta(async () =>
    {
        var shell = new StartMenuActionShell(
            new StartMenuApplication("classic", "Classic", "") { CanRunAsAdministrator = true, HasFileLocation = true },
            new StartMenuApplication("store", "Store", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        OpenAllApplications(fixture);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        Layout(view);

        Assert.True(RaiseApplicationMenu(apps).Handled);
        var menu = ApplicationMenu(fixture);
        Assert.False(RaiseApplicationMenu(ApplicationContainer(apps, 0)).Handled);
        Assert.Equal("classic", apps.SelectedValue);
        Assert.Equal(Visibility.Visible, MenuEntry(menu, "ApplicationRunAsMenuItem").Visibility);
        Assert.Equal(Visibility.Visible, MenuEntry(menu, "ApplicationLocationMenuItem").Visibility);
        Assert.Equal(Visibility.Visible, MenuEntry(menu, "ApplicationUninstallMenuItem").Visibility);

        var interfaceThread = Environment.CurrentManagedThreadId;
        Click(MenuEntry(menu, "ApplicationRunAsMenuItem"));
        var runAs = await shell.WaitFor("runas:classic");
        Assert.NotEqual(interfaceThread, runAs.Thread);
        Assert.Equal(ApartmentState.STA, runAs.Apartment);
        Click(MenuEntry(menu, "ApplicationLocationMenuItem"));
        await shell.WaitFor("location:classic");
        Click(MenuEntry(menu, "ApplicationUninstallMenuItem"));
        await shell.WaitFor("installed");
        Click(MenuEntry(menu, "ApplicationOpenMenuItem"));
        Assert.Equal(["classic"], shell.Launched);

        Assert.False(RaiseApplicationMenu(ApplicationContainer(apps, 1)).Handled);
        Assert.Equal("store", apps.SelectedValue);
        Assert.Equal(Visibility.Collapsed, MenuEntry(menu, "ApplicationRunAsMenuItem").Visibility);
        Assert.Equal(Visibility.Collapsed, MenuEntry(menu, "ApplicationLocationMenuItem").Visibility);
        Assert.Equal(Visibility.Visible, MenuEntry(menu, "ApplicationUninstallMenuItem").Visibility);

        RaiseApplicationMenu(ApplicationContainer(apps, 0));
        shell.Failure = new Win32Exception(1223);
        Click(MenuEntry(menu, "ApplicationRunAsMenuItem"));
        await shell.WaitFor("runas:classic", 2);
        shell.Failure = new InvalidOperationException("erişim yok");
        Click(MenuEntry(menu, "ApplicationLocationMenuItem"));
        await shell.WaitFor("location:classic", 2);
        await WaitUntil(() => fixture.Log.GetSnapshot().Any(line => line.Contains("işlemi tamamlanamadı. erişim yok")));
        Assert.Single(fixture.Log.GetSnapshot(), line => line.Contains("işlemi tamamlanamadı"));
        Assert.Equal("“Classic” konumu açılamadı.", ApplicationElement<TextBlock>(fixture, "ToastText").Text);
    });

    [Fact]
    public Task PinningAnApplicationBringsItFirstMarksItAndCanBeUndone() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("alpha", "Alpha", ""), new("beta", "Beta", ""), new("gamma", "Gamma", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        OpenAllApplications(fixture);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        var menu = ApplicationMenu(fixture);
        Layout(view);

        RaiseApplicationMenu(ApplicationContainer(apps, 2));
        Assert.Equal("OmniSpot'ta Sabitle", MenuEntry(menu, "ApplicationPinMenuItem").Header);
        Click(MenuEntry(menu, "ApplicationPinMenuItem"));
        Assert.Equal(["gamma", "alpha", "beta"], apps.Items.Cast<StartMenuApplication>().Select(app => app.Id));
        Assert.Equal("gamma", apps.SelectedValue);
        Assert.Equal(["gamma"], Settings(fixture).PinnedApplications);
        Layout(view);
        Assert.Equal(Visibility.Visible, Named<TextBlock>(ApplicationContainer(apps, 0), "RowPinned").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<TextBlock>(ApplicationContainer(apps, 1), "RowPinned").Visibility);

        RaiseApplicationMenu(ApplicationContainer(apps, 0));
        Assert.Equal("Sabitlemeyi Kaldır", MenuEntry(menu, "ApplicationPinMenuItem").Header);
        Click(MenuEntry(menu, "ApplicationPinMenuItem"));
        Assert.Equal(["alpha", "beta", "gamma"], apps.Items.Cast<StartMenuApplication>().Select(app => app.Id));
        Assert.Empty(Settings(fixture).PinnedApplications);
        Assert.Equal("gamma", apps.SelectedValue);
    });

    [Fact]
    public Task ApplicationSelectionWaitsForTheGlideInEveryLayout() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("exact", "Tool", ""), new("box", "Tool Box", ""), new("kit", "Tool Kit", ""), new("maker", "Tool Maker", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Tool", naturalLanguage: false);
        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        Layout(view);

        Assert.Same(view.BestMatch, view.SelectionSurface(0));
        apps.SelectedIndex = -1;
        var release = Assert.IsType<Action>(view.HoldSelection(0));
        apps.SelectedIndex = 0;
        Assert.False(view.BestMatchSelected);
        release();
        Assert.True(view.BestMatchSelected);
        AssertSelectionWaits(view, apps, 1);

        ApplicationPart<Button>(view, "ShowAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(view);
        Assert.Equal("RowItem", StyleKey(view, apps.ItemContainerStyle));
        AssertSelectionWaits(view, apps, 2);

        typeof(MainWindow).GetMethod("EnableGridView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Window, null);
        Layout(view);
        Assert.Equal("TileItem", StyleKey(view, apps.ItemContainerStyle));
        AssertSelectionWaits(view, apps, 3);
        Assert.Null(view.HoldSelection(40));
    });

    private static void AssertSelectionWaits(StartMenuResults view, ListBox apps, int index)
    {
        apps.SelectedIndex = -1;
        var surface = Assert.IsType<Border>(view.SelectionSurface(index));
        var release = Assert.IsType<Action>(view.HoldSelection(index));
        apps.SelectedIndex = index;
        Assert.NotEqual(SelectionInk, ((SolidColorBrush)surface.BorderBrush).Color);
        release();
        Assert.False(StartMenuResults.GetArriving(ApplicationContainer(apps, index)));
        Assert.Equal(SelectionInk, ((SolidColorBrush)surface.BorderBrush).Color);
        Assert.Equal(SelectionWash, ((SolidColorBrush)surface.Background).Color);
    }

    private static void OpenAllApplications(Fixture fixture) =>
        ApplicationElement<MenuItem>(fixture, "AllApplicationsMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static FrameworkElement ApplicationContainer(ListBox list, int index) =>
        Assert.IsAssignableFrom<FrameworkElement>(list.ItemContainerGenerator.ContainerFromIndex(index));

    private static ContextMenu ApplicationMenu(Fixture fixture) =>
        Assert.IsType<ContextMenu>(fixture.Window.FindResource("ApplicationContextMenu"));

    private static MenuItem MenuEntry(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().Single(item => item.Name == name);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static ContextMenuEventArgs RaiseApplicationMenu(FrameworkElement target)
    {
        var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [target, true], null)!;
        target.RaiseEvent(args);
        return args;
    }

    private static string? StyleKey(FrameworkElement owner, Style style) =>
        owner.Resources.Keys.OfType<string>().FirstOrDefault(key => ReferenceEquals(owner.Resources[key], style));

    private static AppSettings Settings(Fixture fixture) => Assert.IsType<AppSettings>(typeof(MainWindow)
        .GetField("_appSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window));

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        FindFirst<T>(root, element => element.Name == name);

    private static T FindFirst<T>(DependencyObject root, Func<T, bool> match) where T : FrameworkElement =>
        FindMatch(root, match) ?? throw new InvalidOperationException(typeof(T).Name);

    private static T? FindMatch<T>(DependencyObject root, Func<T, bool> match) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && match(element)) return element;
            if (FindMatch(child, match) is { } nested) return nested;
        }
        return null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class StartMenuActionShell(params StartMenuApplication[] apps) : IStartMenuShell, IStartMenuApplicationActions
    {
        private readonly List<(string Call, int Thread, ApartmentState Apartment)> _calls = [];
        internal List<string> Launched { get; } = [];
        internal Exception? Failure { get; set; }

        public IReadOnlyList<StartMenuApplication> ReadApplications(CancellationToken cancellationToken) => apps;
        public void Launch(StartMenuApplication app) => Launched.Add(app.Id);
        public ImageSource? LoadIcon(StartMenuApplication app) => null;
        public IDisposable Watch(IntPtr window, Action changed) => throw new NotSupportedException();
        public void RunAsAdministrator(StartMenuApplication app) => Record("runas:" + app.Id);
        public void OpenFileLocation(StartMenuApplication app) => Record("location:" + app.Id);
        public void OpenInstalledApplications() => Record("installed");

        private void Record(string call)
        {
            lock (_calls) _calls.Add((call, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
            if (Failure is { } failure) throw failure;
        }

        internal async Task<(int Thread, ApartmentState Apartment)> WaitFor(string call, int count = 1)
        {
            (string Call, int Thread, ApartmentState Apartment)[] matches = [];
            await WaitUntil(() =>
            {
                lock (_calls) matches = _calls.Where(entry => entry.Call == call).ToArray();
                return matches.Length >= count;
            });
            await Task.Delay(20);
            return (matches[^1].Thread, matches[^1].Apartment);
        }
    }
}
