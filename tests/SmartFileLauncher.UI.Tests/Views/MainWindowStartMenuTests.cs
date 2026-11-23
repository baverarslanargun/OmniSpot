using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task StartMenuSearchCanOpenAUniqueApplicationBeforeTheFileIndexIsReady() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(new StartMenuApplication("writer", "Writer", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        using var keyboardSource = ApplicationKeySource();
        fixture.Model.IsIndexed = false;
        var search = ApplicationElement<TextBox>(fixture, "SearchBox");

        Assert.True(search.IsEnabled);
        fixture.Type("Writ", naturalLanguage: false);
        Assert.Equal(Visibility.Visible, ApplicationElement<ContentControl>(fixture, "StartMenuHost").Visibility);
        Assert.Equal(Visibility.Collapsed, ApplicationElement<Grid>(fixture, "FileContentArea").Visibility);
        Assert.Empty(ApplicationList(fixture).Items);
        SendApplicationKey(search, keyboardSource, Key.Enter);
        Assert.Empty(shell.Launched);

        await ReadStartMenuApplications(fixture);
        Assert.Equal("writer", ApplicationList(fixture).SelectedValue);
        Assert.Empty(shell.Launched);
        SendApplicationKey(search, keyboardSource, Key.Enter);

        Assert.Equal(["writer"], shell.Launched);
        Assert.Equal(0, fixture.StandardCalls);
    });

    [Fact]
    public Task StartMenuSearchDoesNotOpenAnOldResultOrChooseBetweenEqualApplications() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("writer", "Writer", ""),
            new("terminal-1", "Terminal", "First publisher"),
            new("terminal-2", "Terminal", "Second publisher"));
        var files = new StartMenuTestFileOperations();
        using var fixture = new Fixture(startMenuShell: shell, fileOperations: files);
        using var keyboardSource = ApplicationKeySource();
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Writer", naturalLanguage: false);
        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var fileList = ApplicationElement<ListView>(fixture, "ResultsList");
        fileList.SelectedIndex = 0;

        fixture.StandardPrefix = "terminal";
        fixture.Type("Terminal", naturalLanguage: false);
        var apps = ApplicationList(fixture);
        Assert.Equal(2, apps.Items.Count);
        Assert.Null(apps.SelectedItem);
        SendApplicationKey(search, keyboardSource, Key.Enter);
        Assert.Empty(shell.Launched);
        Assert.Empty(files.Opened);

        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(apps.SelectedItem);
        Assert.Empty(shell.Launched);
        Assert.Empty(files.Opened);
        var view = ApplicationView(fixture);
        Assert.False(view.LeadsWithBestMatch);
        Assert.Equal(Visibility.Collapsed, ApplicationPart<Border>(view, "BestMatch").Visibility);
        Assert.Equal(Visibility.Visible, ApplicationPart<Grid>(view, "Strip").Visibility);

        SendApplicationKey(search, keyboardSource, Key.Down);
        Assert.Equal("terminal-1", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Right);
        Assert.Equal("terminal-2", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Right);
        Assert.Equal("terminal-2", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Down);
        Assert.Equal(0, fileList.SelectedIndex);
        Assert.Null(apps.SelectedItem);
        SendApplicationKey(search, keyboardSource, Key.Up);
        Assert.Equal("terminal-1", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Up);
        Assert.Equal("terminal-1", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Right);
        SendApplicationKey(search, keyboardSource, Key.Enter);
        Assert.Equal(["terminal-2"], shell.Launched);
        Assert.Empty(files.Opened);
    });

    [Fact]
    public Task AUniqueBestMatchLeadsAndTheOtherApplicationsFormAStrip() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("exact", "Tool", ""),
            new("box", "Tool Box", ""),
            new("kit", "Tool Kit", ""),
            new("maker", "Tool Maker", ""));
        var files = new StartMenuTestFileOperations();
        using var fixture = new Fixture(startMenuShell: shell, fileOperations: files);
        using var keyboardSource = ApplicationKeySource();
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Tool", naturalLanguage: false);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var fileList = ApplicationElement<ListView>(fixture, "ResultsList");
        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        var bestMatch = ApplicationPart<Border>(view, "BestMatch");
        var showAll = ApplicationPart<Button>(view, "ShowAll");
        Assert.True(view.LeadsWithBestMatch);
        Assert.Equal(Visibility.Visible, bestMatch.Visibility);
        Assert.Equal("exact", Assert.IsType<StartMenuApplication>(bestMatch.DataContext).Id);
        Assert.Equal("exact", apps.SelectedValue);
        Assert.True(view.BestMatchSelected);
        Assert.Equal("Diğer", ApplicationPart<TextBlock>(view, "StripLabel").Text);
        Assert.Equal("Tümü (4)", showAll.Content);
        Assert.Equal(Visibility.Visible, showAll.Visibility);
        Layout(view);
        Assert.Equal(Visibility.Collapsed,
            Assert.IsType<ListBoxItem>(apps.ItemContainerGenerator.ContainerFromIndex(0)).Visibility);
        Assert.Equal(Visibility.Visible,
            Assert.IsType<ListBoxItem>(apps.ItemContainerGenerator.ContainerFromIndex(1)).Visibility);

        SendApplicationKey(search, keyboardSource, Key.Right);
        Assert.Equal("exact", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Down);
        Assert.Equal("box", apps.SelectedValue);
        Assert.False(view.BestMatchSelected);
        SendApplicationKey(search, keyboardSource, Key.Right);
        Assert.Equal("kit", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Left);
        SendApplicationKey(search, keyboardSource, Key.Left);
        Assert.Equal("box", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Up);
        Assert.Equal("exact", apps.SelectedValue);
        Assert.True(view.BestMatchSelected);
        SendApplicationKey(search, keyboardSource, Key.Down);
        SendApplicationKey(search, keyboardSource, Key.Down);
        Assert.Equal(0, fileList.SelectedIndex);
        Assert.Null(apps.SelectedItem);
        SendApplicationKey(search, keyboardSource, Key.Up);
        Assert.Equal("box", apps.SelectedValue);
        SendApplicationKey(search, keyboardSource, Key.Up);
        SendApplicationKey(search, keyboardSource, Key.Enter);
        Assert.Equal(["exact"], shell.Launched);
        Assert.Empty(files.Opened);
    });

    [Fact]
    public void ApplicationChipsShareTheRowByShrinkingTheWidestFirst()
    {
        Assert.Equal(double.PositiveInfinity, ApplicationChipPanel.ShareCap([100, 120], 400));
        Assert.Equal(200, ApplicationChipPanel.ShareCap([100, 300, 300], 500));
        Assert.Equal(160, ApplicationChipPanel.ShareCap([250, 250, 250], 480));
        Assert.Equal(150, ApplicationChipPanel.ShareCap([0, 260, 260], 300));
    }

    [Fact]
    public Task SameNamedApplicationsStayWholeAndShowWhatTellsThemApart() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("help-a", "V-Ray for Maya online help index", "https://docs.example.com/display/VMAYA"),
            new("help-b", "V-Ray for Maya online help index", "https://docs.example.com/display/VRAY4MAYA"),
            new("classes-a", "V-Ray SDK classes index", @"C:\Program Files\Chaos Group\V-Ray\Maya 2015 for x64\docs\vrayclasses.chm"),
            new("classes-b", "V-Ray SDK classes index", @"C:\Program Files\Chaos Group\V-Ray\Maya 2022 for x64\vray\docs\vrayclasses.chm"));
        using var fixture = new Fixture(startMenuShell: shell, fileOperations: new StartMenuTestFileOperations());
        await ReadStartMenuApplications(fixture);
        await fixture.Search("index", naturalLanguage: false);

        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        Layout(view);
        Assert.Equal(Visibility.Visible, ApplicationPart<Button>(view, "ShowAll").Visibility);
        Assert.Equal(3, apps.Items.Count);

        static IEnumerable<DependencyObject> Tree(DependencyObject root)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (var nested in Tree(child)) yield return nested;
            }
        }

        string[] expected = ["VMAYA", "VRAY4MAYA", "Maya 2015 for x64"];
        for (var index = 0; index < apps.Items.Count; index++)
        {
            var chip = Assert.IsType<ListBoxItem>(apps.ItemContainerGenerator.ContainerFromIndex(index));
            Assert.True(chip.ActualWidth > 100, $"chip {index} is {chip.ActualWidth} wide");
            Assert.True(chip.TranslatePoint(new Point(chip.ActualWidth, 0), apps).X <= apps.ActualWidth + 0.5,
                $"chip {index} runs past the strip");
            Assert.Contains(Tree(chip).OfType<TextBlock>(), text => text.Text == expected[index]);
        }

        apps.SelectedIndex = 1;
        Layout(view);
        var selected = Assert.IsType<ListBoxItem>(apps.ItemContainerGenerator.ContainerFromIndex(1));
        var surface = Assert.IsType<Border>(selected.Template.FindName("Surface", selected));
        Assert.Equal(Color.FromRgb(0xEF, 0xF6, 0xFF), Assert.IsType<SolidColorBrush>(surface.Background).Color);
        Assert.Equal(Color.FromRgb(0x00, 0x7A, 0xFF), Assert.IsType<SolidColorBrush>(surface.BorderBrush).Color);
    });

    [Fact]
    public Task TypingWithoutFileResultsKeepsTheFileAreaClosedUntilResultsArrive() =>RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("store", "Microsoft Store", ""),
            new("start", "Microsoft Start", ""),
            new("edge", "Microsoft Edge", ""));
        using var fixture = new Fixture(startMenuShell: shell) { StandardTotal = 50 };
        await ReadStartMenuApplications(fixture);
        var area = ApplicationElement<Grid>(fixture, "FileContentArea");
        var changes = new List<Visibility>();
        DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement))
            .AddValueChanged(area, (_, _) => changes.Add(area.Visibility));

        async Task TypeAndWait(string text)
        {
            fixture.Type(text, naturalLanguage: false);
            await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        await TypeAndWait("microsoft s");
        Assert.Equal(Visibility.Visible, area.Visibility);
        Assert.Empty(changes);

        fixture.StandardTotal = 0;
        await TypeAndWait("microsoft st");
        Assert.Equal([Visibility.Collapsed], changes);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)fixture.Window.FindName("ErrorPanel")).Visibility);
        foreach (var text in new[] { "microsoft sto", "microsoft stor", "microsoft sto" })
        {
            fixture.Type(text, naturalLanguage: false);
            Assert.Equal(Visibility.Collapsed, area.Visibility);
            await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.Equal([Visibility.Collapsed], changes);

        fixture.StandardTotal = 50;
        await TypeAndWait("microsoft s");
        Assert.Equal([Visibility.Collapsed, Visibility.Visible], changes);
        Assert.Equal(50, fixture.Model.SearchResults.Count);
    });

    [Fact]
    public Task ApplicationsAreOfferedInAnOpenFolderOnceTheSearchLeavesTheFolder() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(new StartMenuApplication("writer", "Writer", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        fixture.Model.CurrentFolderPath = @"C:\Test\Belgeler";
        var host = ApplicationElement<ContentControl>(fixture, "StartMenuHost");

        fixture.Type("Writer", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(Visibility.Visible, host.Visibility);

        ApplicationElement<Button>(fixture, "SearchScopeChipClear")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Visibility.Visible, host.Visibility);
        Assert.Equal("writer", ApplicationList(fixture).SelectedValue);
    });

    [Fact]
    public Task ASingleApplicationShowsOnlyTheBestMatch() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(new StartMenuApplication("writer", "Writer", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Writer", naturalLanguage: false);

        var view = ApplicationView(fixture);
        Assert.Equal(Visibility.Visible, ApplicationPart<Border>(view, "BestMatch").Visibility);
        Assert.Equal(Visibility.Collapsed, ApplicationPart<Grid>(view, "Strip").Visibility);
        Assert.True(view.BestMatchSelected);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ApplicationAndFileRowsShareKeyboardNavigationWithoutAFileTargetOnTheApplication(bool gridView) =>
        RunSta(async () =>
        {
            var shell = new StartMenuTestShell(new StartMenuApplication("writer", "Writer", ""));
            var files = new StartMenuTestFileOperations();
            using var fixture = new Fixture(gridView: gridView, startMenuShell: shell, fileOperations: files);
            using var keyboardSource = ApplicationKeySource();
            await ReadStartMenuApplications(fixture);
            await fixture.Search("Writer", naturalLanguage: false);

            var search = ApplicationElement<TextBox>(fixture, "SearchBox");
            var fileList = ApplicationElement<ListView>(fixture, "ResultsList");
            var apps = ApplicationList(fixture);
            var file = Assert.Single(fixture.Model.SearchResults);
            Assert.Equal("1 sonuç", await fixture.WaitForCount("1 sonuç"));
            Assert.Equal("writer", apps.SelectedValue);

            SendApplicationKey(search, keyboardSource, Key.Down);
            Assert.Same(file, fileList.SelectedItem);
            Assert.Null(apps.SelectedItem);
            SendApplicationKey(search, keyboardSource, Key.Enter);
            Assert.Equal([file.FullPath], files.Opened);
            Assert.Empty(shell.Launched);

            SendApplicationKey(search, keyboardSource, Key.Up);
            Assert.Equal("writer", apps.SelectedValue);
            Assert.Empty(fileList.SelectedItems);
            Assert.False(file.IsTransferSelected);
            Assert.Null(fixture.Model.SelectedItemPath);
            SendApplicationKey(fixture.Window, keyboardSource, Key.Delete);
            Assert.Empty(files.Deleted);
            SendApplicationKey(search, keyboardSource, Key.Enter);
            Assert.Equal(["writer"], shell.Launched);
            Assert.Equal([file.FullPath], files.Opened);
        });

    [Fact]
    public Task BrowsingAllApplicationsFiltersBySubstringAndEscapeRestoresTheFileSelection() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("exact", "Tool", ""),
            new("box", "Tool Box", ""),
            new("kit", "Tool Kit", ""),
            new("maker", "Tool Maker", ""),
            new("interior", "Microtool", ""),
            new("other", "Other", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        using var keyboardSource = ApplicationKeySource();
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Tool", naturalLanguage: false);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var fileList = ApplicationElement<ListView>(fixture, "ResultsList");
        var file = Assert.Single(fixture.Model.SearchResults);
        var host = ApplicationElement<ContentControl>(fixture, "StartMenuHost");
        var view = Assert.IsType<StartMenuResults>(host.Content);
        var apps = ApplicationList(fixture);
        var showAll = Assert.IsType<Button>(view.FindName("ShowAll"));
        Assert.Equal(3, apps.Items.Count);
        Assert.Equal(Visibility.Visible, showAll.Visibility);

        fileList.SelectedIndex = 0;
        showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Tool", search.Text);
        Assert.Equal(5, apps.Items.Count);
        Assert.Equal(Visibility.Collapsed, ApplicationElement<Grid>(fixture, "FileContentArea").Visibility);

        fixture.Type("icroto", naturalLanguage: false);
        Assert.Equal("interior", Assert.IsType<StartMenuApplication>(Assert.Single(apps.Items)).Id);
        Assert.Equal(1, fixture.StandardCalls);
        SendApplicationKey(search, keyboardSource, Key.Escape);

        Assert.Equal("Tool", search.Text);
        Assert.Same(file, fileList.SelectedItem);
        Assert.Null(apps.SelectedItem);
        Assert.Equal(3, apps.Items.Count);
        Assert.Equal(Visibility.Visible, ApplicationElement<Grid>(fixture, "FileContentArea").Visibility);
        Assert.Equal(1, fixture.StandardCalls);

        var allApplicationsMenuItem = Assert.IsType<MenuItem>(typeof(MainWindow)
            .GetField("AllApplicationsMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window));
        allApplicationsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("", search.Text);
        Assert.Equal(6, apps.Items.Count);
        SendApplicationKey(search, keyboardSource, Key.Escape);
        Assert.Equal("Tool", search.Text);
        Assert.Same(file, fileList.SelectedItem);
        Assert.Equal(1, fixture.StandardCalls);
    });

    [Fact]
    public Task TheBackArrowAndTheMouseBackButtonLeaveAllApplications() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(
            new("exact", "Tool", ""),
            new("box", "Tool Box", ""),
            new("kit", "Tool Kit", ""),
            new("maker", "Tool Maker", ""));
        using var fixture = new Fixture(startMenuShell: shell);
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Tool", naturalLanguage: false);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var fileList = ApplicationElement<ListView>(fixture, "ResultsList");
        var file = Assert.Single(fixture.Model.SearchResults);
        var view = ApplicationView(fixture);
        var apps = ApplicationList(fixture);
        var showAll = ApplicationPart<Button>(view, "ShowAll");
        var back = ApplicationPart<Button>(view, "BrowseBack");
        Assert.Equal(Visibility.Collapsed, back.Visibility);

        fileList.SelectedIndex = 0;
        showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(4, apps.Items.Count);
        Assert.Equal(Visibility.Visible, back.Visibility);
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Tool", search.Text);
        Assert.Same(file, fileList.SelectedItem);
        Assert.Equal(Visibility.Collapsed, back.Visibility);
        Assert.Equal(Visibility.Visible, ApplicationElement<Grid>(fixture, "FileContentArea").Visibility);

        void MouseButtonDown(MouseButton button) => apps.RaiseEvent(
            new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                { RoutedEvent = UIElement.PreviewMouseDownEvent });

        showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MouseButtonDown(MouseButton.XButton2);
        Assert.Equal(Visibility.Visible, back.Visibility);
        Assert.Equal(4, apps.Items.Count);
        MouseButtonDown(MouseButton.XButton1);
        Assert.Equal("Tool", search.Text);
        Assert.Same(file, fileList.SelectedItem);
        Assert.Equal(Visibility.Collapsed, back.Visibility);
        Assert.Equal(1, fixture.StandardCalls);
    });

    private static async Task ReadStartMenuApplications(Fixture fixture)
    {
        var catalog = Assert.IsType<StartMenuCatalog>(typeof(MainWindow)
            .GetField("_startMenuCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window));
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(catalog.IsReady);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static T ApplicationElement<T>(Fixture fixture, string name) where T : FrameworkElement =>
        Assert.IsType<T>(fixture.Window.FindName(name));

    private static ListBox ApplicationList(Fixture fixture) =>
        ApplicationPart<ListBox>(ApplicationView(fixture), "Applications");

    private static StartMenuResults ApplicationView(Fixture fixture) =>
        Assert.IsType<StartMenuResults>(ApplicationElement<ContentControl>(fixture, "StartMenuHost").Content);

    private static T ApplicationPart<T>(StartMenuResults view, string name) where T : FrameworkElement =>
        Assert.IsType<T>(view.FindName(name));

    private static HwndSource ApplicationKeySource() =>
        new(new HwndSourceParameters("OmniSpot test keys") { Width = 1, Height = 1, WindowStyle = 0 });

    private static void SendApplicationKey(UIElement target, PresentationSource source, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(input);
        if (input.Handled) return;
        input.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(input);
    }

    private sealed class StartMenuTestShell(params StartMenuApplication[] apps) : IStartMenuShell
    {
        internal List<string> Launched { get; } = [];

        public IReadOnlyList<StartMenuApplication> ReadApplications(CancellationToken cancellationToken) => apps;
        public void Launch(StartMenuApplication app) => Launched.Add(app.Id);
        public ImageSource? LoadIcon(StartMenuApplication app) => null;
        public IDisposable Watch(IntPtr window, Action changed) => throw new NotSupportedException();
    }

    private sealed class StartMenuTestFileOperations : IFileOperationService
    {
        internal List<string> Opened { get; } = [];
        internal List<string> Deleted { get; } = [];

        public FileItemKind GetItemKind(string path) => FileItemKind.File;
        public void OpenFile(string path) => Opened.Add(path);

        public FileItemKind DeleteToRecycleBin(string path)
        {
            Deleted.Add(path);
            return FileItemKind.File;
        }

        public PasteOperationResult Paste(string sourcePath, string targetFolder, bool move) =>
            throw new NotSupportedException();
        public RenameOperationResult Rename(string path, string newName) => throw new NotSupportedException();
        public string CreateFolder(string targetFolder, string name) => throw new NotSupportedException();
        public string CreateTextFile(string targetFolder, string name) => throw new NotSupportedException();
        public void OpenWith(string path) => throw new NotSupportedException();
        public void Reveal(string path) => throw new NotSupportedException();
        public void ShowProperties(string path) => throw new NotSupportedException();
    }
}
