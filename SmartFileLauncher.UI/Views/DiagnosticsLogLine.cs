namespace SmartFileLauncher.UI.Views;

public enum DiagnosticsLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record DiagnosticsLogLine(
    string Raw,
    string Time,
    string Message,
    DiagnosticsLogLevel Level)
{
    private const string ErrorMark = "❌";
    private const string WarningMark = "⚠";

    public static DiagnosticsLogLine Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var time = string.Empty;
        var message = raw;
        var close = raw.Length > 0 && raw[0] == '[' ? raw.IndexOf(']') : -1;
        if (close > 1 && close <= 16)
        {
            time = raw.Substring(1, close - 1);
            message = raw[(close + 1)..].TrimStart(' ');
        }

        var level = message.StartsWith(ErrorMark, StringComparison.Ordinal)
            ? DiagnosticsLogLevel.Error
            : message.StartsWith(WarningMark, StringComparison.Ordinal)
                ? DiagnosticsLogLevel.Warning
                : DiagnosticsLogLevel.Info;

        return new DiagnosticsLogLine(raw, time, message, level);
    }
}
