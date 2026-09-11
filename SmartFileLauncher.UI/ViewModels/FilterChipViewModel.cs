namespace SmartFileLauncher.UI.ViewModels;

public sealed class FilterChipViewModel
{
    public FilterChipViewModel(object value, string label)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    public object Value { get; }

    public string Label { get; }
}
