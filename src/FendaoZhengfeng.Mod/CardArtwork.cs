using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

internal static class CardArtwork
{
    private static readonly Dictionary<string, Texture2D> cache = [];
    internal static string? Key(CardModel card) => card is ExtraTimelineCard ? "extra_timeline" : card is CharmCard ? "heartstrings_charm" : null;
    internal static Texture2D Get(string key)
    {
        if (cache.TryGetValue(key, out var texture)) return texture;
        using var source = typeof(CardArtwork).Assembly.GetManifestResourceStream("FendaoZhengfeng.CardArt." + key + ".png")
            ?? throw new IOException(L10n.T("缺少卡牌立绘：") + key);
        using var bytes = new MemoryStream(); source.CopyTo(bytes);
        using var image = new Image();
        if (image.LoadPngFromBuffer(bytes.ToArray()) != Error.Ok) throw new IOException(L10n.T("卡牌立绘损坏：") + key);
        image.Resize(768, 512, Image.Interpolation.Lanczos);
        texture = ImageTexture.CreateFromImage(image); texture.ResourceName = key;
        return cache[key] = texture;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Portrait), MethodType.Getter)]
internal static class GeneratedCardPortraitPatch
{
    static bool Prefix(CardModel __instance, ref Texture2D __result)
    { if (CardArtwork.Key(__instance) is not { } key) return true; __result = CardArtwork.Get(key); return false; }
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.AllPortraitPaths), MethodType.Getter)]
internal static class GeneratedCardPortraitPathsPatch
{
    static bool Prefix(CardModel __instance, ref IEnumerable<string> __result)
    { if (CardArtwork.Key(__instance) == null) return true; __result = []; return false; }
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.HasPortrait), MethodType.Getter)]
internal static class GeneratedCardHasPortraitPatch
{
    static bool Prefix(CardModel __instance, ref bool __result)
    { if (CardArtwork.Key(__instance) == null) return true; __result = true; return false; }
}
