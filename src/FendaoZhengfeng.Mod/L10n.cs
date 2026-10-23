using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Localization;

namespace FendaoZhengfeng.Mod;

internal static class L10n
{
    private static readonly TranslationCatalog catalog = Load();
    private static TranslationCatalog Load()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("FendaoZhengfeng.Localization.eng.json")
            ?? throw new InvalidDataException("Missing English localization resource.");
        return new TranslationCatalog(stream);
    }
    // Follow the game's locale, never the OS locale. Other locales use English.
    internal static bool English => TranslationCatalog.UseEnglish(LocManager.Instance?.Language);
    internal static string T(string chinese) => catalog.Text(chinese, English);
    internal static string F(FormattableString chinese) => catalog.Format(chinese, English);
    internal static string RacerName(Racer racer)
    {
        var session = RaceSession.Current;
        var character = PublicProfiles.Read(racer)?.Character;
        if (session == null || character == null) return T(racer.Name);
        int slot = session.Engine.State.Racers.Keys.ToList().IndexOf(racer.Id) + 1;
        return F($"{slot}号 · {character.Title.GetFormattedText()}");
    }
    internal static Dictionary<string, string> Table(IReadOnlyDictionary<string, string> source)
        => catalog.EnglishTable(source);
}

[HarmonyLib.HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class RaceLocaleChangePatch
{
    private static void Postfix() => RaceUi.RefreshLanguage();
}
