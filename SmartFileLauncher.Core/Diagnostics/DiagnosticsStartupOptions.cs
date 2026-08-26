namespace SmartFileLauncher.Core.Diagnostics;

public sealed record DiagnosticsStartupOptions(string? Directory, string? Error)
{
    public const string Switch = "--tanila";

    public static DiagnosticsStartupOptions None { get; } = new(null, null);

    public bool IsRequested => Directory != null;

    public static DiagnosticsStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == null) continue;

            if (argument.StartsWith(Switch + "=", StringComparison.OrdinalIgnoreCase))
            {
                return FromValue(argument[(Switch.Length + 1)..]);
            }

            if (!argument.Equals(Switch, StringComparison.OrdinalIgnoreCase)) continue;

            if (index + 1 >= arguments.Count)
            {
                return MissingValue();
            }

            var next = arguments[index + 1];
            if (string.IsNullOrWhiteSpace(next) || next.StartsWith('-'))
            {
                return MissingValue();
            }

            return FromValue(next);
        }

        return None;
    }

    private static DiagnosticsStartupOptions FromValue(string value)
    {
        var trimmed = value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed)
            ? MissingValue()
            : new DiagnosticsStartupOptions(trimmed, null);
    }

    private static DiagnosticsStartupOptions MissingValue() =>
        new(null, $"{Switch} bir dizin bekliyor; tanılama günlüğü başlatılmadı.");
}
