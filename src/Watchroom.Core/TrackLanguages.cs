using System.Globalization;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public record LanguageTrack(int Id, string Name, string? Language);

public static class TrackLanguages
{
    private static readonly Dictionary<string, string> languages = BuildLanguages();
    private static Dictionary<string, string> BuildLanguages()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0))
            foreach (var name in new[] { culture.Name, culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName, culture.EnglishName, culture.NativeName })
                result.TryAdd(name, culture.TwoLetterISOLanguageName);
        foreach (var alias in new[] { ("fre", "fr"), ("ger", "de"), ("arm", "hy"), ("chi", "zh"), ("cze", "cs"), ("dut", "nl"), ("gre", "el"), ("rum", "ro"), ("slo", "sk"), ("wel", "cy"), ("alb", "sq"), ("baq", "eu"), ("bur", "my"), ("geo", "ka"), ("ice", "is"), ("mac", "mk"), ("may", "ms"), ("per", "fa"), ("tib", "bo") })
            result[alias.Item1] = alias.Item2;
        return result;
    }
    public static string? Normalize(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (languages.TryGetValue(value, out var code)) return code;
        var prefix = value.Split('-', '_')[0];
        return languages.GetValueOrDefault(prefix);
    }
    public static string[] Parse(string text, bool subtitles = false)
    {
        return text.Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => subtitles && value.Equals("off", StringComparison.OrdinalIgnoreCase) ? "off" :
                Normalize(value) ?? throw new ArgumentException($"Unknown language '{value}'. Use a language name or code, such as English, Russian, en or ru."))
            .Distinct().ToArray();
    }
    public static int? Select(IEnumerable<LanguageTrack> tracks, IReadOnlyList<string> preferences, bool subtitles = false)
    {
        var available = tracks.Where(t => t.Id >= 0).ToArray();
        foreach (var preference in preferences)
        {
            if (subtitles && preference == "off") return -1;
            var selected = available.FirstOrDefault(t => Normalize(t.Language) == preference ||
                Normalize(t.Language) is null && Regex.Split(t.Name, @"[\s\[\]():,;/]+").Any(part => Normalize(part) == preference));
            if (selected is not null) return selected.Id;
        }
        return null;
    }
}
