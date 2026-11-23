using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using Button = System.Windows.Controls.Button;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const string NewFolderName = "Yeni klasör";
    private const string NewTextFileName = "Yeni Metin Belgesi.txt";
    private const string InvalidNameMessage = "Ad şu karakterleri içeremez: \\ / : * ? \" < > |";
    private NameEditSession? _nameEdit;
    private bool _fileChangesHeldForNameEdit;

    private sealed class NameEditSession(
        InlineNameEditor editor,
        AdornerLayer layer,
        TextBlock anchor,
        FrameworkElement container,
        Func<string, string?> commit,
        Action? dismissed,
        IInputElement? returnFocus)
    {
        internal InlineNameEditor Editor { get; } = editor;
        internal AdornerLayer Layer { get; } = layer;
        internal TextBlock Anchor { get; } = anchor;
        internal FrameworkElement Container { get; } = container;
        internal Func<string, string?> Commit { get; } = commit;
        internal Action? Dismissed { get; } = dismissed;
        internal IInputElement? ReturnFocus { get; } = returnFocus;
        internal bool Committing { get; set; }
        internal object? DataContext { get; } = container.DataContext;
    }

    private bool NameEditing => _nameEdit != null;

    private bool HoldFileChangesForNameEdit()
    {
        if (!NameEditing) return false;
        _fileChangesHeldForNameEdit = true;
        return true;
    }

    private void ReleaseHeldFileChanges()
    {
        if (!_fileChangesHeldForNameEdit || _isPreparedForShutdown) return;
        _fileChangesHeldForNameEdit = false;
        _fileChangeRefresh.Request();
        SchedulePendingFileChange();
    }

    private void InitializeNameEditing()
    {
        PreviewMouseDown += (_, e) =>
        {
            if (_nameEdit is { } session && !session.Editor.Contains(e.OriginalSource))
                EndNameEdit(cancelled: true, restoreFocus: false);
        };
    }

    private bool NameEditorContains(object? source) => _nameEdit?.Editor.Contains(source) == true;

    private bool BeginNameEdit(
        FrameworkElement container,
        string original,
        bool isDirectory,
        Func<string, string?> commit,
        Action? dismissed = null)
    {
        EndNameEdit(cancelled: true, restoreFocus: false);
        _selectionGlide?.Finish();
        container.ApplyTemplate();
        if (FindNameText(container) is not { } anchor || AdornerLayer.GetAdornerLayer(container) is not { } layer)
            return false;

        var selection = isDirectory ? original.Length : NameStemLength(original);
        var editor = new InlineNameEditor(container, anchor, original, selection);
        var session = new NameEditSession(editor, layer, anchor, container, commit, dismissed, Keyboard.FocusedElement);
        _nameEdit = session;
        CardNameReveal.Hold(container);
        anchor.Opacity = 0;
        layer.Add(editor);
        editor.Submitted += (_, text) => SubmitNameEdit(session, text);
        editor.Cancelled += (_, byKeyboard) =>
        {
            if (ReferenceEquals(_nameEdit, session)) EndNameEdit(cancelled: true, restoreFocus: byKeyboard);
        };
        container.Unloaded += OnNameEditContainerUnloaded;
        container.DataContextChanged += OnNameEditContainerDataContextChanged;
        return true;
    }

    private void OnNameEditContainerUnloaded(object sender, RoutedEventArgs e) => CancelNameEditFor(sender, dataContextChanged: false);

    private void OnNameEditContainerDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        CancelNameEditFor(sender, dataContextChanged: true);

    private void CancelNameEditFor(object sender, bool dataContextChanged)
    {
        if (_nameEdit is not { Committing: false } session || !ReferenceEquals(sender, session.Container)) return;
        if (dataContextChanged && ReferenceEquals(session.Container.DataContext, session.DataContext)) return;
        EndNameEdit(cancelled: true, restoreFocus: false);
    }

    private void SubmitNameEdit(NameEditSession session, string text)
    {
        if (!ReferenceEquals(_nameEdit, session)) return;
        var name = text.Trim();
        var error = ValidateItemName(name);
        if (error == null)
        {
            session.Committing = true;
            try { error = session.Commit(name); }
            finally { session.Committing = false; }
        }

        if (error != null)
        {
            if (ReferenceEquals(_nameEdit, session)) session.Editor.ShowError(error);
            return;
        }
        if (ReferenceEquals(_nameEdit, session)) EndNameEdit(cancelled: false, restoreFocus: true);
    }

    private void EndNameEdit(bool cancelled, bool restoreFocus)
    {
        if (_nameEdit is not { } session) return;
        _nameEdit = null;
        session.Editor.Close();
        session.Container.Unloaded -= OnNameEditContainerUnloaded;
        session.Container.DataContextChanged -= OnNameEditContainerDataContextChanged;
        session.Layer.Remove(session.Editor);
        session.Anchor.ClearValue(OpacityProperty);
        CardNameReveal.Release(session.Container, Animate);
        if (cancelled) session.Dismissed?.Invoke();
        ReleaseHeldFileChanges();
        if (!restoreFocus || _isPreparedForShutdown) return;
        if (session.ReturnFocus is UIElement { IsVisible: true, Focusable: true } target && target.Focus()) return;
        SearchBox.Focus();
    }

    private static string? ValidateItemName(string name)
    {
        if (name.Length == 0) return "Ad boş olamaz.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return InvalidNameMessage;
        if (name is "." or "..") return "Bu ad kullanılamaz.";
        return null;
    }

    private static int NameStemLength(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || extension.Length == name.Length
            ? name.Length
            : name.Length - extension.Length;
    }

    private static TextBlock? FindNameText(DependencyObject root)
    {
        if (root is TextBlock text &&
            (BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "Name" ||
             BindingOperations.GetBinding(text, Services.FileNameTrimming.FileNameProperty)?.Path?.Path == "Name"))
            return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindNameText(VisualTreeHelper.GetChild(root, i)) is { } match) return match;
        }
        return null;
    }

    private static Button? FindItemButton(DependencyObject root, string path)
    {
        if (root is Button { Tag: string tag } button && PathsEqual(tag, path)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindItemButton(VisualTreeHelper.GetChild(root, i), path) is { } match) return match;
        }
        return null;
    }

    private void RenameItem(string path)
    {
        if (FileOperationService.IsVolumeRoot(path))
        {
            ShowErrorToast("Sürücüler OmniSpot içinden yeniden adlandırılamaz.");
            return;
        }
        if (TryBeginRename(path)) return;
        if (!string.IsNullOrEmpty(Path.GetFileName(path.TrimEnd('\\', '/')))) RenameItemWithDialog(path);
    }

    private bool TryBeginRename(string path)
    {
        if (NameEditing || _isPreparedForShutdown) return false;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) return false;
        var inPane = KeyboardInDualPane;
        var container = inPane ? PaneItemContainer(path) : MainItemContainer(path);
        if (container == null) return false;
        var isDirectory = _fileOperations.GetItemKind(path) == FileItemKind.Directory;
        return BeginNameEdit(container, name, isDirectory, newName => CommitRename(path, newName, inPane));
    }

    private FrameworkElement? MainItemContainer(string path)
    {
        if (SearchSurfaceVisible)
        {
            var index = IndexOfSearchResult(path);
            if (index < 0) return null;
            if (_isGridViewMode)
            {
                if (_searchRowsSyncQueued) SyncSearchRows();
                if (index / SearchGridColumns >= _searchRows.Count) return null;
                ResultsGrid.ScrollIntoView(_searchRows[index / SearchGridColumns]);
                ResultsGrid.UpdateLayout();
            }
            else
            {
                ResultsList.ScrollIntoView(_searchResults[index]);
                ResultsList.UpdateLayout();
            }
            return FileSearchFocusTarget(index);
        }

        if (DesktopIconsScroll.Visibility != Visibility.Visible) return null;
        var position = -1;
        for (var i = 0; i < _desktopIcons.Count; i++)
        {
            if (PathsEqual(_desktopIcons[i].FullPath, path)) { position = i; break; }
        }
        if (position < 0) return null;
        if (_desktopRowsRebuildQueued) RebuildDesktopRows();
        var row = position / DesktopColumns;
        if (row >= _desktopRows.Count) return null;
        DesktopIcons.ScrollIntoView(_desktopRows[row]);
        DesktopIcons.UpdateLayout();
        return DesktopIcons.ItemContainerGenerator.ContainerFromIndex(row) is DependencyObject rowContainer
            ? FindItemButton(rowContainer, path)
            : null;
    }

    private FrameworkElement? PaneItemContainer(string path)
    {
        var entry = _paneEntries.FirstOrDefault(item => PathsEqual(item.Path, path));
        if (entry == null) return null;
        DualPaneList.ScrollIntoView(entry);
        DualPaneList.UpdateLayout();
        return DualPaneList.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement;
    }

    private string? CommitRename(string path, string newName, bool inPane)
    {
        var oldName = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.Equals(newName, oldName, StringComparison.Ordinal)) return null;

        RenameOperationResult result;
        try
        {
            result = _fileOperations.Rename(path, newName);
        }
        catch (Exception ex)
        {
            Log($"❌ Adlandırma hatası: {ex.Message}");
            return DescribeNameFailure(Path.GetDirectoryName(path) ?? string.Empty, newName, path, ex);
        }

        if (result.SourceKind == FileItemKind.Missing)
        {
            ShowErrorToast($"“{oldName}” artık bulunamıyor.");
            _ = ProcessFileChangeAsync();
            return null;
        }

        var wasSelected = ResultsList.SelectedItem is SearchResultViewModel selected && PathsEqual(selected.FullPath, path);
        ApplyRenameToViews(path, result.DestinationPath, newName);
        if (wasSelected && IndexOfSearchResult(result.DestinationPath) is var index and >= 0)
            SelectFileSearchResult(index);
        if (_selectedItemPath != null && PathsEqual(_selectedItemPath, path)) _selectedItemPath = result.DestinationPath;
        if (_hoveredItemPath != null && PathsEqual(_hoveredItemPath, path)) _hoveredItemPath = result.DestinationPath;
        Log($"✏️ Adlandırıldı: {oldName} → {newName}");
        if (inPane || DualPaneOpen) _ = RefreshDualPaneAsync();
        return null;
    }

    private static string DescribeNameFailure(string folder, string name, string? source, Exception error)
    {
        var destination = Path.Combine(folder, name);
        if (error is IOException && (source == null || !PathsEqual(destination, source)) &&
            (File.Exists(destination) || Directory.Exists(destination)))
            return "Bu adla bir öğe zaten var.";
        if (error is UnauthorizedAccessException)
            return "Bu konumda değişiklik yapma izniniz yok.";
        return error.Message;
    }

    private void BeginNewItem(bool folder)
    {
        if (_isPreparedForShutdown) return;
        EndNameEdit(cancelled: true, restoreFocus: false);
        var target = _currentFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var name = folder ? NewFolderName : NewTextFileName;
        if (_currentFolderPath == null || SearchSurfaceVisible || DesktopIconsScroll.Visibility != Visibility.Visible)
        {
            CreateNewItemDirectly(target, name, folder);
            return;
        }

        var placeholder = new DesktopIconViewModel
        {
            Name = name,
            FullPath = Path.Combine(target, "⁣" + Guid.NewGuid().ToString("N")),
            IsDirectory = folder,
            Icon = folder ? "folder" : GetFileIcon(name)
        };
        if (folder) placeholder.SetFolderColors(name);
        var wasEmpty = _folderLooksEmpty;
        SetFolderLooksEmpty(false);
        _desktopIcons.Insert(0, placeholder);

        void Discard()
        {
            _desktopIcons.Remove(placeholder);
            if (wasEmpty && _desktopIcons.Count == 0) SetFolderLooksEmpty(true);
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_isPreparedForShutdown || !_desktopIcons.Contains(placeholder)) return;
            var container = MainItemContainer(placeholder.FullPath);
            if (container != null &&
                BeginNameEdit(container, name, folder, newName => CommitNewItem(placeholder, target, newName, folder), Discard))
                return;
            Discard();
            CreateNewItemDirectly(target, name, folder);
        });
    }

    private string? CommitNewItem(DesktopIconViewModel placeholder, string target, string name, bool folder)
    {
        var requested = Path.Combine(target, name);
        var isDefault = string.Equals(name, folder ? NewFolderName : NewTextFileName, StringComparison.Ordinal);
        if (!isDefault && (File.Exists(requested) || Directory.Exists(requested)))
            return "Bu adla bir öğe zaten var.";

        string created;
        try
        {
            created = folder ? _fileOperations.CreateFolder(target, name) : _fileOperations.CreateTextFile(target, name);
        }
        catch (Exception ex)
        {
            Log($"❌ {(folder ? "Klasör" : "Dosya")} oluşturma hatası: {ex.Message}");
            return DescribeNameFailure(target, name, null, ex);
        }

        placeholder.Name = Path.GetFileName(created);
        placeholder.FullPath = created;
        Log($"{(folder ? "📁 Yeni klasör" : "📄 Yeni dosya")} oluşturuldu: {placeholder.Name}");
        RefreshCurrentFolder();
        return null;
    }

    private void CreateNewItemDirectly(string target, string name, bool folder)
    {
        try
        {
            var created = folder ? _fileOperations.CreateFolder(target, name) : _fileOperations.CreateTextFile(target, name);
            var place = _currentFolderPath == null ? "Masaüstü'nde" : "bu klasörde";
            ShowToast($"“{Path.GetFileName(created)}” {place} oluşturuldu.");
            Log($"{(folder ? "📁 Yeni klasör" : "📄 Yeni dosya")} oluşturuldu: {created}");
            RefreshCurrentFolder();
        }
        catch (Exception ex)
        {
            Log($"❌ {(folder ? "Klasör" : "Dosya")} oluşturma hatası: {ex.Message}");
            ShowErrorToast($"{(folder ? "Klasör" : "Dosya")} oluşturulamadı: {DescribeNameFailure(target, name, null, ex)}");
        }
    }

    private void RenameWhenShown(string path, Action? unavailable)
    {
        if (TryBeginRename(path)) return;
        var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        var shownFolder = _currentFolderPath ?? _desktopPath;
        if (SearchSurfaceVisible || DesktopIconsScroll.Visibility != Visibility.Visible ||
            parent == null || string.IsNullOrEmpty(shownFolder) || !PathsEqual(parent, shownFolder))
        {
            unavailable?.Invoke();
            return;
        }

        var attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_isPreparedForShutdown || NameEditing || TryBeginRename(path))
            {
                timer.Stop();
                return;
            }
            if (++attempts < 20) return;
            timer.Stop();
            unavailable?.Invoke();
        };
        timer.Start();
    }
}
