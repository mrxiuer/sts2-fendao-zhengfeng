using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeMechanicsVerification
{
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); GD.Print("[FDZF MECHANICS PASS] " + reason); }
    private static async Task Wait(Func<bool> predicate)
    { long until = System.Environment.TickCount64 + 40000; while (!predicate()) { if (System.Environment.TickCount64 > until) throw new TimeoutException("Mechanics transition"); await Task.Delay(25); } }
    private static async Task Capture(NGame game, string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(600); Directory.CreateDirectory(directory);
        game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png"));
    }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var memory = new SaveManager(new MockGodotFileIo("user://fdzf-mechanics-memory"));
            memory.InitProfileId(1); memory.InitSettingsDataForTest(); memory.InitPrefsDataForTest(); memory.InitProgressData(); memory.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(memory);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var character = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "formation"
                ? (CharacterModel)ModelDb.Character<Necrobinder>() : ModelDb.Character<Ironclad>();
            var run = await MockTesting.StartWithCharacter(character, "FDZFMECHANICS");
            var session = RaceSession.Current!; var player = run.Players.Single(); var modifier = RaceModifier.Active!;
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 5000);
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "combinations")
            {
                await NativeCombinationVerification.Run(run, Check);
                GD.Print("[FDZF COMBINATIONS COMPLETE] Native acquired-effect combination checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "puppet_life")
            {
                await NativePuppetLifeVerification.Run(run, Check);
                GD.Print("[FDZF PUPPET LIFE COMPLETE] Native puppet lifecycle checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "formation")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativeFormationVerification.Run(run, Check);
                GD.Print("[FDZF FORMATION COMPLETE] Native friendly formation checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "twin_slime")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativeTwinSlimeVerification.Run(run, Check);
                GD.Print("[FDZF TWIN SLIME COMPLETE] Native split-body checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "burrow")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativeBurrowVerification.Run(run, Check);
                game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "reroll")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                NativeRerollVerification.VerifyRules(); NativeRerollVerification.VerifyNativeGenerators(player);
                await NativeRewardClickVerification.Run(game, run, Check);
                GD.Print("[FDZF REROLL COMPLETE] Native reward generator checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "charm")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativeCharmVerification.Run(run, Check);
                GD.Print("[FDZF CHARM COMPLETE] Native charm checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "giant")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativeGiantVerification.Run(run, Check);
                GD.Print("[FDZF GIANT COMPLETE] Native giant checks passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "puppet_body")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                await NativePuppetBodyVerification.Run(run, Check);
                GD.Print("[FDZF PUPPET BODY COMPLETE] Native follower body passed."); game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") is "flight" or "gambler")
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
                bool gambler = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "gambler";
                if (gambler) { await NativeGamblerVerification.Run(run, Check); await Capture(game, "mechanics-05-gamblers-heart"); } else await NativeFlightVerification.Run(run, Check);
                GD.Print(gambler ? "[FDZF GAMBLER COMPLETE] Native critical mechanics passed." : "[FDZF FLIGHT COMPLETE] Native flight mechanics passed."); game.GetTree().Quit(0); return;
            }
            foreach (string id in new[] { "chaos_core", "piercing_edge", "resonant_circuit", "second_forge", "tail_refill" })
                Check(EffectIcons.Get(id).GetWidth() == 128, id + " has a packaged transparent display icon");
            await MockTesting.GrantEffect("tail_refill"); await PlayerCmd.GainGold(1000, player);
            var tail = player.Relics.OfType<LizardTail>().Single(); int gold = player.Gold;
            bool rejected = false; try { await AdvancedEffects.Click("tail_refill"); } catch (RuleException) { rejected = true; }
            Check(rejected && player.Gold == gold, "unspent tail cannot be refilled or charged");
            tail.WasUsed = true; await AdvancedEffects.Click("tail_refill");
            Check(player.Gold == gold - 350 && !tail.WasUsed && tail.Status == RelicStatus.Normal, "refill spends exactly 350 actual gold and restores native active relic status");
            await MockTesting.GrantEffect("second_forge");
            var attack = player.Deck.Cards.First(card => card.Type == CardType.Attack && card.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.DamageVar>().Any());
            CardCmd.Upgrade(attack); decimal damage = attack.DynamicVars.Damage.BaseValue; int cost = attack.EnergyCost.Canonical;
            CardCmd.Upgrade(attack);
            Check(attack.CurrentUpgradeLevel == 2 && attack.DynamicVars.Damage.BaseValue == damage + 3 && !attack.IsUpgradable, "second forge adds three attack damage and stops at level two");
            Check(attack.EnergyCost.Canonical == cost, "second forge leaves energy cost unchanged");
            var block = player.Deck.Cards.First(card => card.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar>().Any());
            CardCmd.Upgrade(block); decimal shield = block.DynamicVars.Block.BaseValue; CardCmd.Upgrade(block);
            Check(block.DynamicVars.Block.BaseValue == shield + 3, "second forge increases block by three");
            string packet = NativeCheckpoint.EncodeCard(attack); var decoded = NativeCheckpoint.DecodeCard(packet, run, player);
            Check(decoded.CurrentUpgradeLevel == 2 && decoded.DynamicVars.Damage.BaseValue == damage + 3, "second forge survives binary save and card transfer encoding"); decoded.RemoveFromState();
            var clone = run.CloneCard(attack);
            Check(SecondForge.Bonus(clone) == 3 && clone.DynamicVars.Damage.BaseValue == damage + 3, "card clones keep forge metadata without applying its bonus twice");
            clone.DowngradeInternal();
            var downgraded = NativeCheckpoint.DecodeCard(NativeCheckpoint.EncodeCard(clone), run, player);
            Check(downgraded.CurrentUpgradeLevel == 0 && SecondForge.Bonus(downgraded) == 0,
                "native downgrade clears forge metadata and remains serializable at level zero");
            CardCmd.Upgrade(downgraded); CardCmd.Upgrade(downgraded);
            Check(downgraded.CurrentUpgradeLevel == 2 && downgraded.DynamicVars.Damage.BaseValue == damage + 3,
                "downgraded card can forge again without stale bonus or missing damage");
            downgraded.RemoveFromState(); clone.RemoveFromState();
            var healingModel = ModelDb.AllCards.First(card => card.MaxUpgradeLevel == 1 && card.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.HealVar>().Any());
            var healing = run.CreateCard(healingModel, player); CardCmd.Upgrade(healing);
            decimal heal = healing.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.HealVar>().First().BaseValue;
            CardCmd.Upgrade(healing);
            Check(healing.DynamicVars.Values.OfType<MegaCrit.Sts2.Core.Localization.DynamicVars.HealVar>().First().BaseValue == heal + 3, "second forge increases native healing values by three"); healing.RemoveFromState();
            await MockTesting.GrantEffect("second_forge");
            Check(SecondForge.Bonus(attack) == 4 && attack.DynamicVars.Damage.BaseValue == damage + 4, "reinforcement updates previously forged cards without another upgrade or extra levels");
            session.Me.Effects["farewell_upgrades"] = new() { Id = "farewell_upgrades", Grants = [new() { ActiveBattle = 0 }] };
            var barred = player.Deck.Cards.First(card => card.CurrentUpgradeLevel == 0); CardCmd.Upgrade(barred);
            Check(barred.CurrentUpgradeLevel == 0 && !barred.IsUpgradable, "upgrade ban still takes precedence over second forge"); session.Me.Effects.Remove("farewell_upgrades");
            await MockTesting.GrantEffect("chaos_core");
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
            await Wait(() => modifier.ChaosChannels == 1 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
            Check(player.PlayerCombatState!.OrbQueue.Orbs.Count == 1 && player.PlayerCombatState.OrbQueue.Capacity >= 1, "non-Defect opening channels an actual native orb into a real slot");
            Check(NCombatRoom.Instance!.GetCreatureNode(player.Creature)?.OrbManager?.IsVisibleInTree() == true, "non-Defect native orb manager is visible");
            await Capture(game, "mechanics-01-native-orb-and-forge");
            var enemies = player.Creature.CombatState!.Enemies.ToArray();
            Check(enemies.Length >= 2, "multi-enemy native encounter is available for damage tests");
            foreach (var enemy in enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, 5000);
            for (int round = 2; round <= 5; round++)
            {
                CombatManager.Instance.SetReadyToEndTurn(player, false);
                await Wait(() => modifier.Turn >= round && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && (round != 5 || modifier.ChaosChannels == 2));
            }
            Check(modifier.ChaosChannels == 2, "base chaos interval channels on rounds one and five only");
            var chaosHolding = session.Me.Effects["chaos_core"]; session.Me.Effects.Remove("chaos_core");
            await MockTesting.GrantEffect("piercing_edge"); var target = enemies[0];
            foreach (var enemy in enemies) enemy.LoseBlockInternal(enemy.Block);
            await CreatureCmd.GainBlock(target, 100, ValueProp.Unpowered, null); int hp = target.CurrentHp;
            var results = (await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Move, player.Creature, attack, null)).ToList();
            Check(target.CurrentHp == hp - 3 && target.Block == 83 && results.Count == 1 && results[0].BlockedDamage == 17, "15 percent penetration bypasses block inside one native damage result");
            target.LoseBlockInternal(target.Block); await CreatureCmd.GainBlock(target, 2, ValueProp.Unpowered, null); hp = target.CurrentHp;
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Move, player.Creature, attack, null);
            Check(target.CurrentHp == hp - 18 && target.Block == 0, "partial block does not add a second penetration hit");
            await MockTesting.GrantEffect("piercing_edge");
            await CreatureCmd.GainBlock(target, 100, ValueProp.Unpowered, null); hp = target.CurrentHp;
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Move, player.Creature, attack, null);
            Check(target.CurrentHp == hp - 4 && target.Block == 84, "reinforced penetration bypasses twenty percent without extra hits"); target.LoseBlockInternal(target.Block);
            session.Me.Effects.Remove("piercing_edge"); await MockTesting.GrantEffect("resonant_circuit");
            var previousHp = enemies.Select(enemy => enemy.CurrentHp).ToArray();
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Unpowered, player.Creature, null, null);
            Check(target.CurrentHp == previousHp[0] - 20 && enemies.Skip(1).Select((enemy, i) => enemy.CurrentHp == previousHp[i + 1] - 4).All(ok => ok), "actual twenty HP damage conducts four to each other enemy without recursion");
            await MockTesting.GrantEffect("resonant_circuit"); previousHp = enemies.Select(enemy => enemy.CurrentHp).ToArray();
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Unpowered, player.Creature, null, null);
            Check(enemies.Skip(1).Select((enemy, i) => enemy.CurrentHp == previousHp[i + 1] - 5).All(ok => ok), "reinforcement increases conduction to twenty-five percent");
            await CreatureCmd.SetCurrentHp(target, 5); previousHp = enemies.Select(enemy => enemy.CurrentHp).ToArray();
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, 20, ValueProp.Unpowered, player.Creature, null, null);
            Check(target.IsDead && enemies.Skip(1).Select((enemy, i) => enemy.CurrentHp == previousHp[i + 1] - 1).All(ok => ok), "overkill does not inflate conducted actual HP loss");
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 99999, ValueProp.Unpowered, enemies.First(enemy => enemy.IsAlive), null, null);
            Check(tail.WasUsed && player.Creature.CurrentHp > 0 && session.Me.RevivalCount == 1, "refilled native Lizard Tail actually prevents the next lethal hit");
            gold = player.Gold; rejected = false; try { await AdvancedEffects.Click("tail_refill"); } catch (RuleException) { rejected = true; }
            Check(rejected && player.Gold == gold && tail.WasUsed, "combat refill is rejected without spending gold");
            await Capture(game, "mechanics-02-damage-and-native-status");
            await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle);
            session.Me.Effects["chaos_core"] = chaosHolding; await MockTesting.GrantEffect("chaos_core");
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
            await Wait(() => modifier.ChaosChannels == 1 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
            foreach (var enemy in player.Creature.CombatState!.Enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, 5000);
            for (int round = 2; round <= 4; round++)
            {
                CombatManager.Instance.SetReadyToEndTurn(player, false);
                await Wait(() => modifier.Turn >= round && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && (round != 4 || modifier.ChaosChannels == 2));
            }
            Check(modifier.ChaosChannels == 2 && Balance.ChaosInterval(session.Me.Effects["chaos_core"].Rank) == 3, "reinforced chaos channels on rounds one and four");
            await NativeEvilEchoVerification.Run(run, Check);
            await Capture(game, "mechanics-03-evil-echo");
            await NativeFlightVerification.Run(run, Check);
            await Capture(game, "mechanics-04-flight-wings");
            await NativeGamblerVerification.Run(run, Check);
            await Capture(game, "mechanics-05-gamblers-heart");
            GD.Print("[FDZF MECHANICS COMPLETE] Native item mechanics passed."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF MECHANICS FAILED] " + error); game.GetTree().Quit(1); }
    }
}
