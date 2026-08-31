using System.Globalization;

namespace SmartFileLauncher.Core.Diagnostics;

public enum MeasurementProfile
{
    EmptyProduction,
    ProductionCopy
}

public sealed record ApplicationStartupOptions(
    DiagnosticsStartupOptions Diagnostics,
    MeasurementProfile? Profile,
    string? Error,
    TimeSpan? LiveHeapInterval = null)
{
    public const string ProfileSwitch = "--profil";
    public const string LiveHeapSwitch = "--canli-yigin";
    public const string EmptyProductionProfileName = "bos-uretim";
    public const string ProductionCopyProfileName = "uretim-kopya";

    private const int LiveHeapMinimumSeconds = 5;

    private const int LiveHeapMaximumSeconds = 3600;

    public static ApplicationStartupOptions Default { get; } =
        new(DiagnosticsStartupOptions.None, null, null);

    public bool IsMeasurement => Profile != null;

    public string? ProfileName => Profile switch
    {
        MeasurementProfile.EmptyProduction => EmptyProductionProfileName,
        MeasurementProfile.ProductionCopy => ProductionCopyProfileName,
        _ => null
    };

    public static ApplicationStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var diagnostics = DiagnosticsStartupOptions.Parse(arguments);

        var liveHeap = ReadLiveHeapInterval(arguments, out var liveHeapError);
        if (liveHeapError != null)
        {
            return new ApplicationStartupOptions(diagnostics, null, liveHeapError);
        }

        var profileValue = ReadProfileValue(arguments, out var profileError);
        if (profileError != null)
        {
            return new ApplicationStartupOptions(diagnostics, null, profileError);
        }

        if (profileValue == null)
        {
            return new ApplicationStartupOptions(diagnostics, null, null, liveHeap);
        }

        var profile = profileValue.Equals(
                EmptyProductionProfileName,
                StringComparison.OrdinalIgnoreCase)
            ? MeasurementProfile.EmptyProduction
            : profileValue.Equals(
                ProductionCopyProfileName,
                StringComparison.OrdinalIgnoreCase)
                ? MeasurementProfile.ProductionCopy
                : (MeasurementProfile?)null;

        if (profile == null)
        {
            return new ApplicationStartupOptions(
                diagnostics,
                null,
                $"Tanımsız {ProfileSwitch} değeri: {profileValue}. Desteklenen değerler: {EmptyProductionProfileName}, {ProductionCopyProfileName}.");
        }

        var diagnosticsArgumentError = ValidateDiagnosticsArguments(arguments);
        if (diagnosticsArgumentError != null)
        {
            return new ApplicationStartupOptions(
                diagnostics,
                null,
                $"{ProfileSwitch} {profileValue} için {diagnosticsArgumentError}");
        }

        if (!diagnostics.IsRequested)
        {
            var reason = diagnostics.Error == null
                ? $"{DiagnosticsStartupOptions.Switch} <koşum-dizini> zorunludur."
                : diagnostics.Error;
            return new ApplicationStartupOptions(
                diagnostics,
                null,
                $"{ProfileSwitch} {profileValue} için {reason}");
        }

        return new ApplicationStartupOptions(
            diagnostics,
            profile,
            null,
            liveHeap);
    }

    private static TimeSpan? ReadLiveHeapInterval(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        string? value = null;
        var seen = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == null) continue;

            string? candidate;
            if (argument.StartsWith(
                    LiveHeapSwitch + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                candidate = argument[(LiveHeapSwitch.Length + 1)..];
            }
            else if (argument.Equals(LiveHeapSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count ||
                    string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                    arguments[index + 1].StartsWith('-'))
                {
                    error = $"{LiveHeapSwitch} saniye cinsinden bir sayı bekliyor.";
                    return null;
                }

                candidate = arguments[++index];
            }
            else
            {
                continue;
            }

            if (seen)
            {
                error = $"{LiveHeapSwitch} yalnız bir kez verilebilir.";
                return null;
            }

            seen = true;
            value = candidate.Trim().Trim('"');
        }

        if (!seen)
        {
            error = null;
            return null;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            error = $"{LiveHeapSwitch} saniye cinsinden bir sayı bekliyor: {value}";
            return null;
        }

        if (seconds < LiveHeapMinimumSeconds || seconds > LiveHeapMaximumSeconds)
        {
            error =
                $"{LiveHeapSwitch} değeri {LiveHeapMinimumSeconds}-{LiveHeapMaximumSeconds} saniye aralığında olmalı: {seconds}";
            return null;
        }

        error = null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? ReadProfileValue(
        IReadOnlyList<string> arguments,
        out string? error)
    {
        string? value = null;
        var seen = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == null) continue;

            string? candidate = null;
            if (argument.StartsWith(
                    ProfileSwitch + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                candidate = argument[(ProfileSwitch.Length + 1)..];
            }
            else if (argument.Equals(ProfileSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count ||
                    string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                    arguments[index + 1].StartsWith('-'))
                {
                    error = $"{ProfileSwitch} bir profil adı bekliyor.";
                    return null;
                }

                candidate = arguments[++index];
            }
            else
            {
                continue;
            }

            if (seen)
            {
                error = $"{ProfileSwitch} yalnız bir kez verilebilir.";
                return null;
            }

            seen = true;
            value = candidate.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{ProfileSwitch} bir profil adı bekliyor.";
                return null;
            }
        }

        error = null;
        return value;
    }

    private static string? ValidateDiagnosticsArguments(
        IReadOnlyList<string> arguments)
    {
        var count = 0;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == null) continue;

            if (argument.StartsWith(
                    DiagnosticsStartupOptions.Switch + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                count++;
                var value = argument[(DiagnosticsStartupOptions.Switch.Length + 1)..]
                    .Trim()
                    .Trim('"');
                if (string.IsNullOrWhiteSpace(value))
                {
                    return $"{DiagnosticsStartupOptions.Switch} bir dizin bekliyor.";
                }

                continue;
            }

            if (!argument.Equals(
                    DiagnosticsStartupOptions.Switch,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            if (index + 1 >= arguments.Count ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                arguments[index + 1].StartsWith('-'))
            {
                return $"{DiagnosticsStartupOptions.Switch} bir dizin bekliyor.";
            }

            index++;
        }

        return count switch
        {
            0 => $"{DiagnosticsStartupOptions.Switch} <koşum-dizini> zorunludur.",
            > 1 => $"{DiagnosticsStartupOptions.Switch} yalnız bir kez verilebilir.",
            _ => null
        };
    }
}
