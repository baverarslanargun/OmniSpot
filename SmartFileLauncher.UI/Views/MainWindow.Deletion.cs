using System.IO;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private DeletionRecord? _lastDeletion;

    internal Func<IReadOnlyList<string>, DateTime, RecycleRestoreResult> RestoreFromRecycleBin { get; set; } =
        RecycleBinRestore.Restore;

    private sealed class DeletionRecord(IReadOnlyList<string> paths, DateTime startedUtc)
    {
        internal IReadOnlyList<string> Paths { get; } = paths;
        internal DateTime StartedUtc { get; } = startedUtc;
        internal bool Restoring { get; set; }
    }

    private void DeleteWithUndo(IReadOnlyList<string> paths, bool fromPane)
    {
        if (paths.Count == 0 || _isPreparedForShutdown) return;
        EndNameEdit(cancelled: true, restoreFocus: false);
        var startedUtc = DateTime.UtcNow;
        var deleted = new List<string>();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            if (FileOperationService.IsVolumeRoot(path))
            {
                failures.Add($"{path}: Sürücü kökü silinemez.");
                continue;
            }
            try
            {
                if (_fileOperations.DeleteToRecycleBin(path) != FileItemKind.Missing) deleted.Add(path);
                RemoveItemFromView(path);
                RemoveSearchResult(path);
            }
            catch (OperationCanceledException)
            {
                Log($"↩️ Kalıcı silme iptal edildi: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        foreach (var failure in failures) Log($"❌ Silme hatası: {failure}");
        if (deleted.Count > 0) Log($"🗑️ {deleted.Count} öğe Geri Dönüşüm Kutusu'na taşındı");
        if (fromPane || DualPaneOpen) _ = RefreshDualPaneAsync();
        UpdateSelectionBar();

        if (deleted.Count == 0)
        {
            if (failures.Count > 0)
                ShowErrorToast(failures.Count == 1
                    ? $"Silinemedi. {failures[0]}"
                    : $"{failures.Count} öğe silinemedi (ayrıntı tanılama günlüğünde).");
            return;
        }

        var record = new DeletionRecord(deleted, startedUtc);
        _lastDeletion = record;
        var message = deleted.Count == 1
            ? $"“{Path.GetFileName(deleted[0].TrimEnd('\\', '/'))}” Geri Dönüşüm Kutusu'na taşındı"
            : $"{deleted.Count} öğe Geri Dönüşüm Kutusu'na taşındı";
        if (failures.Count > 0) message += $"; {failures.Count} öğe silinemedi";
        ShowToast(message + ".", error: failures.Count > 0, "Geri al", () => UndoDeletion(record), UndoShortcut);
    }

    private void UndoDeletion(DeletionRecord record)
    {
        if (!ReferenceEquals(_lastDeletion, record) || record.Restoring || _isPreparedForShutdown) return;
        record.Restoring = true;
        ShowToast(record.Paths.Count == 1 ? "Geri alınıyor…" : $"{record.Paths.Count} öğe geri alınıyor…");
        var restore = RestoreFromRecycleBin;
        var worker = new Thread(() =>
        {
            RecycleRestoreResult result;
            string? failure = null;
            try { result = restore(record.Paths, record.StartedUtc); }
            catch (Exception ex)
            {
                result = RecycleRestoreResult.Empty(record.Paths);
                failure = ex.Message;
            }
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.InvokeAsync(() => FinishUndo(record, result, failure));
        }) { IsBackground = true, Name = "OmniSpot geri al" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void FinishUndo(DeletionRecord record, RecycleRestoreResult result, string? failure)
    {
        if (ReferenceEquals(_lastDeletion, record)) _lastDeletion = null;
        if (_isPreparedForShutdown) return;
        foreach (var path in result.Restored) Log($"↩️ Geri alındı: {path}");
        foreach (var path in result.NotFound) Log($"❌ Geri Dönüşüm Kutusu'nda bulunamadı: {path}");
        foreach (var path in result.Occupied) Log($"❌ Aynı adlı öğe var, geri alınmadı: {path}");
        if (failure != null) Log($"❌ Geri alma hatası: {failure}");

        if (result.Restored.Count > 0)
        {
            if (SearchSurfaceVisible) _ = RefreshSearchResultsAsync();
            else RefreshCurrentFolder();
            if (DualPaneOpen) _ = RefreshDualPaneAsync();
        }

        var total = record.Paths.Count;
        var restored = result.Restored.Count;
        if (restored == total)
        {
            ShowToast(total == 1
                ? $"“{Path.GetFileName(record.Paths[0].TrimEnd('\\', '/'))}” geri alındı."
                : $"{total} öğe geri alındı.");
            return;
        }

        var reason = failure != null
            ? failure
            : result.Occupied.Count > 0
                ? "Aynı adlı öğe artık yerinde duruyor."
                : "Öğe Geri Dönüşüm Kutusu'nda bulunamadı.";
        ShowErrorToast(restored == 0
            ? $"Geri alınamadı. {reason}"
            : $"{total} öğeden {restored} tanesi geri alındı. {reason}");
    }
}
