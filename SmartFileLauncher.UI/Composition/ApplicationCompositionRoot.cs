using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.UI.Models;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;

namespace SmartFileLauncher.UI.Composition;

public sealed class ApplicationCompositionRoot : IDisposable
{
    private readonly ApplicationLog _log;
    private readonly AppSettings _settings;
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly ISearchApplicationService _search;
    private readonly IThumbnailService _thumbnails;
    private readonly IFolderBrowserService _folderBrowser;
    private readonly IFileOperationService _fileOperations;
    private readonly GlobalHotkeyService _hotkey;
    private bool _windowCreated;
    private bool _disposed;

    public ApplicationCompositionRoot()
    {
        _log = new ApplicationLog();
        _settings = AppSettings.Load();
        _indexLifecycle = new IndexLifecycleService(
            new IndexManager(new BasicTokenizer()),
            new IndexedLocationProvider());

        var tokenizer = new BasicTokenizer();
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
        _thumbnails = new ThumbnailService(_log.Write);
        _folderBrowser = new FolderBrowserService();
        _fileOperations = new FileOperationService(_indexLifecycle);
        _hotkey = new GlobalHotkeyService();
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
            _settings,
            _indexLifecycle,
            _search,
            _thumbnails,
            _folderBrowser,
            _fileOperations,
            _hotkey,
            _log);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.Dispose();
        _indexLifecycle.Dispose();
    }
}
