using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeCombinationVerification
{
    private static async Task Wait(Func<bool> ready, string reason)
    { long until = System.Environment.TickCount64 + 45000; while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(RunState run, Action<bool,string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single();
        var context = new ThrowingPlayerChoiceContext(); var modifier = RaceModifier.Active!;
        await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 2000);
        foreach (string id in new[] { PuppetRules.Id, TwinSlimeRules.EffectId, BurrowRules.EffectId, GiantForm.EffectId })
        { session.Engine.State.EnabledEffects.Add(id); await MockTesting.GrantEffect(id); }
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "combination combat ready");
        var hero = player.Creature;
        var puppet = PuppetFollower.Guard(hero) ?? throw new InvalidOperationException("combination puppet not spawned");
        var enemy = hero.CombatState!.Enemies.First(creature => creature.IsAlive);
        await CreatureCmd.Damage(context, hero, 2001, ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
        await Wait(() => TwinSlimeRuntime.Find(player)?.SetupTask.IsCompleted == true, "combination real lethal split");
        var pair = TwinSlimeRuntime.Find(player)!; var bodies = pair.Bodies.ToArray();
        check(bodies.Length == 2 && bodies.All(body => body.MaxHp == 1000 && body.CurrentHp == 1000) && puppet.IsAlive,
            "real lethal non-attack produces two bodies while the acquired guarding puppet remains alive");
        session.Me.History["burrow_sanctuary.charges"] = 1;
        int start = modifier.Turn; int puppetHp = puppet.CurrentHp;
        int grant = await BurrowEffect.Activate(endTurn: false);
        check(grant == 2000 && puppet.Block == 2000 && BurrowRules.Read(session.Me).Charges == 0,
            "one use grants each body's max-HP shield to the shared puppet without double spending");
        await Wait(() => bodies.All(body => BurrowVisuals.IsBuried(body) && BurrowVisuals.BodyOffset(body) > 70
            && BurrowVisuals.BodyOpacity(body) < 0.7f), "both actual slime bodies descend and fade");
        CombatEffectDisplay.Tick();
        check(bodies.All(body => body.GetCreatureNode()!.FindChildren("*", "", true, false).OfType<NPower>()
                .Any(power => power.Model is RaceDisplayPower { EffectId: "burrow_sanctuary" })),
            "both real body status bars show the same shared burrow charge state");
        await NativeAcceptanceVisuals.Capture("combination-01-both-buried");
        var hit = (await CreatureCmd.Damage(context, pair.Second, grant, ValueProp.Move | ValueProp.Unpowered, enemy, null, null)).ToArray();
        check(hit.Any(result => result.Receiver == puppet && result.WasBlockBroken) && puppet.Block == 0
            && puppet.CurrentHp == puppetHp && bodies.All(body => body.CurrentHp == 1000),
            "an attack aimed at the second body breaks the real puppet shield without damaging either body");
        check(BurrowRules.Read(session.Me).BlockBroken, "the actual puppet block-break hook marks the shared burrow use");
        PlayerCmd.EndTurn(player, canBackOut: false);
        await Wait(() => modifier.Turn == start + 1 && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Stunned
            && bodies.All(BurrowVisuals.IsDazed), "both emerging bodies receive the real skipped-turn daze");
        check(bodies.All(BurrowVisuals.IsDazed), "both bodies show native daze while exactly one shared player turn is skipped");
        await NativeAcceptanceVisuals.Capture("combination-02-both-dazed");
        await Wait(() => modifier.Turn >= start + 2 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready, "shared player recovers after one skipped turn");
        await Task.Delay(450);
        check(modifier.Turn == start + 2 && bodies.All(body => !BurrowVisuals.IsDazed(body)
            && Math.Abs(BurrowVisuals.BodyOffset(body)) < 1 && BurrowVisuals.BodyOpacity(body) > 0.95f),
            "both bodies recover their original position and opacity after exactly one missed turn");
        session.Me.History["burrow_sanctuary.charges"] = 1; puppet.LoseBlockInternal(puppet.Block);
        int intactStart = modifier.Turn;
        await BurrowEffect.Activate(endTurn: true);
        await Wait(() => modifier.Turn >= intactStart + 1 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready, "both intact bodies emerge on the next turn");
        await Task.Delay(450);
        check(modifier.Turn == intactStart + 1 && bodies.All(body => !BurrowVisuals.IsDazed(body)
            && Math.Abs(BurrowVisuals.BodyOffset(body)) < 1),
            "intact shared puppet shielding lets both bodies emerge without an extra skipped turn");
        await CreatureCmd.Kill(puppet, true);
        await Wait(() => !session.Me.Has(PuppetRules.Id), "acquired puppet item is removed on real follower death");
        session.Me.History["burrow_sanctuary.charges"] = 1;
        foreach (var body in bodies) body.LoseBlockInternal(body.Block);
        int independentGrant = await BurrowEffect.Activate(endTurn: false);
        check(independentGrant == 2000 && bodies.All(body => body.Block == 1000),
            "without a puppet each real body receives its own max-HP block");
        await CreatureCmd.Damage(context, pair.Second, 1000, ValueProp.Move | ValueProp.Unpowered, enemy, null, null);
        check(pair.First.Block == 1000 && pair.Second.Block == 0 && BurrowRules.Read(session.Me).BlockBroken,
            "breaking only the second body's independent shield marks the shared use without consuming the first body's block");
        await DebugTesting.WinBattle();
        await Wait(() => !session.Me.InBattle && TwinSlimeRuntime.Find(player) == null, "combination combat cleanup");
        check(BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready && !BurrowVisuals.IsBuried(player.Creature)
            && !BurrowVisuals.IsDazed(player.Creature), "victory while underground clears both split-body visuals and temporary state");
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "clean next combination combat");
        check(TwinSlimeRuntime.Find(player) == null && !BurrowVisuals.IsBuried(player.Creature)
            && !BurrowVisuals.IsDazed(player.Creature) && player.Creature.MaxHp == 2000,
            "the next fight begins unsplit at original max HP without stale underground motion or stun");
        await NativeAcceptanceVisuals.Capture("combination-03-clean-next-combat");
    }
}
