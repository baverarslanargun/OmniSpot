using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.UI.Services;
using Image = System.Windows.Controls.Image;

namespace SmartFileLauncher.UI.Views;

public partial class StartMenuResults : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty LeadsWithBestMatchProperty =
        DependencyProperty.Register(nameof(LeadsWithBestMatch), typeof(bool), typeof(StartMenuResults));
    public static readonly DependencyProperty BestMatchSelectedProperty =
        DependencyProperty.Register(nameof(BestMatchSelected), typeof(bool), typeof(StartMenuResults));
    public static readonly DependencyProperty PinnedIdsProperty =
        DependencyProperty.Register(nameof(PinnedIds), typeof(IReadOnlySet<string>), typeof(StartMenuResults));
    public static readonly DependencyProperty ArrivingProperty =
        DependencyProperty.RegisterAttached("Arriving", typeof(bool), typeof(StartMenuResults));

    public static bool GetArriving(DependencyObject element) => (bool)element.GetValue(ArrivingProperty);
    public static void SetArriving(DependencyObject element, bool value) => element.SetValue(ArrivingProperty, value);

    internal const int GridColumns = 4;
    private const int IconCapacity = 64;
    private static readonly Thickness StripMargin = new(4, 4, 4, 2);
    private readonly IStartMenuShell _shell;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.Ordinal);
    private readonly Queue<string> _iconOrder = new();
    private readonly HashSet<Image> _realized = [];
    private bool _iconPassPending;
    private int _generation;
    private bool _stopped;
    private bool _browsing;
    private bool _grid;
    private bool _bestMatchArriving;
    private SelectionGlide? _glide;

    internal StartMenuResults(IStartMenuShell shell)
    {
        _shell = shell;
        InitializeComponent();
        IsVisibleChanged += (_, _) => ScheduleIcons();
        Applications.SelectionChanged += (_, _) => UpdateBestMatchSelection();
    }

    internal event EventHandler? BestMatchActivated;

    public bool LeadsWithBestMatch
    {
        get => (bool)GetValue(LeadsWithBestMatchProperty);
        private set => SetValue(LeadsWithBestMatchProperty, value);
    }

    public bool BestMatchSelected
    {
        get => (bool)GetValue(BestMatchSelectedProperty);
        private set => SetValue(BestMatchSelectedProperty, value);
    }

    public IReadOnlySet<string>? PinnedIds
    {
        get => (IReadOnlySet<string>?)GetValue(PinnedIdsProperty);
        set => SetValue(PinnedIdsProperty, value);
    }

    internal int StripStart => LeadsWithBestMatch ? 1 : 0;
    internal bool IsGrid => _browsing && _grid;
    internal SelectionGlide Glide => _glide ??= new SelectionGlide(GlideLayer);

    internal void FinishGlide() => _glide?.Finish();

    internal Border? SelectionSurface(int index)
    {
        if (index == 0 && LeadsWithBestMatch) return BestMatch;
        return Applications.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem { Template: { } template } item
            ? template.FindName("Surface", item) as Border
            : null;
    }

    internal Action? HoldSelection(int index)
    {
        if (index == 0 && LeadsWithBestMatch)
        {
            _bestMatchArriving = true;
            UpdateBestMatchSelection();
            return () =>
            {
                _bestMatchArriving = false;
                UpdateBestMatchSelection();
            };
        }
        if (Applications.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return null;
        SetArriving(item, true);
        return () => SetArriving(item, false);
    }

    internal void UseLayout(bool browsing, StartMenuApplication? bestMatch, bool grid = false)
    {
        grid &= browsing;
        if (_browsing != browsing || _grid != grid)
        {
            FinishGlide();
            _browsing = browsing;
            _grid = grid;
            Applications.ItemsSource = null;
            var layout = grid ? "Grid" : browsing ? "List" : "Strip";
            Applications.ItemsPanel = (ItemsPanelTemplate)Resources[layout + "Panel"];
            Applications.ItemContainerStyle = (Style)Resources[grid ? "TileItem" : browsing ? "RowItem" : "ChipItem"];
            Applications.ItemTemplate = (DataTemplate)Resources[grid ? "TileTemplate" : browsing ? "RowTemplate" : "ChipTemplate"];
            Applications.HorizontalAlignment = browsing
                ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Left;
            EmptyMessage.HorizontalAlignment = browsing
                ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            EmptyMessage.Margin = browsing ? new Thickness(12, 16, 12, 16) : new Thickness(0);
            Strip.Margin = browsing ? new Thickness(0) : StripMargin;
            BrowseBack.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
            StripLabel.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        }
        LeadsWithBestMatch = bestMatch != null;
        BestMatch.DataContext = bestMatch;
        BestMatch.Visibility = bestMatch != null ? Visibility.Visible : Visibility.Collapsed;
        StripLabel.Text = bestMatch != null ? "Diğer" : "Uygulamalar";
        UpdateBestMatchSelection();
    }

    internal void UpdateStrip()
    {
        var others = Applications.Items.Count - StripStart;
        Strip.Visibility = _browsing || others > 0 || ShowAll.Visibility == Visibility.Visible ||
            EmptyMessage.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBestMatchSelection() =>
        BestMatchSelected = LeadsWithBestMatch && Applications.SelectedIndex == 0 && !_bestMatchArriving;

    private void BestMatchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!LeadsWithBestMatch || Applications.Items.Count == 0) return;
        Applications.SelectedIndex = 0;
        if (e.ClickCount == 2) BestMatchActivated?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    internal void ResetIcons(IReadOnlySet<string>? changedIds = null)
    {
        _generation++;
        if (changedIds == null)
        {
            _icons.Clear();
            _iconOrder.Clear();
        }
        else
        {
            foreach (var id in changedIds) _icons.Remove(id);
            for (int remaining = _iconOrder.Count; remaining > 0; remaining--)
            {
                var id = _iconOrder.Dequeue();
                if (!changedIds.Contains(id)) _iconOrder.Enqueue(id);
            }
        }
        foreach (var image in _realized)
            if (changedIds == null || image.DataContext is StartMenuApplication app && changedIds.Contains(app.Id))
                image.Source = null;
        ScheduleIcons();
    }

    internal void Stop()
    {
        _stopped = true;
        FinishGlide();
        _realized.Clear();
        ResetIcons();
    }

    private void IconLoaded(object sender, RoutedEventArgs e)
    {
        _realized.Add((Image)sender);
        BindIcon((Image)sender);
    }

    private void IconUnloaded(object sender, RoutedEventArgs e) => _realized.Remove((Image)sender);

    private void IconDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => BindIcon((Image)sender);

    private void BindIcon(Image image)
    {
        image.Source = image.DataContext is StartMenuApplication app ? _icons.GetValueOrDefault(app.Id) : null;
        if (image.Source == null && image.IsLoaded) ScheduleIcons();
    }

    private void ScheduleIcons()
    {
        if (_stopped || _iconPassPending || !IsVisible) return;
        _iconPassPending = true;
        _ = Dispatcher.InvokeAsync(LoadVisibleIcons, DispatcherPriority.Background);
    }

    private void LoadVisibleIcons()
    {
        if (_stopped || !IsVisible) { _iconPassPending = false; return; }
        var batch = _realized.Where(image => image.IsVisible).Select(image => image.DataContext)
            .OfType<StartMenuApplication>().Where(app => !_icons.ContainsKey(app.Id))
            .DistinctBy(app => app.Id).Take(8).ToArray();
        if (batch.Length == 0) { _iconPassPending = false; return; }
        var generation = _generation;
        var worker = new Thread(() =>
        {
            var loaded = new ImageSource?[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                try { loaded[i] = _shell.LoadIcon(batch[i]); }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
            }
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.InvokeAsync(() => ApplyIcons(batch, loaded, generation), DispatcherPriority.Background);
        }) { IsBackground = true, Name = "OmniSpot application icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void ApplyIcons(StartMenuApplication[] batch, ImageSource?[] icons, int generation)
    {
        _iconPassPending = false;
        if (_stopped) return;
        if (generation == _generation)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                if (_iconOrder.Count == IconCapacity) _icons.Remove(_iconOrder.Dequeue());
                _icons[batch[i].Id] = icons[i];
                _iconOrder.Enqueue(batch[i].Id);
            }
            foreach (var image in _realized)
                if (image.DataContext is StartMenuApplication app && _icons.TryGetValue(app.Id, out var icon))
                    image.Source = icon;
        }
        ScheduleIcons();
    }
}

public sealed class PinnedApplicationVisibility : IMultiValueConverter
{
    public static PinnedApplicationVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string id && values[1] is IReadOnlySet<string> pinned && pinned.Contains(id)
            ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
