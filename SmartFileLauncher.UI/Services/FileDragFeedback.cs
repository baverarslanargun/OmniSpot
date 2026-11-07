using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DragDropEffects = System.Windows.DragDropEffects;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace SmartFileLauncher.UI.Services;

internal sealed class FileDragFeedback
{
    private readonly Window _owner;
    private readonly Popup _popup;
    private readonly TextBlock _operation;

    public FileDragFeedback(Window owner)
    {
        _owner = owner;
        _operation = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.InfoTextBrush,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var content = new StackPanel();
        content.Children.Add(_operation);
        content.Children.Add(new TextBlock
        {
            Text = "Ctrl kopyala · Shift taşı",
            FontSize = 12,
            Foreground = SystemColors.InfoTextBrush,
            Margin = new Thickness(0, 3, 0, 0)
        });
        _popup = new Popup
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = owner,
            StaysOpen = true,
            AllowsTransparency = true,
            Focusable = false,
            IsHitTestVisible = false,
            Child = new Border
            {
                Background = SystemColors.InfoBrush,
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                IsHitTestVisible = false,
                Child = content
            }
        };
        _popup.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_popup.Child) is HwndSource source)
            {
                const long transparentAndNoActivate = 0x20 | 0x08000000;
                var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
                SetWindowLongPtr(source.Handle, -20, new IntPtr(style | transparentAndNoActivate));
            }
        };
    }

    public void Show(DragDropEffects effect, string? destination = null)
    {
        if (!_owner.IsVisible || !GetCursorPos(out var cursor))
        {
            Hide();
            return;
        }

        var operation = (effect & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)) switch
        {
            DragDropEffects.Copy => "+ Kopyala",
            DragDropEffects.Move => "→ Taşı (kes)",
            _ => "Buraya bırakılamaz"
        };
        var name = destination == null ? null : Path.GetFileName(destination);
        _operation.Text = string.IsNullOrEmpty(name) ? operation : $"{operation} → {name}";
        var position = _owner.PointFromScreen(new Point(cursor.X, cursor.Y));
        _popup.HorizontalOffset = position.X + 16;
        _popup.VerticalOffset = position.Y + 22;
        _popup.IsOpen = true;
    }

    public void Hide() => _popup.IsOpen = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
