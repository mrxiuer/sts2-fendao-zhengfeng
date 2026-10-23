using System.Globalization;
using System.Text.Json;

namespace FendaoZhengfeng.Core;

// Presentation only: never use localized text as a protocol, save, or effect ID.
public sealed class TranslationCatalog
{
    private readonly IReadOnlyDictionary<string, string> english;
    public TranslationCatalog(Stream json) => english = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
        ?? throw new InvalidDataException("Empty translation catalog.");
    public static bool UseEnglish(string? language) => !(language ?? "zhs").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public Dictionary<string, string> EnglishTable(IReadOnlyDictionary<string, string> source)
        => source.ToDictionary(pair => pair.Key, pair => Text(pair.Value, true), StringComparer.Ordinal);
    public string Text(string chinese, bool useEnglish) => useEnglish && english.TryGetValue(chinese, out var text) ? text : chinese;
    public string Format(FormattableString text, bool useEnglish) => string.Format(
        useEnglish ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"),
        Text(text.Format, useEnglish), text.GetArguments());
    public IReadOnlyDictionary<string, string> English => english;
}
