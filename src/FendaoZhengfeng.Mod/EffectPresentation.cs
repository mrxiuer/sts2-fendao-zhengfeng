using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace FendaoZhengfeng.Mod;

// Original vector symbols are rendered by Godot and packaged inside the MOD DLL.
internal static class EffectIcons
{
    private static readonly Dictionary<string, Texture2D> textures = [];
    private static readonly IReadOnlyDictionary<string, string> symbols = new Dictionary<string, string>
    {
        ["exhaustion"] = "<path d='M20 45 31 34m5-5 12-17 4 4-16 14M23 31l12 12m-16 6 8-8M27 20l-6-6m17 31 6 6'/>",
        ["imbalance"] = "<path d='M32 15v36M19 51h26M14 24l36-9M19 23l-9 17h18L19 23Zm27-6-9 17h18l-9-17Z'/>",
        ["iron_curtain"] = "<path d='M13 49V18h8v7h7v-7h8v7h7v-7h8v31M12 49h40M24 49V34h16v15m-8-15v15'/>",
        ["timidity"] = "<path d='M32 11 50 18v15c0 10-8 17-18 22-10-5-18-12-18-22V18l18-7Zm1 5-5 15 10 1-9 18'/>",
        ["fortune_dice"] = "<rect x='14' y='14' width='36' height='36' rx='6'/><path d='M23 23h1m16 0h1M31 32h1m-9 9h1m16 0h1' stroke-width='6'/>",
        ["clutter"] = "<path d='m14 20 23-6 9 34-23 6-9-34Zm24-8 10 2 5 32M10 24l-1 25 10 3m6-25 10-3m-8 11 10-3'/>",
        ["abet"] = "<path d='M18 47 42 17l7-3-2 8-24 30M20 35l11 9m10-7v14m-7-7h14M15 18l5 6m7-11 1 8'/>",
        ["carapace"] = "<path d='M12 44c0-19 9-30 20-30s20 11 20 30H12Zm20-29v29M18 23l14 8 14-8M14 35l18 9 18-9m-35 9-3 7m37-7 3 7'/>",
        ["overgrowth"] = "<path d='M32 48 15 32C3 18 24 9 32 23c8-14 29-5 17 9L32 48Zm0-12v18m0-9c-13 0-16-5-17-10 11 0 17 5 17 10Zm1-9c11 0 15-6 15-12-10 0-15 5-15 12Z'/>",
        ["barbs"] = "<path d='M13 45 50 18M18 42l-5-17 14 9m5-2-3-17 14 8m-5 8 16 6-12 9m-17-4 9 13'/>",
        ["hard_shell"] = "<path d='m32 11 19 11v22L32 55 13 44V22l19-11Zm0 12 10 6v12l-10 6-10-6V29l10-6Zm0-12v12m19-1-9 7m9 15-9-3M32 55v-8M13 44l9-3m-9-19 9 7'/>",
        ["ritual"] = "<path d='M22 50h20M24 49V30h16v19M32 28c-14-7 0-13 0-19 13 12 7 18 0 19Zm-18 5-5-5m41 5 5-5M18 19l-3-7m31 7 3-7'/>",
        ["all_in"] = "<path d='m35 10-20 26h15l-1 18 21-28H35V10Z'/>",
        ["tomorrow"] = "<path d='M18 12h28M18 52h28M22 12v9l19 22v9m1-40v9L23 43v9M24 43h16M25 22h14'/>",
        ["greedy_pocket"] = "<path d='m23 13 4 10h10l5-10H23Zm3 13c-6 7-14 12-12 20 3 10 33 10 36 0 2-8-6-13-12-20H26Zm6 7v16m5-13c-9-5-13 6-3 6 8 0 4 10-5 5'/>",
        ["bitter_sweet"] = "<path d='M12 17h18v31H12V17Zm22 0h18v31H34V17ZM17 28h8m-4-4v8m17-4h10m-30 12 6-3m14 3 6-3'/>",
        ["burn_bridges"] = "<path d='M10 43h44M15 43V28m34 15V28M12 28c10 10 30 10 40 0M22 33v10m20-10v10M31 35c-9-6-3-12 2-19 8 10 11 15-2 19Z'/>",
        ["borrowed_time"] = "<circle cx='32' cy='33' r='19'/><path d='M26 9h12m-6 5V9m0 13v12l9 5m-16 7 14-26'/>",
        ["bloom_decay"] = "<path d='M31 34v20m0-10c-11 0-14-5-14-9 9 0 14 4 14 9Zm0-10c-20 0-17-12-9-12-5-11 10-17 13-6 8-10 20 3 10 9 11 6 2 19-9 11'/><circle cx='31' cy='26' r='5'/>",
        ["shared_fortune"] = "<circle cx='22' cy='32' r='12'/><circle cx='43' cy='32' r='12'/><path d='M21 24v16m21-16v16M16 13h32m-5-5 5 5-5 5M48 51H16m5-5-5 5 5 5'/>",
        ["adversity_profit"] = "<circle cx='36' cy='35' r='16'/><path d='M36 24v22m6-17c-12-8-16 7-4 6 10 0 7 13-5 7M11 14l9 9m-5-15-4 6-6 4m17-5 4-6'/>",
        ["war_feeds_war"] = "<path d='M21 47 45 14l5-2-1 6-24 34M17 37l14 10M20 21c-9-14-21 2 1 14 22-13 10-29-1-14Z'/>",
        ["catch_up"] = "<path d='m26 13 15 17-15 18m14-18H12m32-17 12 17-12 18M13 54h27'/>",
        ["shared_misery"] = "<path d='M10 16h17v30H10V16Zm27 2h17v30H37V18ZM21 10h24m-5-5 5 5-5 5M43 54H19m5-5-5 5 5 5m3-34 9 9-9 9'/>",
        ["retaliation"] = "<path d='M22 20h17c18 0 18 28 0 28H19m3-38L12 20l10 10M20 48l7-7m7 7 7 7'/>",
        ["siphon"] = "<path d='M11 14h42L37 33v15l-10 6V33L11 14Zm12 10h18M14 43v12m-6-6h12'/>",
        ["battle_hardened"] = "<path d='m23 48 21-31 5-4v8L28 52M19 37l13 9M13 29l9-9 9 9m-18-12 9-9 9 9'/>",
        ["death_defiance"] = "<path d='M32 46 15 31C4 19 22 10 32 23c10-13 28-4 17 8L32 46Zm0-13V8m-9 9 9-9 9 9M18 53h28'/>",
        ["scapegoat"] = "<path d='M32 10 51 18v15c0 10-10 19-19 23-9-4-19-13-19-23V18l19-8Zm-10 21c0-15 20-15 20 0 0 5-4 7-5 11H27c-1-4-5-6-5-11Zm6-1h1m6 0h1M29 42v6m6-6v6'/>",
    };

    public static Texture2D Get(string id)
    {
        if (textures.TryGetValue(id, out var texture)) return texture;
        var effect = Catalog.Get(id);
        string color = effect.Kind switch { EffectKind.Curse => "#ee787c", EffectKind.Contract => "#c49af0", _ => "#8ad6ae" };
        string body = symbols.TryGetValue(id, out var symbol) ? symbol : throw new InvalidOperationException("效果缺少图标：" + id);
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64' viewBox='0 0 64 64'><rect x='2' y='2' width='60' height='60' rx='12' fill='#15212b' stroke='{color}' stroke-width='2'/><g fill='none' stroke='{color}' stroke-width='2.7' stroke-linejoin='round' stroke-linecap='round'>{body}</g></svg>";
        var image = new Image();
        if (image.LoadSvgFromString(svg) != Error.Ok) throw new InvalidOperationException("效果图标加载失败：" + id);
        textures[id] = texture = ImageTexture.CreateFromImage(image);
        return texture;
    }
}

internal static class EffectHud
{
    private static HFlowContainer? strip;
    private static NRun? anchoredRun;
    private static NRelicInventory? inventory;
    internal static bool IsVisible => strip?.IsVisibleInTree() == true;
    internal static int IconCount => strip?.GetChildCount() ?? 0;
    public static void Install(CanvasLayer layer)
    {
        strip = new HFlowContainer { Position = new Vector2(18, 174), Size = new Vector2(1050, 0), Theme = new Theme { DefaultFontSize = 22 } };
        strip.AddThemeConstantOverride("h_separation", 7);
        strip.AddThemeConstantOverride("v_separation", 7);
        layer.AddChild(strip);
    }
    public static void AlignWithRelics()
    {
        if (strip == null) return;
        if (NRun.Instance == null || !GodotObject.IsInstanceValid(NRun.Instance))
        {
            anchoredRun = null; inventory = null; strip.Visible = false; return;
        }
        if (anchoredRun != NRun.Instance || inventory == null || !GodotObject.IsInstanceValid(inventory))
        {
            anchoredRun = NRun.Instance;
            inventory = anchoredRun?.GlobalUi.FindChildren("*", "", true, false).OfType<NRelicInventory>().FirstOrDefault();
        }
        strip.Visible = RaceRuntime.Live && inventory?.IsVisibleInTree() == true;
        if (inventory == null || !GodotObject.IsInstanceValid(inventory)) return;
        strip.Position = inventory.GlobalPosition - inventory.Position + inventory.GetBottomOfInventory() + new Vector2(0, 8);
        strip.Size = new Vector2(Math.Max(400, Math.Min(1300, inventory.Size.X)), 0);
    }
    public static void Refresh()
    {
        if (strip == null) return;
        foreach (var node in strip.GetChildren()) { strip.RemoveChild(node); node.QueueFree(); }
        AlignWithRelics();
        if (!strip.Visible || RaceSession.Current is not { } session) return;
        foreach (var holding in session.Me.Effects.Values)
        {
            var effect = Catalog.Get(holding.Id);
            int active = session.Me.Count(effect.Id);
            int pending = effect.Kind == EffectKind.Boon ? 0 : holding.Grants.Count - active;
            string category = effect.Kind switch { EffectKind.Curse => "诅咒", EffectKind.Contract => "双刃契约", _ => "增益" };
            string duration = effect.Kind != EffectKind.Boon ? "持续至净化" : effect.Permanent ? "持续本局" : effect.Id == "catch_up" ? $"剩余 {holding.Used} 场战斗" : "持续至自己的下一次幕 Boss 击杀";
            string sources = string.Join("、", holding.Grants.Select(g => g.Source).Distinct().Select(id => id == session.LocalId ? "自己" : session.Engine.State.Racers.TryGetValue(id, out var racer) ? racer.Name : id));
            string counts = effect.Kind == EffectKind.Boon ? $"强化 {holding.Rank}" : $"已激活 {active} 次，待生效 {pending} 次";
            var badge = new TextureRect
            {
                Texture = EffectIcons.Get(effect.Id), CustomMinimumSize = new Vector2(64, 64),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Stop,
                SelfModulate = new Color(1, 1, 1, active == 0 && pending > 0 ? 0.65f : 1)
            };
            string description = $"{category} · {RaceUi.TierName(effect.Tier)}\n{counts}\n{effect.Description}\n{duration}" + (sources.Length > 0 ? "\n来源：" + sources : "");
            var tip = new HoverTip(new LocString(ModEntry.Id, "effect." + effect.Id + ".title"), description, EffectIcons.Get(effect.Id));
            badge.MouseEntered += () => NHoverTipSet.CreateAndShow(badge, tip, HoverTipAlignment.Center);
            badge.MouseExited += () => NHoverTipSet.Remove(badge);
            badge.TreeExiting += () => NHoverTipSet.Remove(badge);
            string stack = effect.Kind == EffectKind.Boon ? holding.Rank > 0 ? "+" + holding.Rank : "" : pending > 0 ? active > 0 ? $"{active}+{pending}" : "待" : active > 1 ? active.ToString() : "";
            if (stack.Length > 0)
            {
                var count = new Label { Text = stack, Position = new Vector2(29, 38), MouseFilter = Control.MouseFilterEnum.Ignore };
                count.AddThemeColorOverride("font_color", pending > 0 ? new Color("ffe1a1") : Colors.White);
                count.AddThemeColorOverride("font_outline_color", Colors.Black);
                count.AddThemeConstantOverride("outline_size", 5);
                badge.AddChild(count);
            }
            strip.AddChild(badge);
        }
    }
}
