using System.Collections.ObjectModel;

namespace SmartFileLauncher.UI.ViewModels;

public sealed class MainWindowViewModel
{
    public ObservableCollection<DesktopIconViewModel> DesktopIcons { get; } = [];
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];
    public string DesktopPath { get; set; } = string.Empty;
    public string? CurrentFolderPath { get; set; }
    public List<string> IndexedRootPaths { get; set; } = [];
    public bool IsIndexed { get; set; }
    public bool IsNaturalLanguageMode { get; set; }
    public bool IsGridViewMode { get; set; }
    public string LastSearchQuery { get; set; } = string.Empty;
    public string? SelectedItemPath { get; set; }
    public string? ClipboardPath { get; set; }
    public bool IsCutOperation { get; set; }
    public string? HoveredItemPath { get; set; }
    public DesktopIconViewModel? HoveredItem { get; set; }
    public DesktopIconViewModel? CutItem { get; set; }
}
