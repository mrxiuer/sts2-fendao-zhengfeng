using FendaoZhengfeng.Core;
using Godot;
using Engine = FendaoZhengfeng.Core.Engine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>Core reducer and native-turn checks for Burrow Sanctuary; invoke from an isolated native verification run.</summary>
internal static class NativeBurrowVerification
{
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!;
        var player = run.Players.Single();
        var modifier = RaceModifier.Active!;
        var context = new ThrowingPlayerChoiceContext();

        var fixture = new Racer { Id = "burrow-fixture" };
        fixture.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        for (int i = 1; i <= 2; i++)
        {
            string key = "burrow-win-" + i;
            fixture.WonBattles.Add(key);
            BurrowRules.RecordVictory(fixture, key);
        }
        check(BurrowRules.Read(fixture).Charges == 0 && BurrowRules.Read(fixture).Progress == 2,
            "base sanctuary stores no use before three distinct victories");
        BurrowRules.RecordVictory(fixture, "burrow-win-2");
        check(BurrowRules.Read(fixture).Progress == 2, "replaying one victory key cannot advance charge progress");
        fixture.WonBattles.Add("burrow-win-3");
        BurrowRules.RecordVictory(fixture, "burrow-win-3");
        check(BurrowRules.Read(fixture).Charges == 1 && BurrowRules.Read(fixture).Progress == 0,
            "the third victory grants exactly one stored use");
        fixture.Effects[BurrowRules.EffectId].Rank = 1;
        foreach (string key in new[] { "burrow-win-4", "burrow-win-5" })
        { fixture.WonBattles.Add(key); BurrowRules.RecordVictory(fixture, key); }
        check(BurrowRules.Read(fixture).Charges == 2 && BurrowRules.Read(fixture).WinsPerCharge == 2,
            "one reinforcement lowers each charge goal to two wins and storage stops at two");
        fixture.WonBattles.Add("burrow-win-6"); BurrowRules.RecordVictory(fixture, "burrow-win-6");
        check(BurrowRules.Read(fixture).Charges == 2 && BurrowRules.Read(fixture).Progress == 0,
            "victories earned at full storage do not bank overflow progress");
        check(BurrowRules.WinsPerChargeForRank(99) == 1,
            "charge interval never falls below one victory");
        string savedFixture = StateJson.Serialize(fixture);
        var restoredFixture = StateJson.Deserialize<Racer>(savedFixture);
        check(BurrowRules.Read(restoredFixture).Charges == 2 && BurrowRules.Read(restoredFixture).Progress == 0,
            "stored charges and charge progress survive match JSON save and restore");

        var reduced = new Racer { Id = "burrow-reducer", Battle = 1, BattleKey = "burrow-reducer-battle", InBattle = true };
        reduced.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        reduced.History["burrow_sanctuary.charges"] = 1;
        var useMatch = new Match { Id = "burrow-use-check", Racers = new() { [reduced.Id] = reduced } };
        var useEngine = new Engine(useMatch);
        var use = new Command("burrow-use-once", reduced.Id, "burrow_use", Key: reduced.BattleKey, Amount: 1);
        check(useEngine.Execute(use) && !useEngine.Execute(use), "the authoritative use command is idempotent by command ID");
        var useState = BurrowRules.Read(useEngine.State.Racers[reduced.Id]);
        check(useState.Charges == 0 && useState.Phase == BurrowRules.Phase.Burrowed,
            "the authoritative reducer spends one charge and records the buried phase together");
        var restoredUse = StateJson.Deserialize<Racer>(StateJson.Serialize(useEngine.State.Racers[reduced.Id]));
        check(BurrowRules.Read(restoredUse).Phase == BurrowRules.Phase.Burrowed
            && BurrowRules.Read(restoredUse).Charges == 0 && !BurrowRules.Read(restoredUse).BlockBroken,
            "an active unbroken use restores in place without refunding its reserved charge");
        bool secondUseRejected = false;
        try { useEngine.Execute(new Command("burrow-use-again", reduced.Id, "burrow_use", Key: reduced.BattleKey, Amount: 1)); }
        catch (RuleException) { secondUseRejected = true; }
        check(secondUseRejected && BurrowRules.Read(useEngine.State.Racers[reduced.Id]).Charges == 0,
            "a distinct duplicate-use request cannot spend another charge during the same phase");
        check(useEngine.Execute(new Command("burrow-block-break", reduced.Id, "burrow_block_broken", Key: reduced.BattleKey)),
            "native block-break event is committed to the persisted burrow state");
        var savedBurrow = StateJson.Serialize(useEngine.State.Racers[reduced.Id]);
        var restoredBurrow = StateJson.Deserialize<Racer>(savedBurrow);
        check(BurrowRules.Read(restoredBurrow).BlockBroken && BurrowRules.Read(restoredBurrow).Charges == 0,
            "the in-flight use and broken-shield state survive save and restore without restoring a charge");
        check(useEngine.Execute(new Command("burrow-emerge", reduced.Id, "burrow_emerge", Key: reduced.BattleKey, Amount: 2)),
            "the first turn after a broken shield commits emergence");
        var stunned = BurrowRules.Read(useEngine.State.Racers[reduced.Id]);
        check(stunned.Phase == BurrowRules.Phase.Stunned && stunned.BlockBroken,
            "only a broken underground shield creates the stunned phase");
        var restoredStun = StateJson.Deserialize<Racer>(StateJson.Serialize(useEngine.State.Racers[reduced.Id]));
        check(BurrowRules.Read(restoredStun).Phase == BurrowRules.Phase.Stunned
            && BurrowRules.Read(restoredStun).BlockBroken && BurrowRules.Read(restoredStun).Charges == 0,
            "a saved stun restores for the remaining skipped turn without restoring a charge");
        check(useEngine.Execute(new Command("burrow-ready", reduced.Id, "burrow_ready", Key: reduced.BattleKey, Amount: 3))
            && BurrowRules.Read(useEngine.State.Racers[reduced.Id]).Phase == BurrowRules.Phase.Ready,
            "stun ends after the real skipped turn");

        var intact = new Racer { Id = "burrow-intact", Battle = 1, BattleKey = "burrow-intact-battle", InBattle = true };
        intact.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        intact.History["burrow_sanctuary.charges"] = 1;
        BurrowRules.Use(intact, intact.BattleKey, 1);
        BurrowRules.Emerge(intact, intact.BattleKey, 2);
        check(BurrowRules.Read(intact).Phase == BurrowRules.Phase.Ready && !BurrowRules.Read(intact).BlockBroken,
            "an unbroken underground shield emerges without stun");

        var boundary = new Racer { Id = "burrow-boundary", Battle = 4, BattleKey = "burrow-boundary-4", InBattle = true };
        boundary.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        boundary.History["burrow_sanctuary.charges"] = 1;
        boundary.History["burrow_sanctuary.progress"] = 2;
        BurrowRules.Use(boundary, boundary.BattleKey, 2);
        BurrowRules.MarkBlockBroken(boundary, boundary.BattleKey);
        BurrowRules.CleanupBattle(boundary, boundary.BattleKey);
        check(BurrowRules.Read(boundary).Phase == BurrowRules.Phase.Ready
            && BurrowRules.Read(boundary).Charges == 0 && BurrowRules.Read(boundary).Progress == 2
            && !BurrowRules.Read(boundary).BlockBroken,
            "combat cleanup clears only temporary burrow state and preserves charge progress");
        boundary.Battle = 5;
        boundary.BattleKey = "burrow-boundary-5";
        BurrowRules.BeginBattle(boundary);
        check(BurrowRules.Read(boundary).Phase == BurrowRules.Phase.Ready
            && BurrowRules.Read(boundary).Charges == 0 && BurrowRules.Read(boundary).Progress == 2,
            "the next battle starts ready without refunding a spent use or losing win progress");

        var replay = new Racer { Id = "burrow-replay", Battle = 1, BattleKey = "burrow-replay-battle", InBattle = true };
        replay.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        replay.History["burrow_sanctuary.charges"] = 1;
        var replayEngine = new Engine(new Match { Id = "burrow-replay-check", Racers = new() { [replay.Id] = replay } });
        replayEngine.Execute(new Command("burrow-replay-use", replay.Id, "burrow_use", Key: replay.BattleKey, Amount: 1));
        replayEngine.Execute(new Command("burrow-replay-reset", replay.Id, "burrow_replay", Key: replay.BattleKey));
        var replayState = BurrowRules.Read(replayEngine.State.Racers[replay.Id]);
        check(replayState.Charges == 1 && replayState.Phase == BurrowRules.Phase.Ready,
            "native combat replay releases the unfinished use once so the player can choose again from turn one");
        replayEngine.Execute(new Command("burrow-replay-reset-again", replay.Id, "burrow_replay", Key: replay.BattleKey));
        check(BurrowRules.Read(replayEngine.State.Racers[replay.Id]).Charges == 1,
            "a repeated replay reset cannot refresh a charge twice");

        var victoryRacer = new Racer { Id = "burrow-victory", Battle = 1, BattleKey = "burrow-victory-key", InBattle = true };
        victoryRacer.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        var victoryEngine = new Engine(new Match { Id = "burrow-victory-check", Racers = new() { [victoryRacer.Id] = victoryRacer } });
        var victory = new Command("burrow-victory-once", victoryRacer.Id, "victory", Key: victoryRacer.BattleKey);
        check(victoryEngine.Execute(victory) && !victoryEngine.Execute(victory), "victory command replay settles sanctuary charging only once");
        var victoryState = BurrowRules.Read(victoryEngine.State.Racers[victoryRacer.Id]);
        check(victoryState.Charges == 0 && victoryState.Progress == 1,
            "one committed victory contributes exactly one saved charge-progress step");

        await Wait(() => session.Me.InBattle && CombatManager.Instance.IsInProgress
            && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && modifier.Turn >= 1);
        session.Me.Effects.Clear();
        session.Me.Effects[BurrowRules.EffectId] = new Holding { Id = BurrowRules.EffectId };
        session.Me.History["burrow_sanctuary.charges"] = 1;
        session.Me.History["burrow_sanctuary.progress"] = 0;
        BurrowVisuals.Restore(player.Creature);
        int iconSize = EffectIcons.Get(BurrowRules.EffectId).GetWidth();
        check(iconSize == 128, "the finished sanctuary artwork is loaded as the effect icon");
        player.Creature.LoseBlockInternal(player.Creature.Block);
        int hp = player.Creature.CurrentHp;
        int maxHp = player.Creature.MaxHp;
        int startingTurn = modifier.Turn;
        int blockGranted = await BurrowEffect.Activate(endTurn: false);
        check(blockGranted == maxHp && player.Creature.Block == maxHp,
            "native activation grants block equal to maximum HP without other block modifiers changing its value");
        check(BurrowRules.Read(session.Me).Charges == 0 && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Burrowed,
            "native activation commits its charge and underground phase before applying the effect");
        await Wait(() => BurrowVisuals.IsBuried(player.Creature) && BurrowVisuals.BodyOffset(player.Creature) > 70
            && BurrowVisuals.BodyOpacity(player.Creature) < 0.7f);
        check(BurrowVisuals.HasMotion(player.Creature),
            "the native hero model sinks and fades while preserving its original creature visuals");
        var hit = (await CreatureCmd.Damage(context, player.Creature, maxHp, ValueProp.Move, null, null, null)).Single();
        check(player.Creature.Block == 0 && player.Creature.CurrentHp == hp && hit.WasBlockBroken,
            "native move damage breaks the underground max-HP block while fully protecting health");
        check(BurrowRules.Read(session.Me).BlockBroken,
            "the real native block-break hook persists the underground shield break");
        PlayerCmd.EndTurn(player, canBackOut: false);
        await Wait(() => modifier.Turn == startingTurn + 1
            && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Stunned
            && BurrowVisuals.IsDazed(player.Creature));
        check(BurrowRules.Read(session.Me).BlockBroken && BurrowVisuals.IsDazed(player.Creature),
            "breaking the shield emerges with a native daze visual and consumes the next turn");
        await Wait(() => modifier.Turn >= startingTurn + 2
            && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready);
        await Task.Delay(430);
        check(modifier.Turn == startingTurn + 2 && !BurrowVisuals.IsDazed(player.Creature)
            && BurrowVisuals.BodyOffset(player.Creature) < 1 && BurrowVisuals.BodyOpacity(player.Creature) > 0.95f,
            "after the real skipped turn, the native model returns to its original position and opacity");

        session.Me.History["burrow_sanctuary.charges"] = 1;
        int intactStartTurn = modifier.Turn;
        player.Creature.LoseBlockInternal(player.Creature.Block);
        int intactBlock = await BurrowEffect.Activate(endTurn: true);
        check(intactBlock == player.Creature.MaxHp && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Burrowed,
            "the normal relic click path spends a charge, grants max-HP block, and ends the turn");
        await Wait(() => BurrowVisuals.BodyOffset(player.Creature) > 70);
        await Wait(() => modifier.Turn >= intactStartTurn + 1
            && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready);
        await Task.Delay(430);
        check(modifier.Turn == intactStartTurn + 1 && !BurrowRules.Read(session.Me).BlockBroken
            && !BurrowVisuals.IsDazed(player.Creature) && BurrowVisuals.BodyOffset(player.Creature) < 1,
            "an unbroken native shield emerges on the next player turn without stun or extra turn loss");
        BurrowVisuals.Restore(player.Creature);
        GD.Print("[FDZF BURROW COMPLETE] Core and native turn checks passed.");
    }

    private static async Task Wait(Func<bool> predicate)
    {
        long until = System.Environment.TickCount64 + 40000;
        while (!predicate())
        {
            if (System.Environment.TickCount64 > until) throw new TimeoutException("地底庇护战斗转场超时。");
            await Task.Delay(25);
        }
    }
}
