using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowInlineNamingTests
{
    private static readonly string Folder = Path.Combine(@"C:\Test", "Belgeler");

    [Fact]
    public Task RenamingAFolderItemHappensInPlaceAndUpdatesTheView() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var renamed = new List<string>();
        fixture.Files = (name, args) => name == "Rename" ? Rename(renamed, args) : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));
        var icon = fixture.Model.DesktopIcons.Single(item => item.Name == "not.txt");

        Call(fixture, "RenameItem", icon.FullPath);
        var editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        Assert.Equal("not.txt", editor.Box.Text);
        Assert.Equal(0, Anchor(fixture).Opacity);

        editor.Box.Text = "toplanti-notlari.txt";
        Press(editor.Box, Key.Enter);

        Assert.Equal(["not.txt → toplanti-notlari.txt"], renamed);
        Assert.Equal("toplanti-notlari.txt", icon.Name);
        Assert.Equal(Path.Combine(Folder, "toplanti-notlari.txt"), icon.FullPath);
        Assert.Null(Editor(fixture));
    });

    [Fact]
    public Task InvalidNamesStayInTheEditorAndEscapeKeepsTheOriginal() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var renamed = new List<string>();
        fixture.Files = (name, args) => name == "Rename"
            ? throw new IOException("Hedef kullanımda.")
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));
        var icon = fixture.Model.DesktopIcons.Single(item => item.Name == "not.txt");

        Call(fixture, "RenameItem", icon.FullPath);
        var editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        var anchor = Anchor(fixture);
        editor.Box.Text = "not:yeni.txt";
        Press(editor.Box, Key.Enter);
        Assert.Same(editor, Editor(fixture));
        Assert.Equal("Ad şu karakterleri içeremez: \\ / : * ? \" < > |", ErrorText(editor));

        editor.Box.Text = "not-yeni.txt";
        Assert.Equal(Visibility.Collapsed, ErrorVisibility(editor));
        Press(editor.Box, Key.Enter);
        Assert.Same(editor, Editor(fixture));
        Assert.Equal("Hedef kullanımda.", ErrorText(editor));

        Press(editor.Box, Key.Escape);
        Assert.Null(Editor(fixture));
        Assert.Equal("not.txt", icon.Name);
        Assert.Equal(DependencyProperty.UnsetValue, anchor.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Empty(renamed);
    });

    [Fact]
    public Task ASearchResultIsRenamedInItsOwnRow() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var renamed = new List<string>();
        fixture.Files = (name, args) => name == "Rename" ? Rename(renamed, args) : throw new NotSupportedException(name);
        await fixture.Search("fatura", naturalLanguage: false);
        Realize(fixture.Element("ResultsList"));
        var result = Assert.Single(fixture.Model.SearchResults);

        Call(fixture, "RenameItem", result.FullPath);
        var editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        Assert.Equal("ilk-fatura", editor.Box.Text);
        editor.Box.Text = "kasim-faturasi";
        Press(editor.Box, Key.Enter);

        Assert.Equal(["ilk-fatura → kasim-faturasi"], renamed);
        Assert.Equal("kasim-faturasi", Assert.Single(fixture.Model.SearchResults).Name);
        Assert.Null(Editor(fixture));
    });

    [Fact]
    public Task NewFolderIsNamedInPlaceAndEscapeCreatesNothing() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var created = new List<string>();
        fixture.Files = (name, args) => name == "CreateFolder"
            ? Create(created, args)
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));
        var count = fixture.Model.DesktopIcons.Count;

        Call(fixture, "ContextMenu_NewFolder", fixture.Window, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        Assert.Equal("Yeni klasör", editor.Box.Text);
        Assert.Equal(count + 1, fixture.Model.DesktopIcons.Count);
        Assert.Equal("Yeni klasör", fixture.Model.DesktopIcons[0].Name);

        Press(editor.Box, Key.Escape);
        Assert.Null(Editor(fixture));
        Assert.Empty(created);
        Assert.Equal(count, fixture.Model.DesktopIcons.Count);

        Call(fixture, "ContextMenu_NewFolder", fixture.Window, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        editor.Box.Text = "Toplantılar";
        Press(editor.Box, Key.Enter);

        Assert.Equal([Path.Combine(Folder, "Toplantılar")], created);
        Assert.Null(Editor(fixture));
    });

    [Fact]
    public Task TheEmptyFolderSpaceStaysReservedWhileAFolderFillsAndEmpties() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var empty = Path.Combine(@"C:\Test", "Bos");
        fixture.EmptyFolders.Add(empty);
        var notice = fixture.Element("EmptyFolderPanel");

        await fixture.Navigate(Folder);
        Assert.Equal(Visibility.Hidden, notice.Visibility);

        await fixture.Navigate(empty);
        Realize(fixture.Element("DesktopIconsScroll"));
        Assert.Equal(Visibility.Visible, notice.Visibility);
        Call(fixture, "ContextMenu_NewTextFile", fixture.Window, new RoutedEventArgs());
        Assert.Equal(Visibility.Hidden, notice.Visibility);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.IsType<InlineNameEditor>(Editor(fixture));

        Press(Editor(fixture)!.Box, Key.Escape);
        Assert.Equal(Visibility.Visible, notice.Visibility);
    });

    [Fact]
    public Task FileChangesElsewhereWaitUntilTheNewItemIsNamed() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var created = new List<string>();
        fixture.Files = (name, args) => name == "CreateTextFile"
            ? Create(created, args)
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));

        Call(fixture, "ContextMenu_NewTextFile", fixture.Window, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var editor = Assert.IsType<InlineNameEditor>(Editor(fixture));
        await fixture.Change(new FileChangeEvent { FullPath = @"C:\Windows\Temp\kayit.log", ChangeType = FileChangeType.Created });

        Assert.Same(editor, Editor(fixture));
        Assert.Equal("Yeni Metin Belgesi.txt", fixture.Model.DesktopIcons[0].Name);
        editor.Box.Text = "toplanti.txt";
        Press(editor.Box, Key.Enter);
        Assert.Equal([Path.Combine(Folder, "toplanti.txt")], created);
        Assert.Null(Editor(fixture));
    });

    [Fact]
    public Task OnTheHomePageANewItemGoesToTheDesktopAndIsAnnounced() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var created = new List<string>();
        fixture.Files = (name, args) => name == "CreateTextFile"
            ? Create(created, args)
            : throw new NotSupportedException(name);
        await fixture.Navigate(Folder);
        await fixture.Back();
        Assert.Equal(Visibility.Visible, fixture.Element("DesktopIconsScroll").Visibility);
        Realize(fixture.Element("DesktopIconsScroll"));
        var count = fixture.Model.DesktopIcons.Count;

        Call(fixture, "ContextMenu_NewTextFile", fixture.Window, new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Null(Editor(fixture));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Assert.Equal([Path.Combine(desktop, "Yeni Metin Belgesi.txt")], created);
        Assert.Equal("“Yeni Metin Belgesi.txt” Masaüstü'nde oluşturuldu.", ((TextBlock)fixture.Element("ToastText")).Text);
        Assert.Equal(count, fixture.Model.DesktopIcons.Count);
    });

    [Fact]
    public Task TabFromTheSearchBoxWalksThroughTheFolderItems() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));
        var icons = fixture.Model.DesktopIcons;
        UIElement Item(int index) => (UIElement)Call(fixture, "MainItemContainer", icons[index].FullPath)!;

        Press(fixture.SearchBox, Key.Tab);
        Assert.True(icons[0].IsTransferSelected);
        Press(Item(0), Key.Tab);
        Assert.True(icons[1].IsTransferSelected);
        Assert.False(icons[0].IsTransferSelected);
        Press(Item(1), Key.Tab);
        Assert.True(icons[1].IsTransferSelected);

        Call(fixture, "StepFolderFocus", icons[1], -1);
        Assert.True(icons[0].IsTransferSelected);
        Assert.False(icons[1].IsTransferSelected);
    });

    [Fact]
    public Task NoticesUseTheToastAndActionsRunFromIt() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        var toast = fixture.Element("Toast");
        var text = (TextBlock)fixture.Element("ToastText");
        var action = (Button)fixture.Element("ToastAction");
        var errorIcon = fixture.Element("ToastErrorIcon");

        Call(fixture, "NotifyFileTransfer", "3 öğe kopyalandı.");
        Assert.Equal(Visibility.Visible, toast.Visibility);
        Assert.Equal("3 öğe kopyalandı.", text.Text);
        Assert.Equal(Visibility.Collapsed, action.Visibility);
        Assert.Equal(Visibility.Collapsed, errorIcon.Visibility);

        var invoked = 0;
        Call(fixture, "ShowErrorToast", "“kayit.m4a” açılamadı.", "Birlikte aç", new Action(() => invoked++));
        Assert.Equal("“kayit.m4a” açılamadı.", text.Text);
        Assert.Equal(Visibility.Visible, errorIcon.Visibility);
        Assert.Equal("Birlikte aç", action.Content);
        action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, invoked);
        Assert.Equal(Visibility.Collapsed, toast.Visibility);
    });

    private static object Rename(List<string> renamed, object?[] args)
    {
        var path = (string)args[0]!;
        var name = (string)args[1]!;
        renamed.Add($"{Path.GetFileName(path)} → {name}");
        return new RenameOperationResult(Path.Combine(Path.GetDirectoryName(path)!, name), FileItemKind.File);
    }

    private static object Create(List<string> created, object?[] args)
    {
        var path = Path.Combine((string)args[0]!, (string)args[1]!);
        created.Add(path);
        return path;
    }

    private static object? Session(Fixture fixture) =>
        typeof(MainWindow).GetField("_nameEdit", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window);

    private static InlineNameEditor? Editor(Fixture fixture) =>
        Session(fixture) is { } session
            ? (InlineNameEditor?)session.GetType().GetProperty("Editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)
            : null;

    private static TextBlock Anchor(Fixture fixture)
    {
        var session = Session(fixture) ?? throw new InvalidOperationException("Ad düzenleyicisi açık değil.");
        return (TextBlock)session.GetType().GetProperty("Anchor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    }

    private static TextBlock Error(InlineNameEditor editor) =>
        (TextBlock)typeof(InlineNameEditor).GetField("_error", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;

    private static string ErrorText(InlineNameEditor editor) =>
        Error(editor).Visibility == Visibility.Visible ? Error(editor).Text : string.Empty;

    private static Visibility ErrorVisibility(InlineNameEditor editor) => Error(editor).Visibility;

    private static void Realize(FrameworkElement element)
    {
        element.Measure(new System.Windows.Size(680, 500));
        element.Arrange(new Rect(0, 0, 680, 500));
        element.UpdateLayout();
    }

    private static void Press(UIElement target, Key key)
    {
        using var source = new HwndSource(new HwndSourceParameters("OmniSpot ad tuşları") { Width = 1, Height = 1, WindowStyle = 0 });
        var input = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(input);
        if (input.Handled) return;
        input.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(input);
    }

    private static object? Call(Fixture fixture, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args);
}
