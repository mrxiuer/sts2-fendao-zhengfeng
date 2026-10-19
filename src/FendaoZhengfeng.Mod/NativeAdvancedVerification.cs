using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeAdvancedVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("验证失败：" + name); GD.Print("[FDZF ADVANCED PASS] " + name); }
    private static async Task Wait(Func<bool> ready, string reason)
    { long end = System.Environment.TickCount64 + 30000; while (!ready()) { if (System.Environment.TickCount64 > end) throw new TimeoutException(reason); await Task.Delay(30); } }
    private static async Task Capture(NGame game, string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(1500); Directory.CreateDirectory(directory);
        if (game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png")) != Error.Ok) throw new IOException("截图失败。");
    }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var saveManager = new SaveManager(new MockGodotFileIo("user://fdzf-memory-verification"));
            saveManager.InitProfileId(1); saveManager.InitSettingsDataForTest(); saveManager.InitPrefsDataForTest(); saveManager.InitProgressData(); saveManager.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(saveManager);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), "FDZFADVANCED");
            var session = RaceSession.Current!; var player = run.Players.Single(); var modifier = RaceModifier.Active!;
            async Task Battle()
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && session.Me.InBattle, "战斗回合未就绪。");
            }
            async Task Win()
            { await MockTesting.WinBattle(); await Wait(() => !session.Me.InBattle && !CombatManager.Instance.IsInProgress, "胜利未结算。"); }
            int initialSkills = player.Deck.Cards.Count(card => card.Type == CardType.Skill);
            await MockTesting.GrantEffect("no_plan");
            Check(player.Deck.Cards.Count(card => card.Type == CardType.Skill) == initialSkills, "no plan remains pending until next battle");
            await Battle();
            Check(modifier.VaultCount == initialSkills && player.Deck.Cards.All(card => card.Type != CardType.Skill), "no plan removes all permanent skills into serialized vault");
            Check(new[] { PileType.Draw, PileType.Hand, PileType.Discard }.SelectMany(pile => pile.GetPile(player).Cards).All(card => card.Type != CardType.Skill), "no plan also removes combat copies before the opening draw");
            await Win();
            await Wait(() => RaceRuntime.CombatRewards?.Rewards.OfType<RaceActionReward>().Any() == true, "缺少技能取回奖励。");
            Check(modifier.CanRecoverSkill, "native post-victory rewards offer a recover-one choice");
            await modifier.RecoverSkill(0);
            Check(modifier.VaultCount == initialSkills - 1 && player.Deck.Cards.Count(card => card.Type == CardType.Skill) == 1 && !modifier.CanRecoverSkill, "recover one restores exactly one skill and cannot repeat within a victory");
            var actionReward = new RaceActionReward(player, 1); var serialized = actionReward.ToSerializable(); var writer = new PacketWriter(); serialized.Serialize(writer);
            var reader = new PacketReader(); reader.Reset(writer.Buffer.Take(writer.BytePosition).ToArray()); var decoded = new SerializableReward(); decoded.Deserialize(reader);
            Check(Reward.FromSerializable(decoded, player) is RaceActionReward, "custom N-choice reward survives native packet serialization");

            Check(new MegaCrit.Sts2.Core.Localization.LocString(ModEntry.Id, "action.remove.title").GetFormattedText().Contains("3点最大生命"), "small-and-light native card-selector title shows the actual three-max-HP price");
            await MockTesting.GrantEffect("small_light");
            var toRemove = player.Deck.Cards.First(card => card.Type == CardType.Attack);
            int beforeCount = player.Deck.Cards.Count, beforeMax = player.Creature.MaxHp;
            await modifier.RemoveWithSmallLight(toRemove); await modifier.UpdateMaxHp();
            Check(player.Deck.Cards.Count == beforeCount - 1 && player.Creature.MaxHp == beforeMax - 3 && modifier.SmallLightLoss == 3, "small and light removes the chosen permanent card and exactly three max HP");
            int afterRemovalMax = player.Creature.MaxHp;
            await CreatureCmd.SetMaxHp(player.Creature, 3);
            bool lowMaxRejected = false;
            try { await modifier.RemoveWithSmallLight(player.Deck.Cards.First()); } catch (RuleException) { lowMaxRejected = true; }
            Check(lowMaxRejected && player.Deck.Cards.Count == beforeCount - 1 && modifier.SmallLightLoss == 3 && player.Creature.MaxHp == 3, "small and light rejects insufficient max HP without removing a card or charging again");
            await CreatureCmd.SetMaxHp(player.Creature, afterRemovalMax);
            await MockTesting.GrantEffect("farewell_upgrades");
            var upgraded = player.Deck.Cards.First(card => card.IsUpgradable); CardCmd.Upgrade(upgraded);
            int preservedUpgrade = upgraded.CurrentUpgradeLevel;
            Check(preservedUpgrade > 0 && !session.Me.Has("farewell_upgrades"), "pending global upgrade curse preserves current upgrade access");
            await MockTesting.GrantEffect("bloom_decay");
            await MockTesting.GrantEffect("good_connections");
            await MockTesting.GrantEffect("r_key");
            await PlayerCmd.GainGold(1000, player);
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            var sale = player.Deck.Cards.First(card => card != upgraded && card.Type == CardType.Attack);
            string saleId = modifier.CardIdentity(sale);
            var packet = NativeCheckpoint.EncodeCard(upgraded);
            var otherProfile = PublicProfiles.Capture(run) with { Gold = 1000, Cards = [new("other-card", packet, upgraded.Title, 3)] };
            await session.Submit(session.New("profile") with { Actor = "2", Profile = StateJson.Serialize(otherProfile) });
            int beforeGold = player.Gold;
            await AdvancedEffects.BuyCard("2", "other-card");
            Check(player.Gold == beforeGold - 100 && session.Me.Purchases == 1 && !session.Engine.State.Trades.Single().Delivered, "market charges 100 native gold and defers delivery");
            Check(player.Deck.Cards.Count == beforeCount - 1 && session.Engine.State.Racers["2"].Receipts.Any(receipt => receipt.Kind == "gold" && receipt.Amount == 100), "purchase pays original owner and does not immediately change either deck");
            session.Engine.State.Racers["2"].Effects["good_connections"] = new() { Id = "good_connections" };
            await session.Submit(session.New("market_buy", session.LocalId, saleId, 1000) with { Actor = "2" });
            Check(modifier.IsReserved(sale), "sold card identity is reserved until the seller's next battle");
            await Battle();
            Check(!player.Deck.Cards.Contains(sale) && !new[] { PileType.Hand, PileType.Draw, PileType.Discard }.SelectMany(pile => pile.GetPile(player).Cards).Any(card => card.DeckVersion == sale), "seller next battle removes the original and its combat copies");
            var bought = player.Deck.Cards.Single(card => modifier.CardIdentity(card).StartsWith("bought-", StringComparison.Ordinal));
            Check(bought.CurrentUpgradeLevel == preservedUpgrade && modifier.RemainingWear(bought) == 3, "buyer next battle adds the purchased upgraded card with its durability");
            Check(session.Engine.State.Trades.Single(trade => trade.Buyer == session.LocalId).Delivered && session.Engine.State.Trades.Single(trade => trade.Seller == session.LocalId).Removed, "host state records both independent native transfer completions");
            Check(modifier.VaultCount == initialSkills - 1 && player.Deck.Cards.Any(card => card.Type == CardType.Skill), "no plan does not repeatedly confiscate recovered or newly bought skills");
            Check(!player.Deck.Cards.Any(card => card.IsUpgradable) && upgraded.CurrentUpgradeLevel == preservedUpgrade, "active global curse blocks future upgrades while retaining existing ones");
            int previousLevel = bought.CurrentUpgradeLevel; bought.UpgradeInternal();
            Check(bought.CurrentUpgradeLevel == previousLevel, "direct native upgrade execution also respects the global curse");
            var savedUpgrade = NativeCheckpoint.DecodeCard(packet, run, player);
            Check(savedUpgrade.CurrentUpgradeLevel == preservedUpgrade, "rehydrating an upgraded card under the ban retains its saved upgrade level"); savedUpgrade.RemoveFromState();
            CombatEffectDisplay.Tick();
            Check(CombatEffectDisplay.Count > 0 && player.Creature.Powers.All(power => power is not RaceDisplayPower), "native status-bar markers are visible-only and never enter gameplay powers");
            var skill = player.Deck.Cards.First(card => card.Type == CardType.Skill);
            var copy = player.Creature.CombatState!.CloneCard(skill); copy.DeckVersion = skill;
            await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Hand, player);
            int originalWear = modifier.RemainingWear(skill)!.Value;
            await copy.OnPlayWrapper(new ThrowingPlayerChoiceContext(), null, false, default, true);
            Check(modifier.RemainingWear(skill) == originalWear - 1 && modifier.RemainingWear(copy) == originalWear - 1, "a real native manual card play updates both deck and combat-copy durability");
            var displayCard = NCard.Create(skill)!; game.AddChild(displayCard); displayCard.Position = new Vector2(960, 560); displayCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
            CardDurabilityUi.Update(displayCard);
            Check(CardDurabilityUi.ValueOn(displayCard) == (originalWear - 1).ToString(), "native card view exposes the exact remaining durability number");
            await Capture(game, "advanced-01-card-durability"); displayCard.QueueFree();
            await Capture(game, "advanced-02-combat-status");
            await MockTesting.GrantEffect("carapace");
            var choose = RaceUi.ChooseCleanse(CleanseAt.Shop);
            await Task.Delay(200);
            var overlay = game.GetTree().Root.FindChildren("FdzfNativeChoice", "", true, false).OfType<Control>().Last();
            var row = overlay.FindChildren("*", "", true, false).OfType<NRewardButton>().First();
            AccessTools.Method(typeof(NRewardButton), "OnFocus").Invoke(row, null);
            await Capture(game, "advanced-03-native-cleanse-tooltip");
            AccessTools.Method(typeof(NRewardButton), "OnRelease").Invoke(row, null);
            Check(await choose != null, "native reward row hover and selection complete the cleanse picker");
            await Win();
            string deckBefore = StateJson.Serialize(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToList());
            string relicsBefore = string.Join("|", player.Relics.Select(relic => relic.Id.ToString()));
            int hpBefore = player.Creature.CurrentHp, maxBefore = player.Creature.MaxHp, goldBefore = player.Gold;
            bool tailBefore = player.Relics.OfType<LizardTail>().Single().WasUsed;
            int stashBefore = modifier.VaultCount;
            run.Acts[3].MarkRoomVisited(RoomType.Boss); run.Acts[4].MarkRoomVisited(RoomType.Boss);
            await AdvancedEffects.ResetRun();
            Check(run.Acts.Skip(3).All(act => act.PullNextEncounter(RoomType.Boss).Id == act.BossEncounter.Id), "R key resets native double-boss encounter cursors to the first boss");
            Check(run.CurrentActIndex == 0 && run.ActFloor == 0 && session.Me.ResetCount == 1 && session.Me.ResetCharges == 0 && !session.Me.ResetPending, "R key returns to act-one map start and consumes exactly one charge");
            Check(player.Creature.CurrentHp == hpBefore && player.Creature.MaxHp == maxBefore && player.Gold == goldBefore
                && string.Join("|", player.Relics.Select(relic => relic.Id.ToString())) == relicsBefore
                && player.Relics.OfType<LizardTail>().Single().WasUsed == tailBefore
                && StateJson.Serialize(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToList()) == deckBefore && modifier.VaultCount == stashBefore,
                "R key preserves actual player HP, max HP, gold, relics, Lizard Tail use, exact deck packets and skill vault");
            await Capture(game, "advanced-04-reset-map");
            await Battle();
            var enemy = player.Creature.CombatState!.Enemies.First(); int scaled = enemy.MaxHp;
            Check(scaled == (int)Math.Ceiling(modifier.ResetBaseHp(enemy) * 1.2m)
                && modifier.ModifyDamageMultiplicative(player.Creature, 10, ValueProp.Unpowered, enemy, null, null) == 1.2m
                && modifier.ModifyBlockMultiplicative(enemy, 10, ValueProp.Unpowered, null, null) == 1.2m, "R key scales native monster health, damage and block by 20 percent");
            await modifier.AfterCreatureAddedToCombat(enemy); Check(enemy.MaxHp == scaled, "monster reset scaling is applied once per creature");
            var alliedSummon = new MegaCrit.Sts2.Core.Entities.Creatures.Creature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Osty>().ToMutable(), CombatSide.Player, null);
            int alliedMax = alliedSummon.MaxHp; await modifier.AfterCreatureAddedToCombat(alliedSummon);
            Check(alliedSummon.MaxHp == alliedMax && modifier.ModifyDamageMultiplicative(player.Creature, 10, ValueProp.Unpowered, alliedSummon, null, null) == 1m
                && modifier.ModifyBlockMultiplicative(alliedSummon, 10, ValueProp.Unpowered, null, null) == 1m, "R and late-act monster scaling exclude allied monster summons such as Osty");
            await Win();
            session.Me.BossPrizes.Add("advanced:later");
            await session.Submit(session.New("boss_cleanse", "advanced:later", "no_plan"));
            await modifier.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
            Check(modifier.VaultCount == 0 && player.Deck.Cards.Count(card => card.Type == CardType.Skill) >= initialSkills, "cleansing no plan restores every remaining saved skill without duplication");
            GD.Print("[FDZF ADVANCED COMPLETE] Native advanced checks passed using memory saves."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF ADVANCED FAILED] " + error); game.GetTree().Quit(1); }
    }
}
