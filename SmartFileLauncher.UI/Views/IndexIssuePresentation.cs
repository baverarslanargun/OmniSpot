using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SmartFileLauncher.Core.Application.Indexing;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using FontFamily = System.Windows.Media.FontFamily;
using TextBox = System.Windows.Controls.TextBox;

namespace SmartFileLauncher.UI.Views;

internal sealed record IndexIssueSummary(string Title, string? Location, string Effect, string? Cause, string NextStep,
    int OtherIssueCount)
{
    internal string? OtherIssues => OtherIssueCount > 0 ? $"{OtherIssueCount} başka sorun ayrıntılarda." : null;

    internal string Message =>
        (Location is null ? "" : $"{Location} · ") + Effect + " " +
        (Cause is null ? NextStep : $"{Cause} {NextStep}") +
        (OtherIssues is null ? "" : " " + OtherIssues);
}

internal static class IndexIssuePresentation
{
    internal static IndexIssueSummary Summarize(IndexHealth health)
    {
        var issue = health.Issues[0];
        var title = !health.SearchAvailable ? "Arama indeksi kullanılamıyor"
            : issue.LocationSkipped ? "Bir konum indekslenemedi"
            : issue.Reason == IndexIssueReason.IncompleteScan ? "Bazı klasörler taranamadı"
            : issue.Reason is IndexIssueReason.IndexNotSaved or IndexIssueReason.StorageFull ? "İndeks diske kaydedilemedi"
            : "İndeks güncellemesi bekliyor";
        var (cause, nextStep) = issue.Reason switch
        {
            IndexIssueReason.InvalidLocation => ((string?)null, "İndeks konumunu kontrol edin veya başka bir konum seçin."),
            IndexIssueReason.LocationUnavailable => ("Konum açılamadı.", "Sürücü veya ağ bağlantısını kontrol edin ve yeniden deneyin."),
            IndexIssueReason.AccessDenied => ("Konum okunamadı.", "Erişim isteyin veya başka bir konum seçin."),
            IndexIssueReason.UnsupportedFileSystem => ("Bu konum MFT ile taranamıyor.", "Ayarlardan Klasör taraması’nı seçin."),
            IndexIssueReason.JournalInactive => ("Windows’un dosya değişiklik günlüğü etkin değil.", "Ayarlar’da bu disk için Otomatik veya Klasör taraması’nı seçin ya da günlüğü etkinleştirin."),
            IndexIssueReason.JournalChanging => ("Windows dosya değişiklik günlüğünü kaldırıyor.", "İşlem tamamlanınca yeniden deneyin."),
            IndexIssueReason.JournalHistoryLost => ("Windows’un değişiklik kayıtları eksik.", "Yeniden tarama tamamlanınca sonuçlar güncellenir."),
            IndexIssueReason.ServiceUnavailable => ("OmniSpot hizmetine ulaşılamıyor.", "Hizmetin çalıştığını kontrol edin ve yeniden deneyin."),
            IndexIssueReason.ServiceVersionMismatch => ("Uygulama ile OmniSpot hizmetinin sürümleri uyuşmuyor.", "İkisini aynı sürüme güncelleyin."),
            IndexIssueReason.ServiceUntrusted => ("OmniSpot hizmetinin kimliği doğrulanamadı.", "Kurulumu kontrol edin."),
            IndexIssueReason.StorageFull => (null, "İndeksin kaydedildiği diskte yer açın ve yeniden deneyin."),
            IndexIssueReason.IndexNotSaved => (null, "İndeks klasörünün yazılabilir olduğunu kontrol edin."),
            IndexIssueReason.IncompleteScan => (null, "Etkilenen klasörlerin erişimini kontrol edin, ardından indeksi yeniden oluşturun."),
            _ => ("İndeks işlemi tamamlanamadı.", "Yeniden deneyin. Sorun sürerse teknik ayrıntıları destek için kopyalayın.")
        };
        var effect = !health.SearchAvailable ? "Arama şu anda kullanılamıyor."
            : issue.LocationSkipped ? "Bu konum aramada yok; diğer konumlarda arama açık."
            : issue.Reason == IndexIssueReason.IncompleteScan ? "Arama sonuçları eksik olabilir."
            : issue.Reason is IndexIssueReason.IndexNotSaved or IndexIssueReason.StorageFull
                ? "Arama açık, ancak sonraki açılışta yeniden tarama gerekebilir."
                : "Arama sonuçları güncel olmayabilir.";
        return new(title, issue.Location, effect, cause, nextStep, health.Issues.Count - 1);
    }

    internal static (string Title, string Message) Describe(IndexHealth health)
    {
        var summary = Summarize(health);
        return (summary.Title, summary.Message);
    }

    internal static string FormatDetails(IndexHealth health) =>
        (health.InitialStartup ? "Aşama: İlk açılış" + Environment.NewLine + Environment.NewLine : "") +
        string.Join(Environment.NewLine + Environment.NewLine,
        health.Issues.Select(issue => string.Join(Environment.NewLine,
            new[]
            {
                $"Konum: {issue.Location ?? "Belirtilmedi"}",
                $"İşlem: {issue.Operation}",
                $"Neden: {issue.Reason}",
                issue.NativeErrorCode is 1179 ? "Windows hata kodu: 1179 (ERROR_JOURNAL_NOT_ACTIVE)"
                    : issue.NativeErrorCode is { } code ? $"Windows hata kodu: {code}" : null,
                issue.Detail
            }.OfType<string>())));

    internal static void ShowDetails(Window owner, IndexHealth health)
    {
        var dialog = CreateDetailsDialog(health);
        ModernDialog.AttachOwner(dialog, owner);
        dialog.ShowDialog();
    }

    internal static ModernDialog CreateDetailsDialog(IndexHealth health)
    {
        var details = FormatDetails(health);
        var dialog = new ModernDialog("İndeksleme ayrıntıları", "", DialogKind.Warning, "Kapat", null) { Width = 560 };
        var field = new TextBox
        {
            Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = (Style)dialog.FindResource("DialogTextBox"),
            FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 12.5, Padding = new Thickness(6, 5, 6, 5)
        };
        AutomationProperties.SetName(field, "Teknik ayrıntılar");
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(2, 8, 0, 0),
            Foreground = (Brush)dialog.FindResource("DialogInk2"), Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var copy = new Button
        {
            Content = "Ayrıntıları kopyala", Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)dialog.FindResource("DialogSecondaryButton")
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(details);
                status.Text = "Ayrıntılar panoya kopyalandı.";
            }
            catch (ExternalException)
            {
                field.Focus();
                field.SelectAll();
                status.Text = "Panoya yazılamadı. Seçili metni Ctrl+C ile kopyalayabilirsiniz.";
            }
            status.Visibility = Visibility.Visible;
        };
        var panel = new StackPanel();
        panel.Children.Add(field);
        panel.Children.Add(status);
        dialog.Extra = panel;
        dialog.ButtonRow.Children.Insert(0, copy);
        return dialog;
    }
}
