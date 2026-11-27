using System.Windows;
using SmartFileLauncher.Core.Application.Search;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const int SearchRefreshCostFactor = 10;
    private static readonly TimeSpan MinimumSearchRefreshPause = TimeSpan.FromSeconds(1);
    private SearchOutcome? _refreshSearchOutcome;
    private CancellationTokenSource? _searchRefreshPause;
    private bool _windowConcealed;

    private void InitializeSearchRefreshPace()
    {
        StateChanged += (_, _) => UpdateWindowConcealment();
        IsVisibleChanged += (_, _) => UpdateWindowConcealment();
    }

    private void UpdateWindowConcealment()
    {
        var concealed = !IsVisible || WindowState == WindowState.Minimized;
        if (concealed == _windowConcealed) return;
        _windowConcealed = concealed;
        if (!concealed) ResumeDeferredSearchRefresh();
    }

    private bool DefersSearchRefresh() => _windowConcealed || _searchRefreshPause != null;

    private void PauseSearchRefresh(TimeSpan turn)
    {
        EndSearchRefreshPause();
        var pause = turn * SearchRefreshCostFactor;
        if (pause < MinimumSearchRefreshPause) return;
        var pending = new CancellationTokenSource();
        _searchRefreshPause = pending;
        _ = WaitForSearchRefreshPauseAsync(pending, pause);
    }

    private async Task WaitForSearchRefreshPauseAsync(CancellationTokenSource pending, TimeSpan pause)
    {
        try
        {
            await Task.Delay(pause, SearchTimeProvider, pending.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_searchRefreshPause != pending) return;
        _searchRefreshPause = null;
        pending.Dispose();
        ResumeDeferredSearchRefresh();
    }

    private void EndSearchRefreshPause()
    {
        var pending = _searchRefreshPause;
        if (pending == null) return;
        _searchRefreshPause = null;
        pending.Cancel();
        pending.Dispose();
    }

    private void ResumeDeferredSearchRefresh()
    {
        if (_isPreparedForShutdown || DefersSearchRefresh() ||
            Volatile.Read(ref _searchRefreshRequested) == 0) return;
        _fileChangeRefresh.Request();
        SchedulePendingFileChange();
    }
}
