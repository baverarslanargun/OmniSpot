using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    internal const int SearchPageSize = 200;
    private const double SearchPageScrollMargin = 600;
    private bool _searchHasMore;
    private bool _searchPageLoading;
    private int? _searchTotal;
    private CancellationTokenSource? _searchPageCancellation;

    private void InitializeSearchPaging()
    {
        foreach (var host in new System.Windows.Controls.ListBox[] { ResultsList, ResultsGrid })
            host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(SearchResultsScrolled));
    }

    private void ResetSearchPaging()
    {
        var previous = _searchPageCancellation;
        _searchPageCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
        _searchHasMore = false;
        _searchPageLoading = false;
        _searchTotal = null;
        SearchCountText.Visibility = Visibility.Hidden;
    }

    private void StartSearchPaging(SearchRequest request, SearchOutcome outcome, long version)
    {
        ResetSearchPaging();
        if (!outcome.IsPageable || outcome.Results.Count == 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _searchPageCancellation = cancellation;
        _searchHasMore = outcome.Results.Count >= request.MaxResults;
        if (!_searchHasMore)
        {
            _searchTotal = _searchResults.Count;
            ShowSearchCount();
            return;
        }
        ShowSearchCount();
        _ = CountSearchResultsAsync(request, outcome, version, cancellation);
    }

    private void RestoreSearchPaging(bool hasMore, int? total)
    {
        ResetSearchPaging();
        if (_completedSearchOutcome is not { IsPageable: true } || _searchResults.Count == 0) return;
        _searchPageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _searchHasMore = hasMore;
        _searchTotal = total;
        ShowSearchCount();
    }

    private void ShowSearchCount()
    {
        if (_searchResults.Count == 0)
        {
            SearchCountText.Visibility = Visibility.Hidden;
            return;
        }
        SearchCountText.Text = _searchTotal is { } total
            ? $"{total:N0} sonuç"
            : $"{_searchResults.Count:N0}+ sonuç";
        SearchCountText.Visibility = Visibility.Visible;
    }

    private async Task CountSearchResultsAsync(
        SearchRequest request, SearchOutcome outcome, long version, CancellationTokenSource cancellation)
    {
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var total = await _searchService.CountAsync(request, outcome, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsCurrentSearch(version) ||
                !ReferenceEquals(_searchPageCancellation, cancellation) || total is not { } count) return;
            _searchTotal = Math.Max(count, _searchResults.Count);
            if (_searchResults.Count >= _searchTotal) _searchHasMore = false;
            ShowSearchCount();
            Log($"🔢 Toplam eşleşme: {_searchTotal:N0} ({Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:0.0} ms)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentSearch(version) && !_isPreparedForShutdown)
                Log($"⚠️ Toplam eşleşme sayılamadı: {ex.Message}");
        }
    }

    private void SearchResultsScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!_searchHasMore || _searchPageLoading || sender is not FrameworkElement { IsVisible: true }) return;
        if (e.OriginalSource is not ScrollViewer scroll) return;
        if (scroll.ScrollableHeight <= 0 ||
            scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - SearchPageScrollMargin) return;
        _ = LoadNextSearchPageAsync();
    }

    internal async Task LoadNextSearchPageAsync()
    {
        if (!_searchHasMore || _searchPageLoading || _isPreparedForShutdown ||
            Volatile.Read(ref _currentSearch) != null ||
            _searchPageCancellation is not { } cancellation ||
            _completedSearchRequest is not { } request ||
            _completedSearchOutcome is not { } outcome) return;

        var version = Volatile.Read(ref _searchVersion);
        var offset = _searchResults.Count;
        var token = cancellation.Token;
        _searchPageLoading = true;
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var page = await Task.Run(async () =>
            {
                await _searchWorkGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return await _searchService.LoadPageAsync(request, outcome, offset, SearchPageSize, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _searchWorkGate.Release();
                }
            }, token);
            if (token.IsCancellationRequested || !IsCurrentSearch(version) ||
                !ReferenceEquals(_completedSearchOutcome, outcome) || _searchResults.Count != offset) return;

            var added = AppendSearchResults(page);
            _searchHasMore = page.Count >= SearchPageSize &&
                             (_searchTotal is not { } total || _searchResults.Count < total);
            ShowSearchCount();
            Log($"📄 Sonraki sayfa: +{added} sonuç (gösterilen {_searchResults.Count:N0}, " +
                $"{Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:0.0} ms)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchPageCancellation, cancellation)) _searchHasMore = false;
            if (!_isPreparedForShutdown) Log($"⚠️ Sonraki sayfa yüklenemedi: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_searchPageCancellation, cancellation)) _searchPageLoading = false;
        }

        if (ReferenceEquals(_searchPageCancellation, cancellation) && _searchHasMore)
            _ = Dispatcher.InvokeAsync(LoadNextPageIfNearEnd, DispatcherPriority.Background);
    }

    private void LoadNextPageIfNearEnd()
    {
        if (!_searchHasMore || _searchPageLoading || SearchResultsScroll() is not { IsVisible: true } scroll) return;
        if (scroll.ScrollableHeight > 0 &&
            scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - SearchPageScrollMargin) return;
        _ = LoadNextSearchPageAsync();
    }

    private int AppendSearchResults(IReadOnlyList<SearchResult> page)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in _searchResults) known.Add(current.FullPath);
        var added = new List<SearchResultViewModel>(page.Count);
        foreach (var result in page)
        {
            if (!known.Add(result.FullPath)) continue;
            var viewModel = CreateSearchResultViewModel(result);
            _searchResults.Add(viewModel);
            added.Add(viewModel);
        }
        foreach (var viewModel in added) _ = LoadSearchThumbnailAsync(viewModel);
        return added.Count;
    }

    private SearchResultViewModel CreateSearchResultViewModel(SearchResult result)
    {
        var viewModel = new SearchResultViewModel
        {
            Name = result.Name,
            FullPath = result.FullPath,
            Score = result.Score,
            Icon = result.IsDirectory ? "folder" : GetFileIcon(result.Name),
            IsDirectory = result.IsDirectory
        };
        if (result.IsDirectory) viewModel.SetFolderColors(result.Name);
        return viewModel;
    }
}
