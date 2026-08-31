using System.Globalization;
using System.Text;

namespace SmartFileLauncher.Core.Search;

public static class SearchTextNormalizer
{
    public static string ToComposedForm(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        try
        {
            return value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    public static string Fold(string token)
    {
        if (string.IsNullOrEmpty(token) || IsAscii(token))
        {
            return token;
        }

        string decomposed;
        try
        {
            decomposed = token.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return token;
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'ı' => 'i',
                'İ' => 'i',
                'I' => 'i',
                _ => character
            });
        }

        return builder.ToString();
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }
}
