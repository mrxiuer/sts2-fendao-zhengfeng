using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>Opt-in verification in the actual Godot process, using the game's in-memory save backend.</summary>
internal static class NativeVerification
{
    private static bool started;
    public static void MaybeRun(NGame game)
    {
        if (started || System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") != "1") return;
        started = true;
        if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") is "host" or "client") _ = NativeNetworkVerification.Run(game);
        else _ = Verify(game);
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("验证失败：" + name); GD.Print("[FDZF VERIFY PASS] " + name); }
    private static async Task Verify(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            GD.Print("[FDZF VERIFY] Starting isolated native checks");
            var mock = new SaveManager(new MockGodotFileIo("user://fdzf-memory-verification"));
            mock.InitProfileId(1); mock.InitSettingsDataForTest(); mock.InitPrefsDataForTest(); mock.InitProgressData();
            mock.SetFtuesEnabled(false);
            SaveManager.MockInstanceForTesting(mock);
            const string seed = "FDZFVERIFY"; var session = RaceSession.CreateVerification(seed);
            foreach (var e in Catalog.All.Where(e => e.Kind == EffectKind.Curse || e.Kind == EffectKind.Contract))
                session.Me.Effects[e.Id] = new() { Id = e.Id, Grants = [new() { EventId = e.Id, Source = "2", ActiveBattle = 1 }] };
            session.Me.Effects["battle_hardened"] = new() { Id = "battle_hardened" }; session.Me.VictoryCount = 20;
            session.Me.Effects["death_defiance"] = new() { Id = "death_defiance" }; session.Me.RevivalCount = 2;
            session.Me.Effects["shared_misery"] = new() { Id = "shared_misery" };
            var modifier = ModelDb.Modifier<RaceModifier>().ToMutable();
            var run = await game.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), false, ActModel.GetDefaultList(), [modifier], seed, GameMode.Standard);
            Check(run.Players.Count == 1, "personal run has one player");
            Check(run.Players.Single().Relics.Any(r => r is LizardTail), "starting Lizard Tail granted");
            await RunManager.Instance.EnterRoomDebug(RoomType.Elite);
            var player = run.Players.Single();
            long until = System.Environment.TickCount64 + 30000;
            while ((RaceModifier.Active?.Turn ?? 0) < 1 || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play) { if (System.Environment.TickCount64 > until) throw new TimeoutException("回合启动超时。"); await Task.Delay(30); }
            await Task.Delay(250);
            var active = RaceModifier.Active!;
            Check(session.Me.BurdenSnapshot == 27, "curse burden activated at battle boundary");
            Check(player.Creature.GetPowerAmount<StrengthPower>() == -1, "strength curse applied");
            Check(player.Creature.GetPowerAmount<DexterityPower>() == -1, "dexterity curse applied");
            Check(player.Creature.GetPowerAmount<DoomPower>() == 3, "borrowed time applies Doom");
            Check(active.Dice is >= 80 and <= 110, "damage dice generated within range");
            Check(player.PlayerCombatState!.Energy >= 6, "combined opening energy applied");
            Check(PileType.Hand.GetPile(player).Cards.OfType<ClutterCard>().Count() == 2, "two clutter cards after normal draw");
            Check(player.Creature.MaxHp == 96, "historical revival max HP scaling");
            await CreatureCmd.GainBlock(player.Creature, 20, ValueProp.Unpowered, null);
            Check(player.Creature.Block == 0, "all-in prevents first-turn block");
            var ctx = new ThrowingPlayerChoiceContext();
            await CardPileCmd.AddGeneratedCardToCombat(run.CreateCard<Slimed>(player), PileType.Discard, null);
            Check(session.Engine.State.Racers["2"].PendingStatuses.Count == 1, "enemy-origin status queues for opponent");
            var source = player.Deck.Cards.First(); var packet = NativeCheckpoint.EncodeCard(source); var copy = NativeCheckpoint.DecodeCard(packet, run, player);
            Check(copy.Id == source.Id && copy.CurrentUpgradeLevel == source.CurrentUpgradeLevel, "native card serialization round trip");
            var enemy = player.Creature.CombatState!.Enemies[0];
            var attack = PileType.Hand.GetPile(player).Cards.First(c => c.Type == CardType.Attack);
            int beforeHp = player.Creature.CurrentHp;
            await CreatureCmd.Damage(ctx, enemy, 10, ValueProp.Unpowered, player.Creature, attack, null);
            int afterFirst = player.Creature.CurrentHp;
            await CreatureCmd.Damage(ctx, enemy, 10, ValueProp.Unpowered, player.Creature, attack, null);
            Check(afterFirst == beforeHp - 1 && player.Creature.CurrentHp == afterFirst, "barbs triggers once per card and enemy across multiple hits");
            int openingHp = enemy.MaxHp;
            await CreatureCmd.Damage(ctx, enemy, 9999, ValueProp.Unpowered, player.Creature, null, null);
            int cappedHp = enemy.CurrentHp;
            Check(cappedHp == openingHp - (int)Math.Ceiling(openingHp * 0.4), "hard shell caps actual native HP loss");
            await CreatureCmd.Damage(ctx, enemy, 9999, ValueProp.Unpowered, player.Creature, null, null);
            Check(enemy.CurrentHp == cappedHp, "hard shell does not reset between hits");
            session.Me.Ghosts = 1;
            await CreatureCmd.Damage(ctx, player.Creature, 9999, ValueProp.Unpowered, player.Creature.CombatState!.Enemies[0], null, null);
            Check(player.Creature.CurrentHp == 1 && session.Me.Ghosts == 0, "scapegoat preserves one HP and consumes one charge");
            Check(session.Me.RevivalCount == 2, "scapegoat does not count as revival");
            Check(!player.Relics.OfType<LizardTail>().Single().WasUsed, "scapegoat precedes native resurrection");
            GD.Print("[FDZF VERIFY COMPLETE] Native checks passed; no player save backend was used.");
            game.GetTree().Quit(0);
        }
        catch (Exception e) { GD.PushError("[FDZF VERIFY FAILED] " + e); game.GetTree().Quit(1); }
    }
}
