using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace FendaoZhengfeng.Mod;

internal static class CardDurabilityUi
{
    private static readonly Dictionary<NCard, Label> labels = [];
    internal static int VisibleCount => labels.Values.Count(label => GodotObject.IsInstanceValid(label) && label.IsVisibleInTree());
    internal static void Update(NCard node)
    {
        if (!node.IsNodeReady()) return;
        int? remaining = RaceRuntime.Live && node.Model is { } model ? RaceModifier.Active?.RemainingWear(model) : null;
        if (!labels.TryGetValue(node, out var label) || !GodotObject.IsInstanceValid(label))
        {
            if (!remaining.HasValue) return;
            var energy = node.GetNode<Control>("%EnergyIcon");
            label = new Label { Name = "FdzfDurability", Text = "", MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 20,
                HorizontalAlignment = HorizontalAlignment.Center, Size = new Vector2(60, 56) };
            label.AddThemeFontSizeOverride("font_size", 40);
            label.AddThemeColorOverride("font_outline_color", new Color("281d16")); label.AddThemeConstantOverride("outline_size", 8);
            energy.GetParent().AddChild(label); labels[node] = label;
            node.TreeExiting += () => labels.Remove(node);
        }
        label.Visible = remaining.HasValue;
        if (!remaining.HasValue) return;
        var energyIcon = node.GetNode<Control>("%EnergyIcon");
        label.Size = new Vector2(energyIcon.Size.X, energyIcon.Size.Y);
        label.Position = new Vector2(-energyIcon.Position.X - label.Size.X, energyIcon.Position.Y);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Text = remaining.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        label.AddThemeColorOverride("font_color", remaining <= 2 ? new Color("ff8672") : new Color("fff1c4"));
    }
    internal static void Tick()
    {
        foreach (var node in labels.Keys.ToList())
        { if (!GodotObject.IsInstanceValid(node)) labels.Remove(node); else Update(node); }
    }
    internal static string? ValueOn(NCard node) => labels.TryGetValue(node, out var label) && label.Visible ? label.Text : null;
}
[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class DurabilityVisualPatch { static void Postfix(NCard __instance) => CardDurabilityUi.Update(__instance); }
[HarmonyPatch(typeof(NCard), "Reload")]
internal static class DurabilityReloadPatch { static void Postfix(NCard __instance) => CardDurabilityUi.Update(__instance); }
