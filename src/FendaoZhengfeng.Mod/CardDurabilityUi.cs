using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace FendaoZhengfeng.Mod;

internal static class CardDurabilityUi
{
    private static readonly Dictionary<NCard, TextureRect> badges = [];
    internal static int VisibleCount => badges.Values.Count(badge => GodotObject.IsInstanceValid(badge) && badge.IsVisibleInTree());
    internal static void Update(NCard node)
    {
        if (!node.IsNodeReady()) return;
        int? remaining = RaceRuntime.Live && node.Model is { } model ? RaceModifier.Active?.RemainingWear(model) : null;
        if (!badges.TryGetValue(node, out var badge) || !GodotObject.IsInstanceValid(badge))
        {
            if (!remaining.HasValue) return;
            var energy = node.GetNode<TextureRect>("%EnergyIcon");
            var energyLabel = node.GetNode<Control>("%EnergyLabel");
            badge = new TextureRect
            {
                Name = "FdzfDurability", MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = energy.StretchMode
            };
            var value = new Label
            {
                Name = "Value", MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            value.AddThemeFontOverride("font", energyLabel.GetThemeFont("font"));
            value.AddThemeFontSizeOverride("font_size", energyLabel.GetThemeFontSize("font_size"));
            value.AddThemeColorOverride("font_outline_color", energyLabel.GetThemeColor("font_outline_color"));
            value.AddThemeConstantOverride("outline_size", energyLabel.GetThemeConstant("outline_size"));
            badge.AddChild(value);
            energy.GetParent().AddChild(badge); badges[node] = badge;
            node.TreeExiting += () => badges.Remove(node);
        }
        badge.Visible = remaining.HasValue;
        if (!remaining.HasValue) return;
        var energyIcon = node.GetNode<TextureRect>("%EnergyIcon");
        badge.ZIndex = energyIcon.ZIndex;
        badge.ZAsRelative = energyIcon.ZAsRelative;
        badge.Texture = energyIcon.Texture;
        badge.Size = energyIcon.Size;
        badge.Position = new Vector2(-energyIcon.Position.X - badge.Size.X, energyIcon.Position.Y);
        var label = badge.GetNode<Label>("Value");
        label.Size = badge.Size;
        label.Text = remaining.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        label.AddThemeColorOverride("font_color", remaining <= 2 ? new Color("ff8672") : new Color("fff1c4"));
    }
    internal static void Tick()
    {
        foreach (var node in badges.Keys.ToList())
        { if (!GodotObject.IsInstanceValid(node)) badges.Remove(node); else Update(node); }
    }
    internal static bool HasFrameOn(NCard node) => badges.TryGetValue(node, out var badge) && badge.Visible
        && badge.Texture == node.GetNode<TextureRect>("%EnergyIcon").Texture
        && badge.Size == node.GetNode<TextureRect>("%EnergyIcon").Size;
    internal static bool HasNativeLayerOn(NCard node) => badges.TryGetValue(node, out var badge)
        && badge.GetParent() == node.GetNode<TextureRect>("%EnergyIcon").GetParent()
        && badge.ZIndex == node.GetNode<TextureRect>("%EnergyIcon").ZIndex
        && badge.ZAsRelative == node.GetNode<TextureRect>("%EnergyIcon").ZAsRelative;
    internal static string? ValueOn(NCard node) => badges.TryGetValue(node, out var badge) && badge.Visible ? badge.GetNode<Label>("Value").Text : null;
}
[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class DurabilityVisualPatch { static void Postfix(NCard __instance) => CardDurabilityUi.Update(__instance); }
[HarmonyPatch(typeof(NCard), "Reload")]
internal static class DurabilityReloadPatch { static void Postfix(NCard __instance) => CardDurabilityUi.Update(__instance); }
