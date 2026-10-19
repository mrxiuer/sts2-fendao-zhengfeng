using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace FendaoZhengfeng.Mod;

internal static class EffectHud
{
    private static HFlowContainer? strip;
    private static NRun? anchoredRun;
    private static NRelicInventory? inventory;
    internal static float Bottom => strip?.Visible == true ? strip.Position.Y + strip.Size.Y : 170;
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
