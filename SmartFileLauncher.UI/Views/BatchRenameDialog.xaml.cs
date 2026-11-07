using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SmartFileLauncher.Core.Application.Files;

namespace SmartFileLauncher.UI.Views;

public partial class BatchRenameDialog : Window
{
    private static readonly System.Windows.Media.Brush Ink = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27)));
    private static readonly System.Windows.Media.Brush Muted = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly System.Windows.Media.Brush Danger = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0x41, 0x34)));
    private readonly IReadOnlyList<BatchRenameSource> _sources;
    private readonly Func<string, bool> _exists;

    public IReadOnlyList<BatchRenameItem> Plan { get; private set; } = [];

    public BatchRenameDialog(IReadOnlyList<BatchRenameSource> sources, Func<string, bool>? exists = null)
    {
        InitializeComponent();
        _sources = sources;
        _exists = exists ?? (path => File.Exists(path) || Directory.Exists(path));
        SubtitleText.Text = $"{sources.Count} öğe birlikte adlandırılacak";
        SourceInitialized += (_, _) => ModernDialog.ApplyRoundedCorners(this);
        foreach (var box in new[] { TemplateBox, StartBox, FindBox, ReplaceBox })
            box.TextChanged += (_, _) => Refresh();
        foreach (var option in new[] { CaseKeep, CaseLower, CaseUpper, CaseTitle })
            option.Checked += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            TemplateBox.Focus();
        };
    }

    private BatchRenameOptions Options => new(
        FindBox.Text,
        ReplaceBox.Text,
        TemplateBox.Text,
        int.TryParse(StartBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) && start >= 0 ? start : 1,
        CaseLower.IsChecked == true ? BatchRenameCase.Lower
            : CaseUpper.IsChecked == true ? BatchRenameCase.Upper
            : CaseTitle.IsChecked == true ? BatchRenameCase.Title
            : BatchRenameCase.Keep);

    private void Refresh()
    {
        if (!IsInitialized || PreviewList == null) return;
        Plan = BatchRenamePlanner.Plan(_sources, Options, _exists);
        PreviewList.ItemsSource = Plan.Select(item => new PreviewRow(item)).ToArray();
        var ready = Plan.Count(item => item.Status == BatchRenameStatus.Ready);
        var problems = Plan.Count(item => item.Status is BatchRenameStatus.Invalid or BatchRenameStatus.Duplicate or BatchRenameStatus.Exists);
        SummaryText.Text = problems > 0
            ? $"{problems} adda sorun var. Düzeltmeden uygulanamaz."
            : ready == 0 ? "Henüz değişen ad yok." : $"{ready} öğenin adı değişecek.";
        SummaryText.Foreground = problems > 0 ? Danger : (System.Windows.Media.Brush)FindResource("DialogInk2");
        ApplyButton.IsEnabled = BatchRenamePlanner.CanApply(Plan);
        ApplyButton.Opacity = ApplyButton.IsEnabled ? 1 : 0.45;
        ApplyButton.Content = ApplyButton.IsEnabled ? $"{ready} öğeyi adlandır" : "Yeniden adlandır";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (!BatchRenamePlanner.CanApply(Plan)) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase &&
            e.OriginalSource is not System.Windows.Controls.TextBox &&
            e.OriginalSource is not System.Windows.Controls.ScrollViewer)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private static System.Windows.Media.Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    internal sealed class PreviewRow(BatchRenameItem item)
    {
        public string SourcePath => item.SourcePath;
        public string OldName => item.OldName;
        public string NewName => item.Status == BatchRenameStatus.Invalid && item.NewName.Length == 0 ? "—" : item.NewName;
        public System.Windows.Media.Brush NameBrush => item.Status switch
        {
            BatchRenameStatus.Unchanged => Muted,
            BatchRenameStatus.Ready => Ink,
            _ => Danger
        };
        public string Note => item.Status switch
        {
            BatchRenameStatus.Unchanged => "değişmiyor",
            BatchRenameStatus.Invalid => "geçersiz ad",
            BatchRenameStatus.Duplicate => "aynı ad iki kez",
            BatchRenameStatus.Exists => "bu ad zaten var",
            _ => string.Empty
        };
        public System.Windows.Media.Brush NoteBrush => item.Status == BatchRenameStatus.Unchanged ? Muted : Danger;
    }
}
