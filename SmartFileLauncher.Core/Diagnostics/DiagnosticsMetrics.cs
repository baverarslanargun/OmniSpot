namespace SmartFileLauncher.Core.Diagnostics;

public enum DiagnosticsSeverity
{
    Normal,
    Good,
    Warning,
    Critical
}

public sealed record DiagnosticsReading(
    string Label,
    string Value,
    DiagnosticsSeverity Severity,
    double? Numeric = null);

public sealed record DiagnosticsGroup(
    string Title,
    IReadOnlyList<DiagnosticsReading> Readings);

public sealed class DiagnosticsMetrics
{
    private readonly object _sync = new();
    private readonly List<string> _groupOrder = new();
    private readonly Dictionary<string, List<string>> _labelOrder = new();
    private readonly Dictionary<(string Group, string Label), DiagnosticsReading> _readings = new();

    private long _revision;

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public void Set(
        string group,
        string label,
        string value,
        DiagnosticsSeverity severity = DiagnosticsSeverity.Normal,
        double? numeric = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(value);

        lock (_sync)
        {
            if (!_labelOrder.TryGetValue(group, out var labels))
            {
                labels = new List<string>();
                _labelOrder[group] = labels;
                _groupOrder.Add(group);
                _revision++;
            }

            var key = (group, label);
            if (!_readings.ContainsKey(key))
            {
                labels.Add(label);
                _revision++;
            }

            _readings[key] = new DiagnosticsReading(label, value, severity, numeric);
        }
    }

    public IReadOnlyList<DiagnosticsGroup> Snapshot()
    {
        lock (_sync)
        {
            var groups = new List<DiagnosticsGroup>(_groupOrder.Count);
            foreach (var group in _groupOrder)
            {
                var labels = _labelOrder[group];
                var readings = new List<DiagnosticsReading>(labels.Count);
                foreach (var label in labels)
                {
                    readings.Add(_readings[(group, label)]);
                }

                groups.Add(new DiagnosticsGroup(group, readings));
            }

            return groups;
        }
    }
}
