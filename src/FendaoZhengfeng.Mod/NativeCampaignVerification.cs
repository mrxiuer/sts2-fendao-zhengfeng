using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeCampaignVerification
{
    private static void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); GD.Print("[FDZF CAMPAIGN PASS] " + text); }
    private static async Task Wait(Func<bool> ready, string text)
    { long end = System.Environment.TickCount64 + 30000; while (!ready()) { if (System.Environment.TickCount64 > end) throw new TimeoutException(text); await Task.Delay(30); } }
    internal static string MapSignature(ActMap map) => string.Join(";", map.GetAllMapPoints().Append(map.BossMapPoint)
        .Concat(map.SecondBossMapPoint == null ? [] : new[] { map.SecondBossMapPoint }).OrderBy(point => point.coord.row).ThenBy(point => point.coord.col)
        .Select(point => $"{point.coord}:{point.PointType}:" + string.Join(",", point.Children.OrderBy(child => child.coord.row).ThenBy(child => child.coord.col).Select(child => child.coord.ToString()))));
    private static async Task Capture(NGame game, string name)
    {
        var directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(name.Contains("map", StringComparison.Ordinal) ? 6000 : 1200); Directory.CreateDirectory(directory);
        if (name.Contains("map", StringComparison.Ordinal) && MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance is { } mapScreen)
        {
            mapScreen.Open();
            HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), "_targetDragPos").SetValue(mapScreen, new Vector2(0, 1800));
            await Task.Delay(1200);
        }
        if (game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png")) != Error.Ok) throw new IOException("截图失败");
    }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var saves = new SaveManager(new MockGodotFileIo("user://fdzf-campaign-memory"));
            saves.InitProfileId(1); saves.InitSettingsDataForTest(); saves.InitPrefsDataForTest(); saves.InitProgressData(); saves.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(saves);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), "FDZFFIVEACTS");
            var session = RaceSession.Current!; var player = run.Players.Single(); var modifier = RaceModifier.Active!;
            Check(run.Acts.Count == 5 && !ReferenceEquals(run.Acts[1], run.Acts[3]) && !ReferenceEquals(run.Acts[2], run.Acts[4]), "five native mutable acts have independent room state");
            Check(run.Acts.Skip(3).All(act => act.HasSecondBoss && act.BossEncounter.Id != act.SecondBossEncounter!.Id), "both extra acts have two distinct preselected bosses");
            Check(Catalog.All.All(effect => EffectIcons.Get(effect.Id).GetWidth() == EffectIcons.TextureSize), "all fifty effect PNG resources load");
            foreach (var effect in Catalog.All)
            {
                var marker = (RaceDisplayPower)ModelDb.Power<RaceDisplayPower>().ToMutable(); marker.EffectId = effect.Id;
                Check(marker.HoverTips.Any(), "native status hover description loads: " + effect.Id);
            }
            async Task Battle(RoomType type = RoomType.Monster)
            {
                await RunManager.Instance.EnterRoomDebug(type);
                await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "battle did not start");
                foreach (var enemy in player.Creature.CombatState!.Enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, 5000);
            }
            async Task Win()
            {
                var room = run.CurrentRoom;
                await MockTesting.WinBattle();
                await Wait(() => !session.Me.InBattle && !CombatManager.Instance.IsInProgress && RaceRuntime.RewardRoom == room
                    && RaceRuntime.CombatRewards?.Rewards.All(reward => reward.IsPopulated) == true, "victory rewards did not settle");
            }
            async Task Play(CardType type, int spent = 0, bool automatic = false)
            {
                var model = player.Deck.Cards.First(card => card.Type == type);
                var card = player.Creature.CombatState!.CloneCard(model); card.DeckVersion = model;
                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
                var target = type == CardType.Attack ? player.Creature.CombatState.Enemies.First(enemy => enemy.IsAlive) : null;
                await card.OnPlayWrapper(new ThrowingPlayerChoiceContext(), target, automatic, new ResourceInfo { EnergySpent = spent, EnergyValue = spent, StarsSpent = 0, StarValue = 0 }, true);
            }
            foreach (string id in new[] { "sharpen", "light_step", "prepared", "poise", "restoration", "thin_armor", "exposed", "abrasion", "combat_cycle", "heavy_followup", "cornered", "combo_tax", "disorder" }) await MockTesting.GrantEffect(id);
            await MockTesting.GrantEffect("sharpen");
            await CreatureCmd.SetCurrentHp(player.Creature, 20);
            await Battle();
            Check(player.Creature.GetPowerAmount<StrengthPower>() == 2 && player.Creature.GetPowerAmount<DexterityPower>() == 1, "ordinary boons grant strength and dexterity with repeat scaling");
            Check(player.Creature.GetPowerAmount<FrailPower>() > 0 && player.Creature.GetPowerAmount<VulnerablePower>() > 0 && player.Creature.CurrentHp == 18, "ordinary curses apply statuses and a nonlethal two-HP payment at next battle");
            Check(modifier.CorneredUsed && player.Creature.Block >= 14 && PileType.Hand.GetPile(player).Cards.Count >= 7, "prepared poise and cornered expose opening block and extra draws");
            Check(!modifier.DisorderUsed, "initial deck shuffle does not trigger disorder");
            await CardPileCmd.Shuffle(new ThrowingPlayerChoiceContext(), player);
            Check(modifier.DisorderUsed && PileType.Draw.GetPile(player).Cards.Count(card => card is MegaCrit.Sts2.Core.Models.Cards.Dazed) == 2, "first reshuffle creates exactly two temporary Dazed cards");
            await modifier.AfterShuffle(new ThrowingPlayerChoiceContext(), player);
            Check(PileType.Draw.GetPile(player).Cards.Count(card => card is MegaCrit.Sts2.Core.Models.Cards.Dazed) == 2, "disorder cannot repeat in one battle");
            await Play(CardType.Attack); int blockBefore = player.Creature.Block;
            await Play(CardType.Skill);
            Check(modifier.CycleUsed && player.Creature.Block >= blockBefore + 4, "attack then skill triggers the once-per-turn combat cycle");
            await Play(CardType.Attack, 2);
            Check(modifier.HeavyUsed, "actual paid high-cost attack triggers heavy followup");
            int enemyBlock = player.Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive).Block;
            await Play(CardType.Skill);
            Check(modifier.ManualCards == 4 && player.Creature.CombatState.Enemies.All(enemy => enemy.Block >= 3), "fourth manual card taxes all live enemies once");
            int counted = modifier.ManualCards; await Play(CardType.Skill, automatic: true);
            Check(modifier.ManualCards == counted, "automatic card plays do not advance combo thresholds");
            CombatEffectDisplay.Tick(); await Capture(game, "campaign-01-tier-status");
            int hpBeforeWin = player.Creature.CurrentHp; await Win();
            Check(player.Creature.CurrentHp == hpBeforeWin + 8, "restoration adds two HP once alongside native Burning Blood healing");
            await MockTesting.GrantEffect("lone_crown"); await MockTesting.GrantEffect("desperate_echo");
            await Battle();
            Check(player.Creature.GetPowerAmount<StrengthPower>() == 4 && player.Creature.GetPowerAmount<DexterityPower>() == 3, "lone crown adds its battle stats alongside ordinary boons");
            int beforeHeal = player.Creature.CurrentHp; await CreatureCmd.Heal(player.Creature, 10);
            Check(player.Creature.CurrentHp == beforeHeal + 5, "lone crown halves ordinary healing");
            int playsBefore = CombatManager.Instance.History.CardPlaysFinished.Count(), hpBeforeEcho = player.Creature.CurrentHp;
            await Play(CardType.Attack, 2);
            Check(CombatManager.Instance.History.CardPlaysFinished.Count() == playsBefore + 2 && modifier.EchoUsed && player.Creature.CurrentHp == hpBeforeEcho - 2, "desperate echo actually replays once and charges two HP once");
            playsBefore = CombatManager.Instance.History.CardPlaysFinished.Count(); await Play(CardType.Attack, 2);
            Check(CombatManager.Instance.History.CardPlaysFinished.Count() == playsBefore + 1, "desperate echo cannot retrigger within the same turn");
            await Win();
            await RunManager.Instance.EnterRoomDebug(RoomType.RestSite); beforeHeal = player.Creature.CurrentHp;
            await CreatureCmd.Heal(player.Creature, 10);
            Check(player.Creature.CurrentHp == beforeHeal + 10, "camp healing is exempt from the lone-crown penalty");
            for (int act = 1; act < 5; act++)
            {
                await RunManager.Instance.EnterAct(act, false);
                Check(!session.Me.Finished && run.CurrentActIndex == act, "act " + (act + 1) + " enters without premature victory");
                if (act < 3) continue;
                string originalMap = MapSignature(run.Map);
                Check(run.Map.SecondBossMapPoint != null && originalMap == MapSignature(FiveActCampaign.CreateLateMap(run)), "late map is deterministic with a native double-boss connection");
                int nativeEliteCount = run.Act.GetMapPointTypes(new MegaCrit.Sts2.Core.Random.Rng(run.Rng.Seed, $"act_{act + 1}_map")).NumOfElites;
                Check(run.Map.GetAllMapPoints().Count(point => point.PointType == MapPointType.Elite) >= nativeEliteCount + act - 2, "late map retains its increased elite-node quota");
                if (act == 3) await Capture(game, "campaign-02-fourth-act-map");
                if (act == 4)
                {
                    await MockTesting.GrantEffect("expedition"); await Battle(RoomType.Elite); await Win();
                    Check(RaceRuntime.CombatRewards!.Rewards.OfType<RelicReward>().Count() == 2 && session.Me.ExpeditionRewards.Count == 0, "fifth-act expedition needs one elite and emits exactly one extra native relic reward");
                    var relic = RaceRuntime.CombatRewards.Rewards.OfType<RelicReward>().First(reward => RaceRuntime.RewardKey(reward).Contains("expedition:"));
                    string key = RaceRuntime.RewardKey(relic); await session.Submit(session.New("offer", key, boss: false, route: Route.Mixed));
                    Check(session.Engine.State.Offers.Values.Last().Candidates.All(id => Catalog.Get(id).Tier != Tier.Boss), "expedition elite relic can be sacrificed using the elite mixed pool");
                    run.Map = FiveActCampaign.CreateLateMap(run);
                }
                await RunManager.Instance.EnterMapCoord(run.Map.BossMapPoint.coord);
                await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "first boss did not start");
                var scaledEnemy = player.Creature.CombatState!.Enemies.First();
                Check(scaledEnemy.MaxHp == (int)Math.Ceiling(modifier.ResetBaseHp(scaledEnemy) * FiveActCampaign.ExtraMonsterMultiplier(act, 0)), "late-act monster HP receives the campaign multiplier once");
                Check(!FiveActCampaign.IsFinalBoss(run), "first boss is not the act-final boss");
                await Win();
                Check(!session.Me.KilledBossActs.Contains(act) && !session.Me.Finished && RaceRuntime.CombatRewards!.Rewards.OfType<RelicReward>().Count() == 1, "first boss gives a relic but no act-first prize or victory");
                await RunManager.Instance.EnterMapCoord(run.Map.SecondBossMapPoint!.coord);
                await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "second boss did not start");
                Check(FiveActCampaign.IsFinalBoss(run), "second boss is the act-final boss");
                await Win();
                Check(session.Me.KilledBossActs.Contains(act) && session.Me.BossPrizes.Any(prize => prize == act + ":first"), "second boss awards exactly one act-first prize");
                Check(session.Me.Finished == (act == 4), "only the fifth-act final boss locks the race finish");
            }
            var save = RunManager.Instance.ToSave(null); var roundtrip = RunState.FromSerializable(save);
            Check(roundtrip.Acts.Count == 5 && roundtrip.Acts[4].HasSecondBoss, "native checkpoint serializes all five acts and their boss encounter state");
            GD.Print("[FDZF CAMPAIGN COMPLETE] Five-act and effect checks passed using memory saves."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF CAMPAIGN FAILED] " + error); game.GetTree().Quit(1); }
    }
}
