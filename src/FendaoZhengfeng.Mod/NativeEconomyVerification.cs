using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;

namespace FendaoZhengfeng.Mod;

internal static class NativeEconomyVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); GD.Print("[FDZF ECONOMY PASS] " + name); }
    private static async Task Wait(Func<bool> ready)
    { long until = System.Environment.TickCount64 + 40000; while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException("Economy battle did not settle."); await Task.Delay(30); } }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var memory = new SaveManager(new MockGodotFileIo("user://fdzf-economy-memory"));
            memory.InitProfileId(1); memory.InitSettingsDataForTest(); memory.InitPrefsDataForTest(); memory.InitProgressData(); memory.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(memory);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Defect>(), "FDZFECONOMY");
            var session = RaceSession.Current!; var player = run.Players.Single();
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
            bool Stats(int n) => player.Creature.GetPowerAmount<StrengthPower>() == n && player.Creature.GetPowerAmount<DexterityPower>() == n && player.Creature.GetPowerAmount<FocusPower>() == n;
            async Task Battle()
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
            }
            async Task Win() { await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle); }
            await MockTesting.GrantEffect("war_feeds_war"); await Battle();
            Check(Stats(1), "one acquisition grants one native Strength Dexterity and Focus at battle start");
            int round = player.Creature.CombatState!.RoundNumber;
            CombatManager.Instance.SetReadyToEndTurn(player, false);
            await Wait(() => player.Creature.CombatState!.RoundNumber > round && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
            Check(Stats(1), "ordinary next turn does not add another stack");
            await Win();
            await MockTesting.GrantEffect("war_feeds_war"); await MockTesting.GrantEffect("war_feeds_war");
            await MockTesting.GrantEffect("adversity_profit");
            await MockTesting.GrantEffect("iron_curtain"); await MockTesting.GrantEffect("iron_curtain");
            await MockTesting.GrantEffect("carapace"); await MockTesting.GrantEffect("hard_shell");
            await Battle(); Check(Stats(3), "three acquisitions grant exactly three native attributes in the next battle without carrying prior combat powers");
            Check(EffectDetails.Describe("war_feeds_war").Contains(L10n.F($"\n当前 {3}/3 层。")), "hover description exposes current stack cap and attributes");
            await Win(); await MockTesting.EnterService(RoomType.RestSite);
            int before = player.Gold;
            await session.Submit(session.New("camp_cleanse", RaceRuntime.RoomKey, "iron_curtain"));
            await RaceModifier.Active!.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
            Check(player.Gold == before && session.Me.Effects["adversity_profit"].Used == 9, "victory pays nine gold for three curse tiers and camp cleansing pays no extra gold");
            Check(EffectDetails.Describe("adversity_profit").Contains("9/800") && !EffectDetails.Describe("adversity_profit").Contains(L10n.T("\n持续至自己的下一次幕Boss击杀。")), "adversity tooltip shows lifetime eight-hundred-gold cap without act expiry");
            await RunManager.Instance.EnterAct(1, false);
            Check(session.Me.Has("adversity_profit") && session.Me.Effects["adversity_profit"].Used == 9, "native act transition preserves adversity and its claimed total");
            GD.Print("[FDZF ECONOMY COMPLETE] Native effect checks passed."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF ECONOMY FAILED] " + error); game.GetTree().Quit(1); }
    }
}
