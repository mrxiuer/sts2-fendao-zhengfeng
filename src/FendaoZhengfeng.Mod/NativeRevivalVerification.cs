using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeRevivalVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); GD.Print("[FDZF REVIVAL PASS] " + name); }
    private static async Task Wait(Func<bool> condition)
    {
        long deadline = System.Environment.TickCount64 + 30000;
        while (!condition()) { if (System.Environment.TickCount64 > deadline) throw new TimeoutException("revival test combat transition"); await Task.Delay(30); }
    }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var memory = new SaveManager(new MockGodotFileIo("user://fdzf-revival-memory"));
            memory.InitProfileId(1); memory.InitSettingsDataForTest(); memory.InitPrefsDataForTest(); memory.InitProgressData(); memory.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(memory);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), "FDZFREVIVAL");
            var session = RaceSession.Current!; var player = run.Players.Single(); var modifier = RaceModifier.Active!;
            async Task Battle()
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
            }
            async Task LethalHit() => await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 9999,
                ValueProp.Unpowered, player.Creature.CombatState!.Enemies.First(), null, null);
            async Task Win()
            { await MockTesting.WinBattle(); await Wait(() => !session.Me.InBattle && !CombatManager.Instance.IsInProgress); }
            await Battle();
            int baseMax = player.Creature.MaxHp;
            Check(session.Me.RevivalCount == 0 && !session.Me.Has("death_defiance"), "revival history begins empty without the boon");
            await LethalHit();
            Check(player.Relics.OfType<LizardTail>().Single().WasUsed && player.Creature.CurrentHp > 0 && session.Me.RevivalCount == 1,
                "real Lizard Tail revival is recorded before acquiring death defiance");
            Check(player.Creature.MaxHp == baseMax, "recording an unowned boon does not grant its stats");
            await Win();
            Check(session.Me.VictoryCount == 1 && !session.Me.Has("battle_hardened"), "real victory history is tracked before acquiring battle hardened");
            int hpBefore = player.Creature.CurrentHp;
            await MockTesting.GrantEffect("death_defiance");
            Check(player.Creature.MaxHp == (int)Math.Floor(baseMax * 1.1m) && player.Creature.CurrentHp == hpBefore,
                "late death defiance acquisition applies historical max HP immediately without healing");
            await MockTesting.GrantEffect("battle_hardened");
            Check(Math.Abs(session.Me.Growth - 0.11) < 0.00001, "both retroactive boons include actual pre-acquisition events");
            await modifier.UpdateMaxHp(); await modifier.UpdateMaxHp();
            Check(player.Creature.MaxHp == (int)Math.Floor(baseMax * 1.1m), "reapplying historical max HP does not compound the bonus");
            await Battle();
            await PotionCmd.TryToProcure<FairyInABottle>(player);
            Check(player.Potions.Any(potion => potion is FairyInABottle), "native bottled fairy is available for a second independent revival source");
            await LethalHit();
            Check(session.Me.RevivalCount == 2 && player.Creature.CurrentHp > 0 && player.Potions.All(potion => potion is not FairyInABottle),
                "native bottled fairy is consumed and adds exactly one revival");
            Check(player.Creature.MaxHp == (int)Math.Floor(baseMax * 1.2m), "new revival while holding the boon updates max HP immediately");
            var attack = player.Deck.Cards.First(card => card.Type == CardType.Attack);
            var foe = player.Creature.CombatState!.Enemies.First();
            Check(Math.Abs(modifier.ModifyDamageMultiplicative(foe, 10, ValueProp.Unpowered, player.Creature, attack, null) - 1.21m) < 0.00001m
                && Math.Abs(modifier.ModifyBlockMultiplicative(player.Creature, 10, ValueProp.Unpowered, null, null) - 1.21m) < 0.00001m,
                "historical victories and revivals affect actual damage and block multipliers");
            await CreatureCmd.SetMaxAndCurrentHp(foe, 500);
            int foeHp = foe.CurrentHp;
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), foe, 100, ValueProp.Move, player.Creature, attack, null);
            Check(foeHp - foe.CurrentHp == 121, "native damage command actually deals the retroactive 21-percent bonus");
            int blockBefore = player.Creature.Block;
            await CreatureCmd.GainBlock(player.Creature, 100, ValueProp.Move, null);
            Check(player.Creature.Block - blockBefore == 121, "native block command actually grants the retroactive 21-percent bonus");
            await CreatureCmd.Heal(player.Creature, 1);
            Check(session.Me.RevivalCount == 2, "ordinary healing is not a revival");
            session.Me.Ghosts = 1;
            await LethalHit();
            Check(player.Creature.CurrentHp == 1 && session.Me.Ghosts == 0 && session.Me.RevivalCount == 2,
                "scapegoat prevents lethal damage without incrementing revival history");
            await Win();
            var restoredMatch = StateJson.Deserialize<Match>(StateJson.Serialize(session.Engine.State));
            var restoredRun = RunState.FromSerializable(RunManager.Instance.ToSave(null));
            Check(restoredMatch.Racers[session.LocalId].RevivalCount == 2 && restoredMatch.Racers[session.LocalId].VictoryCount == 2
                && restoredRun.Modifiers.OfType<RaceModifier>().Single().AppliedMaxHpBonus == modifier.AppliedMaxHpBonus,
                "actual history and applied max-HP bonus survive native and mode serialization");
            await Battle();
            await CreatureCmd.Kill(player.Creature, true);
            Check(player.Creature.IsDead && session.Me.RevivalCount == 2, "forced death without a successful restoration adds no revival");
            GD.Print("[FDZF REVIVAL COMPLETE] Real native revival and retrospective growth checks passed."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF REVIVAL FAILED] " + error); game.GetTree().Quit(1); }
    }
}
