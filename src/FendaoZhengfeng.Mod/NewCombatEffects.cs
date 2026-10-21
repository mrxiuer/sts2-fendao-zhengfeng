using System.Threading;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    private bool conducting;
    internal int ChaosChannels { get; private set; }
    private async Task ChaosTurnStart(PlayerChoiceContext context)
    {
        if (!Me.Has("chaos_core") || (Turn - 1) % Balance.ChaosInterval(Me.Effects["chaos_core"].Rank) != 0) return;
        OrbModel[] pool = [ModelDb.Orb<LightningOrb>(), ModelDb.Orb<FrostOrb>(), ModelDb.Orb<DarkOrb>(), ModelDb.Orb<PlasmaOrb>(), ModelDb.Orb<GlassOrb>()];
        var random = new StableRandom(S.Engine.State.Seed + ":chaos:" + S.LocalId + ":" + Me.BattleKey + ":" + Turn);
        await OrbCmd.Channel(context, pool[(int)(random.Next() * pool.Length)].ToMutable(), Player);
        ChaosChannels++;
    }
    private async Task ConductDamage(PlayerChoiceContext context, Creature? dealer, DamageResult result, Creature target)
    {
        if (conducting || !Me.Has("resonant_circuit") || dealer != Player.Creature || !target.IsEnemy || result.UnblockedDamage <= 0) return;
        int amount = (int)Math.Floor(result.UnblockedDamage * Balance.ConductionRatio(Me.Effects["resonant_circuit"].Rank));
        var others = Player.Creature.CombatState!.Enemies.Where(enemy => enemy != target && enemy.IsAlive).ToArray();
        if (amount == 0 || others.Length == 0) return;
        conducting = true;
        try { await CreatureCmd.Damage(context, others, amount, ValueProp.Unpowered, Player.Creature, null, null); }
        finally { conducting = false; }
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay)])]
internal static class DamageOriginScope
{
    private sealed record Origin(Creature? Dealer, CardModel? Card);
    private static readonly AsyncLocal<Origin?> origin = new();
    [ThreadStatic] private static bool enterOriginal;
    internal static decimal Pierce(Creature target, decimal amount, ValueProp props)
    {
        if (!RaceRuntime.Live || !target.IsEnemy || props.HasFlag(ValueProp.Unblockable)
            || origin.Value is not { Card.Type: CardType.Attack } current || current.Dealer != RaceRuntime.State!.Players.Single().Creature
            || RaceSession.Current is not { } session || !session.Me.Has("piercing_edge")) return amount;
        return amount - Math.Floor(Math.Max(0, amount) * Balance.PierceRatio(session.Me.Effects["piercing_edge"].Rank));
    }
    static bool Prefix(PlayerChoiceContext choiceContext, IEnumerable<Creature>? targets, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay, ref Task<IEnumerable<DamageResult>> __result)
    {
        if (enterOriginal) { enterOriginal = false; return true; }
        if (!RaceRuntime.Live || RaceSession.Current?.Me.Has("piercing_edge") != true) return true;
        __result = Execute(choiceContext, targets, amount, props, dealer, cardSource, cardPlay); return false;
    }
    private static async Task<IEnumerable<DamageResult>> Execute(PlayerChoiceContext context, IEnumerable<Creature>? targets, decimal amount, ValueProp props, Creature? dealer, CardModel? card, CardPlay? play)
    {
        var previous = origin.Value; origin.Value = new(dealer, card);
        try { enterOriginal = true; return await CreatureCmd.Damage(context, targets, amount, props, dealer, card, play); }
        finally { enterOriginal = false; origin.Value = previous; }
    }
}
[HarmonyPatch(typeof(Creature), nameof(Creature.DamageBlockInternal))]
internal static class PiercingBlockPatch
{ static void Prefix(Creature __instance, ref decimal amount, ValueProp props) => amount = DamageOriginScope.Pierce(__instance, amount, props); }
