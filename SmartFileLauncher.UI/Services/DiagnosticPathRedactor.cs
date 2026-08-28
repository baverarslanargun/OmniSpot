using System.Text.RegularExpressions;

namespace SmartFileLauncher.UI.Services;

internal static class DiagnosticPathRedactor
{
    private static readonly Regex AbsolutePath = new(
        @"(?:[A-Za-z]:[\\/]|\\\\|%(?:USERPROFILE|APPDATA|LOCALAPPDATA)%[\\/])[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AbsolutePath.Replace(value, "<gizli-path>");
    }
}
