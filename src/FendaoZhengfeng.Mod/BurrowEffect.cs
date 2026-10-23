using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>Native execution for the persisted Burrow Sanctuary reducer state.</summary>
internal static class BurrowEffect
{
    internal static async Task Click()
    {
        await Activate(endTurn: true);
    }

    /// <summary>Undo an unfinished use when the native combat is replayed from its opening state.</summary>
    internal static async Task AfterBattleStart(bool replaying)
    {
        if (!replaying || RaceSession.Current is not { } session || !session.Me.InBattle) return;
        if (BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready) return;
        await session.Submit(session.New("burrow_replay", session.Me.BattleKey));
        BurrowVisuals.Restore(RaceRuntime.State?.Players.SingleOrDefault()?.Creature);
        BurrowStatusHud.Refresh();
    }

    // Kept as one operation for native verification: a test may inspect the exact
    // block and break it before asking CombatManager to execute the end-turn.
    internal static async Task<int> Activate(bool endTurn)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session || RaceModifier.Active is not { } modifier)
            throw new RuleException("地底庇护只能在分道争锋中使用。");
        var player = RaceRuntime.State?.Players.SingleOrDefault();
        if (player == null || !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsEnding
            || !session.Me.InBattle || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || player.Creature.CombatState?.CurrentSide != CombatSide.Player)
            throw new RuleException("地底庇护只能在自己的战斗回合使用。");

        var before = BurrowRules.Read(session.Me);
        if (!session.Me.Has(BurrowRules.EffectId)) throw new RuleException("尚未取得地底庇护。");
        if (before.Charges <= 0) throw new RuleException("地底庇护没有储存次数。");
        if (before.Phase != BurrowRules.Phase.Ready) throw new RuleException("地底庇护仍在冷却中。");

        string battleKey = session.Me.BattleKey;
        await session.Submit(session.New("burrow_use", battleKey, amount: modifier.Turn));
        if (!ReferenceEquals(RaceSession.Current, session)) return 0;
        var applied = BurrowRules.Read(session.Me);
        if (applied.Phase != BurrowRules.Phase.Burrowed || applied.Charges != before.Charges - 1
            || session.Me.BattleKey != battleKey)
            throw new RuleException("遁地指令未被比赛状态确认。");

        int block = 0;
        foreach (var body in TwinSlimeRuntime.BodiesOf(player.Creature).Where(body => body.IsAlive))
        {
            int amount = body.MaxHp;
            body.GainBlockInternal(amount);
            block = checked(block + amount);
            if (amount > 0 && body.CombatState is { } combat)
                CombatManager.Instance.History.BlockGained(combat, PuppetFollower.Guard(body) ?? body, amount, ValueProp.Unpowered, null);
        }
        BurrowVisuals.Bury(player.Creature);
        BurrowStatusHud.Refresh();
        if (endTurn) PlayerCmd.EndTurn(player, canBackOut: false);
        return block;
    }

    /// <summary>
    /// Call at the end of RaceModifier.AfterPlayerTurnStartLate, after the native
    /// turn-start work and before any early return for the first turn.
    /// </summary>
    internal static async Task AfterPlayerTurnStartLate(Player player, RaceModifier modifier)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session || RaceRuntime.State?.Players.SingleOrDefault() != player
            || !session.Me.InBattle || !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsEnding) return;

        string battleKey = session.Me.BattleKey;
        var state = BurrowRules.Read(session.Me);
        switch (state.Phase)
        {
            case BurrowRules.Phase.Burrowed:
                if (modifier.Turn <= state.UseTurn) break;
                BurrowVisuals.SnapBuried(player.Creature);
                await session.Submit(session.New("burrow_emerge", battleKey, amount: modifier.Turn));
                state = BurrowRules.Read(session.Me);
                bool stunned = state.Phase == BurrowRules.Phase.Stunned;
                BurrowVisuals.Emerge(player.Creature, stunned);
                BurrowStatusHud.Refresh();
                if (stunned) PlayerCmd.EndTurn(player, canBackOut: false);
                break;
            case BurrowRules.Phase.Stunned:
                BurrowVisuals.EnsureDazed(player.Creature);
                if (modifier.Turn <= state.PhaseTurn)
                {
                    // Re-entering this same turn after a native replay must still
                    // execute the real EndTurn path.
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    break;
                }
                await session.Submit(session.New("burrow_ready", battleKey, amount: modifier.Turn));
                BurrowVisuals.ClearDaze(player.Creature);
                BurrowStatusHud.Refresh();
                break;
        }
    }

    /// <summary>Called from RaceModifier.AfterBlockBroken for the player's creature.</summary>
    internal static async Task AfterBlockBroken()
    {
        if (RaceSession.Current is not { } session || !session.Me.InBattle) return;
        if (BurrowRules.Read(session.Me).Phase != BurrowRules.Phase.Burrowed) return;
        await session.Submit(session.New("burrow_block_broken", session.Me.BattleKey));
        var player = RaceRuntime.State?.Players.SingleOrDefault();
        if (player != null) BurrowVisuals.ShieldBroken(player.Creature);
        BurrowStatusHud.Refresh();
    }

    /// <summary>Call from RaceModifier.AfterCombatEnd for abnormal exits as well as normal cleanup.</summary>
    internal static async Task AfterCombatEnd()
    {
        if (RaceSession.Current is not { } session) return;
        var state = BurrowRules.Read(session.Me);
        if (state.Phase != BurrowRules.Phase.Ready)
            await session.Submit(session.New("burrow_cleanup", session.Me.BattleKey));
        BurrowVisuals.Restore(RaceRuntime.State?.Players.SingleOrDefault()?.Creature);
        BurrowStatusHud.Refresh();
    }
}

public sealed partial class RaceModifier
{
    internal bool IsNativeReplayOpening => replayOpening;

    public override Task AfterBlockBroken(PlayerChoiceContext choiceContext, Creature target, Creature? breaker)
        => target.Player == Player || target.PetOwner == Player && target.Monster is SubstitutePuppet { GuardsOwner: true }
            ? BurrowEffect.AfterBlockBroken() : Task.CompletedTask;
}

/// <summary>Compatibility entry points retained for older callers; effect UI is native.</summary>
internal static class BurrowStatusHud
{
    internal static void Install(NGame game) { }
    internal static void Refresh() { }
}

[HarmonyPatch(typeof(RaceModifier), nameof(RaceModifier.BeforeCombatStart))]
internal static class BurrowReplayResetPatch
{
    static void Postfix(ref Task __result, RaceModifier __instance) { __result = After(__result, __instance); }
    private static async Task After(Task original, RaceModifier modifier)
    {
        await original;
        await BurrowEffect.AfterBattleStart(modifier.IsNativeReplayOpening);
    }
}