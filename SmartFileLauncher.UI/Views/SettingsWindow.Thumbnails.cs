using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow
{
    private readonly ObservableCollection<string> _thumbnailFolders = new();
    private readonly DispatcherTimer _thumbnailPreviewTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<long>? _thumbnailCacheBytes;
    private bool _thumbnailUiReady;
    private SystemMemorySnapshot _thumbnailMemory;
    private double _thumbnailRequestedPercent;

    private void InitializeThumbnailSettings()
    {
        _thumbnailUiReady = false;
        ThumbnailEnabled.IsChecked = _settings.ThumbnailPreviewsEnabled;
        ThumbnailModeRatio.IsChecked = _settings.ThumbnailCacheUseRamRatio;
        ThumbnailModeCount.IsChecked = !_settings.ThumbnailCacheUseRamRatio;
        ThumbnailCount.Text = _settings.ThumbnailCacheMaxCount.ToString(CultureInfo.CurrentCulture);
        ThumbnailIdle.Text = ThumbnailMemoryPolicy.IdleSeconds(_settings).ToString(CultureInfo.CurrentCulture);
        _thumbnailMemory = SystemMemoryReader.Read();
        var budget = ThumbnailMemoryPolicy.Calculate(_settings, _thumbnailMemory);
        ThumbnailRatio.Maximum = Math.Max(0, budget.SafeMaxPercent);
        _thumbnailRequestedPercent = double.IsFinite(_settings.ThumbnailCacheRamPercent)
            ? Math.Clamp(_settings.ThumbnailCacheRamPercent, 0, 2) : 0;
        ThumbnailRatio.Value = Math.Min(_thumbnailRequestedPercent, ThumbnailRatio.Maximum);
        _thumbnailFolders.Clear();
        foreach (var path in ThumbnailMemoryPolicy.Configure(_settings, _thumbnailMemory).PinnedFolders)
            _thumbnailFolders.Add(path);
        ThumbnailFolders.ItemsSource = _thumbnailFolders;
        _thumbnailUiReady = true;
        UpdateThumbnailPreview();
    }

    private void StartThumbnailPreview()
    {
        _thumbnailPreviewTimer.Tick += RefreshThumbnailMemory;
        Loaded += (_, _) => _thumbnailPreviewTimer.Start();
        Closed += (_, _) => _thumbnailPreviewTimer.Stop();
    }

    private void RefreshThumbnailMemory(object? sender, EventArgs e)
    {
        _thumbnailMemory = SystemMemoryReader.Read();
        UpdateThumbnailPreview();
    }

    private AppSettings ThumbnailDraft() => new()
    {
        ThumbnailPreviewsEnabled = ThumbnailEnabled.IsChecked == true,
        ThumbnailCacheUseRamRatio = ThumbnailModeRatio.IsChecked == true,
        ThumbnailCacheMaxCount = long.TryParse(ThumbnailCount.Text, out var count)
            ? (int)Math.Clamp(count, 0, int.MaxValue) : int.MaxValue,
        ThumbnailCacheRamPercent = _thumbnailRequestedPercent
    };

    private void ThumbnailSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_thumbnailUiReady) return;
        if (ReferenceEquals(sender, ThumbnailRatio)) _thumbnailRequestedPercent = ThumbnailRatio.Value;
        UpdateThumbnailPreview();
    }

    private void UpdateThumbnailPreview()
    {
        var draft = ThumbnailDraft();
        var budget = ThumbnailMemoryPolicy.Calculate(draft, _thumbnailMemory);
        _thumbnailUiReady = false;
        ThumbnailRatio.Maximum = Math.Max(0, budget.SafeMaxPercent);
        ThumbnailRatio.Value = Math.Min(_thumbnailRequestedPercent, ThumbnailRatio.Maximum);
        _thumbnailUiReady = true;
        ThumbnailManualPanel.Visibility = draft.ThumbnailCacheUseRamRatio ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailRatioPanel.Visibility = draft.ThumbnailCacheUseRamRatio ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailControls.IsEnabled = draft.ThumbnailPreviewsEnabled;
        ThumbnailRatioValue.Text = $"İstenen: %{_thumbnailRequestedPercent:N3}";
        ThumbnailBudget.Text = $"Uygulanacak sınır: {budget.Count:N0} görsel · {FormatThumbnailBytes(budget.Bytes)}";
        ThumbnailSafeLimit.Text = $"Güvenli üst sınır: {budget.SafeMaxCount:N0} görsel / {FormatThumbnailBytes(budget.SafeMaxBytes)} (%{budget.SafeMaxPercent:N3}). Boş RAM azaldığında otomatik düşer.";
        var total = Math.Max(1, _thumbnailMemory.TotalBytes);
        var used = Math.Clamp(total - _thumbnailMemory.AvailableBytes, 0, total);
        var process = Math.Clamp(_thumbnailMemory.ProcessBytes, 0, used);
        var currentCache = Math.Clamp(_thumbnailCacheBytes?.Invoke() ?? 0, 0, process);
        var other = used - process;
        var appBase = process - currentCache;
        var preview = Math.Min(budget.Bytes, total - other - appBase);
        var free = Math.Max(0, total - other - appBase - preview);
        ThumbnailOtherColumn.Width = new(other, GridUnitType.Star);
        ThumbnailAppColumn.Width = new(appBase, GridUnitType.Star);
        ThumbnailCacheColumn.Width = new(preview, GridUnitType.Star);
        ThumbnailFreeColumn.Width = new(free, GridUnitType.Star);
        ThumbnailOtherLabel.Text = $"Diğer kullanım: {FormatThumbnailBytes(other)}";
        ThumbnailAppLabel.Text = $"OmniSpot, cache hariç: ≈ {FormatThumbnailBytes(appBase)}";
        ThumbnailCacheLabel.Text = $"Thumbnail bütçesi: {FormatThumbnailBytes(preview)}";
        ThumbnailFreeLabel.Text = $"Kalan: ≈ {FormatThumbnailBytes(free)}";
        ThumbnailMemoryTitle.Text = _thumbnailMemory.TotalBytes == 0
            ? "RAM bilgisi okunamadı; önbellek güvenli olarak kapalı."
            : $"RAM dağılımı · {FormatThumbnailBytes(total)} toplam";
    }

    private static string FormatThumbnailBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):N2} GB" : $"{bytes / (1024d * 1024):N1} MB";

    private bool SaveThumbnailSettings()
    {
        _thumbnailMemory = SystemMemoryReader.Read();
        var draft = ThumbnailDraft();
        var budget = ThumbnailMemoryPolicy.Calculate(draft, _thumbnailMemory);
        if (!int.TryParse(ThumbnailIdle.Text, out var seconds) || seconds < 5 || seconds > 86400)
        {
            ThumbnailValidation.Text = "Hareketsizlik süresi 5 ile 86.400 saniye arasında olmalı.";
            ThumbnailIdle.Focus();
            return false;
        }
        if (draft.ThumbnailPreviewsEnabled && !draft.ThumbnailCacheUseRamRatio && (!int.TryParse(ThumbnailCount.Text, out var count)
            || count < 1 || count > budget.SafeMaxCount))
        {
            ThumbnailValidation.Text = $"Bu bilgisayarın şu anki güvenli sınırı {budget.SafeMaxCount:N0} görsel. 1 ile bu sınır arasında bir sayı girin.";
            ThumbnailCount.Focus();
            return false;
        }
        _settings.ThumbnailPreviewsEnabled = draft.ThumbnailPreviewsEnabled;
        _settings.ThumbnailCacheUseRamRatio = draft.ThumbnailCacheUseRamRatio;
        if (int.TryParse(ThumbnailCount.Text, out var savedCount) && savedCount > 0 && savedCount <= budget.SafeMaxCount)
            _settings.ThumbnailCacheMaxCount = savedCount;
        _settings.ThumbnailCacheRamPercent = draft.ThumbnailCacheRamPercent;
        _settings.ThumbnailIdleSeconds = seconds;
        _settings.ThumbnailPinnedFolders = _thumbnailFolders.ToList();
        return true;
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var delta)) return;
        if (FindName(parts[0]) is not System.Windows.Controls.TextBox box) return;
        var current = long.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : 0;
        var min = ReferenceEquals(box, ThumbnailIdle) ? 5 : 1;
        box.Text = Math.Max(min, current + delta).ToString(CultureInfo.CurrentCulture);
        if (ReferenceEquals(box, ThumbnailIdle)) return;
        UpdateThumbnailPreview();
    }

    private void AddThumbnailFolder_Click(object sender, RoutedEventArgs e)
    {
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Küçük resimleri hızlı yüklenecek klasörü seçin",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var path = ThumbnailMemoryPolicy.NormalizeFolder(picker.SelectedPath);
        if (path == null || _thumbnailFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        if (!_settings.HideThumbnailPinWarning)
        {
            var warning = new ThumbnailPinConfirmationWindow(path) { Owner = this };
            if (warning.ShowDialog() != true) return;
            _settings.HideThumbnailPinWarning = warning.HideFutureWarnings;
        }
        _thumbnailFolders.Add(path);
        ThumbnailFolders.SelectedItem = path;
    }

    private void RemoveThumbnailFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ThumbnailFolders.SelectedItem is string path) _thumbnailFolders.Remove(path);
    }
}
