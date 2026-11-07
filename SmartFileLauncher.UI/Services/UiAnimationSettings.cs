using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace SmartFileLauncher.UI.Services;

public sealed class UiAnimationSettings : INotifyPropertyChanged
{
    public static UiAnimationSettings Current { get; } = new();

    private bool _userEnabled = true;

    private UiAnimationSettings()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                NotifyChanged();
        };
    }

    public bool Enabled => _userEnabled && SystemParameters.ClientAreaAnimation;

    public PopupAnimation PopupAnimation => Enabled ? PopupAnimation.Fade : PopupAnimation.None;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(bool enabled)
    {
        if (_userEnabled == enabled) return;
        _userEnabled = enabled;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PopupAnimation)));
    }
}
