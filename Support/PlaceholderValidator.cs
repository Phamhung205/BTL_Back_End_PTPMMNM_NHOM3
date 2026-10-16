using System.Text.RegularExpressions;

namespace TranslationManager.Api.Support;

public static partial class PlaceholderValidator
{
    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial Regex TokenRegex();

    public static string[] Extract(string text) => TokenRegex()
        .Matches(text ?? string.Empty)
        .Select(m => m.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    public static bool Match(string source, string translation, out object detail)
    {
        var expected = Extract(source);
        var actual = Extract(translation);
        var ok = expected.SequenceEqual(actual, StringComparer.Ordinal);
        detail = new { expected, actual };
        return ok;
    }
}
