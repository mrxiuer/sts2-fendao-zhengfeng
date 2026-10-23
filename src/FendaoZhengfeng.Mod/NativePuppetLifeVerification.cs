using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativePuppetLifeVerification
{
    private static async Task Wait(Func<bool> ready)
    { long end = Environment.TickCount64 + 40000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Puppet lifecycle transition"); await Task.Delay(25); } }
    internal static async Task Run(RunState run, Action<bool,string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single(); var hero = player.Creature;
        var modifier = RaceModifier.Active!; var context = new ThrowingPlayerChoiceContext();
        await CreatureCmd.SetMaxAndCurrentHp(hero, 80);
        session.Engine.State.EnabledEffects.Add(PuppetRules.Id);
        await MockTesting.GrantEffect(PuppetRules.Id);
        var initial = StateJson.Deserialize<PuppetVitals>(modifier.PuppetVitalsJson);
        check(initial.InitialMaxHp == 80 && initial.MaxHp == 80 && initial.CurrentHp == 80 && initial.Generation == 1,
            "native acquisition captures actual current maximum HP before any combat exists");
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
        var puppet = PuppetFollower.Guard(hero) ?? throw new InvalidOperationException("Puppet missing after opening"); var state = hero.CombatState!;
        check(puppet.MaxHp == 80 && puppet.CurrentHp == 80 && puppet.GetPowerAmount<BarricadePower>() == 1,
            "owned item creates a real guarding follower during native combat opening");
        foreach (var enemy in state.Enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, 1000);
        var opponent = state.Enemies.First();
        await CreatureCmd.Damage(context, hero, 15, ValueProp.Move, opponent, null, null);
        check(hero.CurrentHp == 80 && puppet.CurrentHp == 65, "an actual acquired follower intercepts native attacks");
        await CreatureCmd.GainBlock(hero, 8, ValueProp.Unpowered, null);
        int rawHeroBlock = (int)AccessTools.Field(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "_block").GetValue(hero)!;
        check(puppet.Block == 8 && hero.Block == 8 && rawHeroBlock == 0,
            "generated hero block lives only on the follower while cards read the logical protected block");
        var entrench = state.CreateCard<Entrench>(player);
        await CardPileCmd.AddGeneratedCardToCombat(entrench, PileType.Hand, player);
        await CardCmd.AutoPlay(context, entrench, null, AutoPlayType.Default);
        check(puppet.Block == 16 && hero.Block == 16, "actual native Entrench doubles follower block through the owner's card");
        int enemyHp = opponent.CurrentHp;
        var slam = state.CreateCard<BodySlam>(player);
        await CardPileCmd.AddGeneratedCardToCombat(slam, PileType.Hand, player);
        await CardCmd.AutoPlay(context, slam, opponent, AutoPlayType.Default);
        check(opponent.CurrentHp == enemyHp - 16, "actual native Body Slam deals damage using follower block");
        hero.LoseBlockInternal(hero.Block);
        await CharmPowerCmd.Apply<DexterityPower>(context, hero, 3, hero, null);
        await CharmPowerCmd.Apply<DexterityPower>(context, hero, -1, opponent, null);
        await CharmPowerCmd.Apply<FrailPower>(context, hero, 2, opponent, null);
        check(hero.GetPowerAmount<DexterityPower>() == 3 && puppet.GetPowerAmount<DexterityPower>() == -1
            && !hero.HasPower<FrailPower>() && puppet.GetPowerAmount<FrailPower>() == 2,
            "positive attributes stay on the hero while negative attributes and frail reach the follower");
        await CreatureCmd.GainBlock(hero, 20, ValueProp.Move, null);
        check(puppet.Block == 16, "native hero dexterity and follower negative dexterity plus frail modify transferred block once");
        await CreatureCmd.SetCurrentHp(hero, 60); await CreatureCmd.Heal(hero, 10);
        check(hero.CurrentHp == 60, "ordinary native healing is blocked while the acquired puppet is owned");
        hero.HealInternal(5);
        check(hero.CurrentHp == 60, "direct native healing cannot bypass the puppet restriction");
        await MockTesting.GrantEffect(PuppetRules.Id);
        check(puppet.MaxHp == 100 && puppet.CurrentHp == 85 && hero.CurrentHp == 60,
            "repeat acquisition adds one quarter of original max HP and heals only the follower by that amount");
        var stored = StateJson.Deserialize<PuppetVitals>(modifier.PuppetVitalsJson);
        var receipt = new Receipt(session.Me.History[PuppetRules.Id] + ":already-applied", "puppet_reinforce", stored.Generation.ToString(), 20);
        // Use an already recorded native receipt to prove the independent replay guard.
        var applied = StateJson.Deserialize<HashSet<string>>(modifier.PuppetReceiptsJson).Last();
        await modifier.ApplyPuppetReceipt(receipt with { Id = applied }, true);
        check(puppet.MaxHp == 100 && puppet.CurrentHp == 85, "replaying an applied reinforcement receipt cannot grow or heal twice");
        await NativeAcceptanceVisuals.Capture("puppet-life-01-guard-block-status");
        var save = RunManager.Instance.ToSave(null); var writer = new PacketWriter(); save.Serialize(writer);
        var bundle = new NativeCheckpoint.Bundle("0.5.2", Catalog.RulesVersion, session.Engine.State.Revision,
            StateJson.Serialize(session.Engine.State), Convert.ToBase64String(writer.Buffer,0,writer.BytePosition));
        var loaded = NativeCheckpoint.Restore(bundle).Run.Modifiers.OfType<RaceModifier>().Single();
        var loadedVitals = StateJson.Deserialize<PuppetVitals>(loaded.PuppetVitalsJson);
        check(loadedVitals == stored && loaded.PuppetReceiptsJson == modifier.PuppetReceiptsJson,
            "actual native run packet round-trip preserves follower HP, original base, generation and receipt markers");
        await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle);
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
        puppet = PuppetFollower.Guard(hero)!; opponent = hero.CombatState!.Enemies.First();
        check(puppet.MaxHp == 100 && puppet.CurrentHp == 85 && puppet.Block == 0 && puppet.GetPowerAmount<BarricadePower>() == 1,
            "next actual combat rebuilds the saved follower HP with fresh block and native barricade");
        check(!puppet.HasPower<FrailPower>(), "ordinary combat debuffs end with the prior fight while follower HP persists");
        var tail = player.Relics.OfType<LizardTail>().Single();
        await CreatureCmd.Damage(context, hero, 99999, ValueProp.Unpowered | ValueProp.Unblockable, opponent, null, null);
        check(tail.WasUsed && hero.CurrentHp > 0 && puppet.CurrentHp == 85,
            "native Lizard Tail resurrection remains legal despite ordinary healing being forbidden");
        int before = hero.CurrentHp;
        await CreatureCmd.Damage(context, hero, 100, ValueProp.Move, opponent, null, null);
        check(puppet.IsDead && hero.CurrentHp == before - 15 && !session.Me.Has(PuppetRules.Id) && modifier.PuppetVitalsJson == "null",
            "lethal interception spills excess damage and removes the same acquired item and saved follower state");
        before = hero.CurrentHp; await CreatureCmd.Heal(hero, 5);
        check(hero.CurrentHp == before + 5, "follower death immediately restores ordinary native healing");
        await MockTesting.GrantEffect(PuppetRules.Id);
        var next = PuppetFollower.Guard(hero)!;
        check(next != puppet && next.MaxHp == 80 && next.CurrentHp == 80
            && StateJson.Deserialize<PuppetVitals>(modifier.PuppetVitalsJson).Generation > stored.Generation,
            "reacquiring after death starts a new real follower and a new acquisition generation");
    }
}
