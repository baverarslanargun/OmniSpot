using System.IO;
using SmartFileLauncher.Core.Application.Connectivity;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Diagnostics;
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
    private readonly ApplicationStartupOptions _startupOptions;
    private readonly MeasurementRunLayout? _measurementRun;
    private bool _windowCreated;
    private bool _disposed;

    public ApplicationCompositionRoot()
        : this(ApplicationStartupOptions.Default, null)
    {
    }

    internal ApplicationCompositionRoot(
        ApplicationStartupOptions startupOptions,
        MeasurementRunLayout? measurementRun)
    {
        _startupOptions = startupOptions ?? throw new ArgumentNullException(nameof(startupOptions));
        _measurementRun = measurementRun;
        if (_startupOptions.IsMeasurement != (_measurementRun != null))
        {
            throw new ArgumentException(
                "Ölçüm profili ile koşum düzeni birlikte verilmelidir.",
                nameof(measurementRun));
        }

        _log = new ApplicationLog(
            _startupOptions.Profile == MeasurementProfile.ProductionCopy);
        var appDataDirectory = _measurementRun?.SettingsDirectory ??
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "OmniSpot");
        IStartupRegistration startupRegistration = _measurementRun == null
            ? new WindowsStartupRegistration(_log.Write)
            : new DisabledStartupRegistration();
        var settingsStore = new JsonSettingsStore(
            Path.Combine(appDataDirectory, "settings.json"));
        _settingsApplication = new SettingsApplicationService(
            settingsStore,
            startupRegistration);
        _settings = _startupOptions.Profile == MeasurementProfile.ProductionCopy
            ? settingsStore.LoadStrict()
            : settingsStore.Load();
        var tokenizer = new BasicTokenizer();
        var indexManager = _measurementRun == null
            ? new IndexManager(tokenizer)
            : IndexManager.CreateWithDatabasePath(
                _measurementRun.DatabasePath,
                tokenizer,
                enforceMeasurementPathSafety:
                    _startupOptions.Profile == MeasurementProfile.EmptyProduction,
                skipReparsePoints:
                    _startupOptions.Profile == MeasurementProfile.ProductionCopy);
        IIndexedLocationProvider locationProvider = _measurementRun == null
            ? new IndexedLocationProvider()
            : _startupOptions.Profile == MeasurementProfile.EmptyProduction
                ? new FixedIndexedLocationProvider(_measurementRun.CorpusPath!)
                : new IndexedLocationProvider();
        _indexLifecycle = new IndexLifecycleService(
            indexManager,
            locationProvider);
        var indexMaintenance = new IndexMaintenanceService(
            _indexLifecycle.DatabasePath);
        _indexMaintenance = _measurementRun == null
            ? indexMaintenance
            : new MeasurementIndexMaintenanceService(
                indexMaintenance,
                _startupOptions.ProfileName ?? "ölçüm",
                _startupOptions.Profile == MeasurementProfile.ProductionCopy);

        var scoring = new BasicScoringStrategy();
        var standardSearch = new SearchEngine(
            _indexLifecycle.CreateSearchState,
            tokenizer,
            scoring);
        var advancedSearch = new AdvancedSearchEngine(
            _indexLifecycle.CreateSearchState,
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
            _indexLifecycle.CreateSearchState);
        _thumbnails = new ThumbnailService(
            _log.Write,
            _measurementRun?.ThumbnailCachePath);
        _folderNavigation = new FolderNavigationService(
            new FolderBrowserService(
                skipReparsePoints:
                    _startupOptions.Profile is
                        MeasurementProfile.EmptyProduction or MeasurementProfile.ProductionCopy),
            () => _indexLifecycle.ReconciliationStatus,
            _indexLifecycle.EnsureSyncedAsync);
        _connectivity = new ConnectivityMonitor();
        var fileOperations = new FileOperationService(_indexLifecycle);
        _fileOperations = _measurementRun == null
            ? fileOperations
            : _startupOptions.Profile == MeasurementProfile.ProductionCopy
                ? new ReadOnlyFileOperationService(fileOperations)
                : new RootScopedFileOperationService(
                    fileOperations,
                    _measurementRun.CorpusPath!);
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
            _log,
            _startupOptions,
            _measurementRun);
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
        _measurementRun?.Dispose();
    }

    private sealed class DisabledStartupRegistration : IStartupRegistration
    {
        public void Apply(bool enabled)
        {
        }
    }

    private sealed class FixedIndexedLocationProvider(string rootPath)
        : IIndexedLocationProvider
    {
        public IndexLocations Resolve()
        {
            return new IndexLocations(rootPath, new[] { rootPath });
        }
    }

    internal sealed class MeasurementIndexMaintenanceService(
        IIndexMaintenanceService inner,
        string profileName,
        bool blockOpenIndexFolder)
        : IIndexMaintenanceService
    {
        public IndexStorageStatus GetStatus() => inner.GetStatus();

        public bool OpenIndexFolder()
        {
            if (blockOpenIndexFolder)
            {
                throw new InvalidOperationException(
                    $"İndeks klasörü {profileName} profilinde devre dışıdır.");
            }

            return inner.OpenIndexFolder();
        }

        public void ScheduleRebuild()
        {
            throw new InvalidOperationException(
                $"İndeks yeniden oluşturma {profileName} profilinde devre dışıdır.");
        }
    }
}
