using System.Windows;
using SmartFileLauncher.Core.Application.Indexing;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private IndexHealth? _indexHealth;

    private void HandleIndexHealth(IndexHealth health)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => ShowIndexHealth(health));
    }

    private void ShowIndexHealth(IndexHealth health)
    {
        if (_isPreparedForShutdown) return;
        var recovering = _indexHealth is { SearchAvailable: false };
        _indexHealth = health;
        if (health.SearchAvailable && _isIndexed && (recovering || !SearchBox.IsEnabled))
        {
            SearchBox.IsEnabled = true;
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) BeginSearch(SearchBox.Text);
            UpdateAIButtonState(_connectivityMonitor.IsConnected);
        }
        if (health.Issues.Count == 0)
        {
            IndexHealthBanner.Visibility = Visibility.Collapsed;
            return;
        }

        if (!health.SearchAvailable)
        {
            CancelCurrentSearch();
            SearchBox.IsEnabled = _startMenuCatalog != null;
            if (ResultsContainer.Visibility == Visibility.Visible)
            {
                ResultsContainer.Visibility = Visibility.Collapsed;
                SearchLatencyText.Visibility = Visibility.Collapsed;
                CancelSearchThumbnailRequests();
                ShowPanel(DesktopIconsScroll, animate: false);
            }
            ShowApplicationMatches(SearchBox.Text, refresh: true);
            UpdateAIButtonState(_connectivityMonitor.IsConnected);
        }
        var summary = IndexIssuePresentation.Summarize(health);
        IndexHealthTitle.Text = summary.Title;
        IndexHealthTitle.ToolTip = summary.Title;
        IndexHealthLocation.Text = summary.Location ?? "";
        IndexHealthLocationChip.ToolTip = summary.Location;
        IndexHealthLocationChip.Visibility = summary.Location is null ? Visibility.Collapsed : Visibility.Visible;
        IndexHealthEffect.Text = summary.Effect;
        IndexHealthCause.Text = summary.Cause is null ? "" : " " + summary.Cause;
        IndexHealthNextStep.Text = summary.NextStep;
        IndexHealthMore.Text = summary.OtherIssues is null ? "" : " " + summary.OtherIssues;
        IndexHealthRetryButton.Visibility = !health.SearchAvailable && health.Issues[0].Reason is
            IndexIssueReason.LocationUnavailable or IndexIssueReason.JournalChanging or
            IndexIssueReason.ServiceUnavailable or IndexIssueReason.Unknown or IndexIssueReason.StorageFull
            ? Visibility.Visible : Visibility.Collapsed;
        IndexHealthBanner.Visibility = Visibility.Visible;
        foreach (var issue in health.Issues)
            Log($"⚠️ {issue.Location} {issue.Operation}: {issue.Detail}");
    }

    private void IndexHealthSettings_Click(object sender, RoutedEventArgs e) => OpenSettings(showIndexing: true);

    private void IndexHealthRetry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _indexMaintenance.ScheduleRestart();
            ForceExit();
        }
        catch (Exception failure)
        {
            var issue = IndexIssue.FromException(failure, null, "OmniSpot’u yeniden başlatma");
            var health = new IndexHealth([issue], SearchAvailable: false);
            if (ModernDialog.Confirm(this, "OmniSpot yeniden başlatılamadı",
                    "OmniSpot’u kapatıp yeniden açın.", "Teknik ayrıntılar", DialogKind.Warning, "Kapat"))
                IndexIssuePresentation.ShowDetails(this, health);
        }
    }

    private void IndexHealthDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_indexHealth is { } health) IndexIssuePresentation.ShowDetails(this, health);
    }
}
