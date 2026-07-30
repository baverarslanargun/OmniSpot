using System.IO;
using SmartFileLauncher.Core.Application.Connectivity;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using SmartFileLauncher.UI.Views;

namespace SmartFileLauncher.UI.Composition;

public sealed class ApplicationCompositionRoot : IDisposable
{
    private readonly ApplicationLog _log;
    private readonly AppSettings _settings;
    private readonly ISettingsApplicationService _settingsApplication;
    private readonly IIndexMaintenanceService _indexMaintenance;
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly ISearchApplicationService _search;
    private readonly ISearchDiagnosticsService _searchDiagnostics;
    private readonly IThumbnailService _thumbnails;
    private readonly IFolderNavigationService _folderNavigation;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IFileOperationService _fileOperations;
    private readonly IApplicationShellService _shell;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private bool _windowCreated;
    private bool _disposed;

    public ApplicationCompositionRoot()
    {
        _log = new ApplicationLog();
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "OmniSpot");
        _settingsApplication = new SettingsApplicationService(
            new JsonSettingsStore(
                Path.Combine(appDataDirectory, "settings.json")),
            new WindowsStartupRegistration(_log.Write));
        _settings = _settingsApplication.Load();
        var tokenizer = new BasicTokenizer();
        _indexLifecycle = new IndexLifecycleService(
            new IndexManager(tokenizer),
            new IndexedLocationProvider());
        _indexMaintenance = new IndexMaintenanceService(
            _indexLifecycle.DatabasePath);

        var scoring = new BasicScoringStrategy();
        var standardSearch = new SearchEngine(
            _indexLifecycle.CreateSearchSnapshot,
            tokenizer,
            scoring);
        var advancedSearch = new AdvancedSearchEngine(
            _indexLifecycle.CreateSearchSnapshot,
            tokenizer,
            scoring);
        var intentParser = new IntentParser(_log.Write);

        _search = new SearchApplicationService(
            standardSearch.Search,
            advancedSearch.Search,
            intentParser.ParseWithGroqAsync,
            intentParser.ParseIntent);
        _searchDiagnostics = new SearchDiagnosticsService(
            tokenizer,
            _indexLifecycle.GetTokenMatches);
        _thumbnails = new ThumbnailService(_log.Write);
        _folderNavigation = new FolderNavigationService(
            new FolderBrowserService(),
            () => _indexLifecycle.ReconciliationStatus,
            _indexLifecycle.EnsureSyncedAsync);
        _connectivity = new ConnectivityMonitor();
        _fileOperations = new FileOperationService(_indexLifecycle);
        _shell = new ApplicationShellService(
            new GlobalHotkeyService(),
            _log.Write);
        _mainWindowViewModel = new MainWindowViewModel();
    }

    public MainWindow CreateMainWindow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowCreated)
        {
            throw new InvalidOperationException("Ana pencere yalnız bir kez oluşturulabilir.");
        }

        _windowCreated = true;
        return new MainWindow(
            _mainWindowViewModel,
            _settings,
            _settingsApplication,
            _indexMaintenance,
            _indexLifecycle,
            _search,
            _searchDiagnostics,
            _thumbnails,
            _folderNavigation,
            _connectivity,
            _fileOperations,
            _shell,
            _log);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Dispose();
        _connectivity.Dispose();
        _indexLifecycle.Dispose();
    }
}
