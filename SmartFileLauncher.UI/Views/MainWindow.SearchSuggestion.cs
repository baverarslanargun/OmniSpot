using System.Diagnostics;
using System.Windows;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private static readonly TimeSpan SearchSuggestionDelay = TimeSpan.FromMilliseconds(350);
    private CancellationTokenSource? _searchSuggestionCancellation;
    private SearchRequest? _searchSuggestionRequest;
    private string? _searchSuggestion;
    private int? _filterHiddenCount;

    internal string? CurrentSearchSuggestion => NoResultsSuggestionButton.Visibility == Visibility.Visible ? _searchSuggestion : null;

    internal int? CurrentFilterHiddenCount => NoResultsClearFilterButton.Visibility == Visibility.Visible ? _filterHiddenCount : null;

    private void ResetSearchSuggestion()
    {
        var previous = _searchSuggestionCancellation;
        _searchSuggestionCancellation = null;
        previous?.Cancel();
        previous?.Dispose();
        _searchSuggestionRequest = null;
        _searchSuggestion = null;
        _filterHiddenCount = null;
        NoResultsSuggestionButton.Visibility = Visibility.Collapsed;
        NoResultsClearFilterButton.Visibility = Visibility.Collapsed;
    }

    private void StartSearchSuggestion(SearchRequest request, SearchOutcome outcome, long version)
    {
        if (outcome.Results.Count == 0 && (_searchSuggestion != null || _filterHiddenCount != null) &&
            request == _searchSuggestionRequest) return;
        ResetSearchSuggestion();
        if (request.NaturalLanguageMode || !outcome.IsPageable || outcome.Results.Count > 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _searchSuggestionCancellation = cancellation;
        _searchSuggestionRequest = request;
        _ = SuggestSearchAsync(request, outcome, version, cancellation);
    }

    private async Task SuggestSearchAsync(SearchRequest request, SearchOutcome outcome, long version, CancellationTokenSource cancellation)
    {
        bool Stale() => cancellation.IsCancellationRequested || !IsCurrentSearch(version) ||
                        !ReferenceEquals(_searchSuggestionCancellation, cancellation) || _searchResults.Count > 0;
        try
        {
            await Task.Delay(SearchSuggestionDelay, SearchTimeProvider, cancellation.Token);
            if (Stale()) return;
            var timestamp = Stopwatch.GetTimestamp();
            if (request.View.Filter.IsActive)
            {
                var hidden = await _searchService.CountAsync(request with { Filter = ItemFilter.None }, outcome, cancellation.Token);
                if (Stale()) return;
                if (hidden is > 0)
                {
                    _filterHiddenCount = hidden;
                    NoResultsClearFilterCount.Text = $"· {hidden:N0} sonuç";
                    NoResultsClearFilterButton.Visibility = Visibility.Visible;
                    Log($"💡 Filtre {hidden:N0} sonucu gizliyor ({Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:0.0} ms)");
                    return;
                }
            }

            var suggestion = await _searchService.SuggestAsync(request, outcome, cancellation.Token);
            if (Stale() || string.IsNullOrWhiteSpace(suggestion) ||
                string.Equals(suggestion, request.Query.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            _searchSuggestion = suggestion;
            NoResultsSuggestionText.Text = suggestion;
            NoResultsSuggestionButton.Visibility = Visibility.Visible;
            Log($"💡 Arama önerisi hazır ({Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds:0.0} ms)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentSearch(version) && !_isPreparedForShutdown)
                Log($"⚠️ Arama önerisi hazırlanamadı: {ex.Message}");
        }
    }

    private bool ApplySearchSuggestion()
    {
        if (CurrentSearchSuggestion is not { } suggestion) return false;
        ResetSearchSuggestion();
        SearchBox.Text = suggestion;
        SearchBox.CaretIndex = suggestion.Length;
        return true;
    }

    private void NoResultsSuggestion_Click(object sender, RoutedEventArgs e) => ApplySearchSuggestion();

    private void NoResultsClearFilter_Click(object sender, RoutedEventArgs e)
    {
        ResetSearchSuggestion();
        ApplyFilterChange(ItemFilter.None);
    }
}
