using System.Text.RegularExpressions;

namespace TranslationManager.Api.Support;

/// <summary>Kết quả so khớp biến thay thế giữa chuỗi nguồn và bản dịch.</summary>
public sealed record PlaceholderCheck(string[] Expected, string[] Actual, string[] Missing, string[] Extra)
{
    public bool Ok => Missing.Length == 0 && Extra.Length == 0;
}

public static partial class PlaceholderValidator
{
    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial Regex TokenRegex();

    public static string[] Extract(string? text) => TokenRegex()
        .Matches(text ?? string.Empty)
        .Select(m => m.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Bản dịch phải chứa đúng tập biến {name}/{count}... của chuỗi nguồn: không thiếu, không thừa.
    /// </summary>
    public static PlaceholderCheck Check(string source, string? translation)
    {
        var expected = Extract(source);
        var actual = Extract(translation);
        return new PlaceholderCheck(
            expected,
            actual,
            expected.Except(actual, StringComparer.Ordinal).ToArray(),
            actual.Except(expected, StringComparer.Ordinal).ToArray());
    }
}
