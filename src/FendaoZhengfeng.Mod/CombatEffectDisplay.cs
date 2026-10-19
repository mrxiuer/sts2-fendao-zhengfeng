using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace FendaoZhengfeng.Mod;

// Informational models are attached only to native NPowerContainer UI. They never enter
// Creature.Powers, receive combat hooks, consume Artifact, or change buff-count mechanics.
public sealed class RaceDisplayPower : PowerModel
{
    public string EffectId { get; set; } = "exhaustion";
    public override PowerType Type => Catalog.Get(EffectId).Kind == EffectKind.Boon ? PowerType.Buff : PowerType.Debuff;
    public override PowerStackType StackType => EffectId is "fortune_dice" or "bitter_sweet" or "hard_shell" or "scapegoat" or "no_plan" or "r_key" or "expedition" or "combo_tax" or "cornered" or "disorder" or "combat_cycle" or "heavy_followup" or "desperate_echo" ? PowerStackType.Counter : PowerStackType.Single;
    public override bool ShouldReceiveCombatHooks => false;
    public override bool ShouldPlayVfx => false;
    public override LocString Title => new(ModEntry.Id, "display." + EffectId + ".title");
    public override LocString Description => new(ModEntry.Id, "display." + EffectId + ".description");
}
internal static class CombatEffectDisplay
{
    private sealed record Entry(RaceDisplayPower Model, NPowerContainer Container);
    private static readonly Dictionary<(Creature, string), Entry> entries = [];
    internal static int Count => entries.Count;
    private static readonly HashSet<string> enemyEffects = ["iron_curtain", "carapace", "overgrowth", "barbs", "hard_shell", "ritual"];
    internal static void Tick()
    {
        if (!RaceRuntime.Live || !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsEnding || NCombatRoom.Instance == null)
        {
            foreach (var entry in entries.Values)
            {
                if (!GodotObject.IsInstanceValid(entry.Container)) continue;
                var node = entry.Container.GetChildren().OfType<NPower>().FirstOrDefault(node => node.Model == entry.Model);
                if (node == null) continue;
                var nodes = (List<NPower>)AccessTools.Field(typeof(NPowerContainer), "_powerNodes").GetValue(entry.Container)!;
                nodes.Remove(node); node.QueueFree(); AccessTools.Method(typeof(NPowerContainer), "UpdatePositions").Invoke(entry.Container, null);
            }
            entries.Clear(); return;
        }
        var session = RaceSession.Current!; var player = RaceRuntime.State!.Players.Single(); var modifier = RaceModifier.Active!;
        var desired = new HashSet<(Creature, string)>();
        foreach (var id in session.Me.Effects.Keys.Where(id => session.Me.Has(id)))
        {
            if (enemyEffects.Contains(id))
            {
                foreach (var enemy in player.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive && modifier.IsOpeningEnemy(enemy)))
                    if (id != "hard_shell" || modifier.Turn <= 3) desired.Add((enemy, id));
            }
            else desired.Add((player.Creature, id));
        }
        foreach (var key in entries.Keys.Where(key => !desired.Contains(key) || !GodotObject.IsInstanceValid(entries[key].Container)).ToList())
        {
            var entry = entries[key];
            if (GodotObject.IsInstanceValid(entry.Container)) AccessTools.Method(typeof(NPowerContainer), "Remove").Invoke(entry.Container, [entry.Model]);
            entries.Remove(key);
        }
        foreach (var key in desired)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                var visual = NCombatRoom.Instance.GetCreatureNode(key.Item1);
                var container = visual?.FindChildren("*", "", true, false).OfType<NPowerContainer>().FirstOrDefault();
                if (container == null) continue;
                var power = (RaceDisplayPower)ModelDb.Power<RaceDisplayPower>().ToMutable(); power.EffectId = key.Item2;
                AccessTools.PropertySetter(typeof(PowerModel), nameof(PowerModel.Owner)).Invoke(power, [key.Item1]);
                AccessTools.Field(typeof(PowerModel), "_amount").SetValue(power, 1);
                AccessTools.Method(typeof(NPowerContainer), "Add").Invoke(container, [power]);
                entries[key] = entry = new(power, container);
            }
            int amount = key.Item2 switch
            {
                "sharpen" or "light_step" or "poise" => 1 + session.Me.Effects[key.Item2].Rank,
                "prepared" => 6 + 3 * session.Me.Effects[key.Item2].Rank,
                "restoration" => 2 + session.Me.Effects[key.Item2].Rank,
                "abrasion" => 1 + session.Me.Count(key.Item2),
                "fortune_dice" => modifier.Dice,
                "bitter_sweet" => modifier.ManualCards,
                "hard_shell" => modifier.RemainingEnemyBudget(key.Item1),
                "scapegoat" => session.Me.Ghosts,
                "no_plan" => modifier.VaultCount,
                "r_key" => session.Me.ResetCharges,
                "expedition" => session.Me.ExpeditionProgress,
                "combo_tax" => modifier.ManualCards % 4,
                "cornered" => modifier.CorneredUsed ? 0 : 1,
                "disorder" => modifier.DisorderUsed ? 0 : 1,
                "combat_cycle" => modifier.CycleUsed ? 0 : 1,
                "heavy_followup" => modifier.HeavyUsed ? 0 : 1,
                "desperate_echo" => modifier.EchoUsed ? 0 : 1,
                _ => session.Me.Count(key.Item2)
            };
            AccessTools.Field(typeof(PowerModel), "_amount").SetValue(entry.Model, amount);
            var node = entry.Container.GetChildren().OfType<NPower>().FirstOrDefault(node => node.Model == entry.Model);
            if (node != null) AccessTools.Method(typeof(NPower), "RefreshAmount").Invoke(node, null);
        }
    }
}
[HarmonyPatch]
internal static class InformationalPowerIconPatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    { yield return AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Icon)); yield return AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.BigIcon)); }
    static bool Prefix(PowerModel __instance, ref Texture2D __result)
    { if (__instance is not RaceDisplayPower marker) return true; __result = EffectIcons.Get(marker.EffectId); return false; }
}

// Native PowerModel formats descriptions directly through LocManager.SmartFormat,
// which also requires an on-disk localization table. Our informational effects use
// dynamic text, so provide their complete native hover tip before that formatter.
[HarmonyPatch(typeof(PowerModel), "get_HoverTips")]
internal static class InformationalPowerHoverPatch
{
    static bool Prefix(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (__instance is not RaceDisplayPower marker) return true;
        __result = [new HoverTip(marker.Title, EffectDetails.Describe(marker.EffectId), EffectIcons.Get(marker.EffectId))];
        return false;
    }
}

[HarmonyPatch(typeof(NPower), nameof(NPower._Ready))]
internal static class InteractivePowerMarkerPatch
{
    static void Postfix(NPower __instance)
    {
        if (__instance.Model is not RaceDisplayPower marker || marker.EffectId is not ("small_light" or "good_connections" or "r_key")) return;
        __instance.GuiInput += input =>
        { if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) { __instance.AcceptEvent(); RaceUi.Run(() => AdvancedEffects.Click(marker.EffectId)); } };
    }
}
