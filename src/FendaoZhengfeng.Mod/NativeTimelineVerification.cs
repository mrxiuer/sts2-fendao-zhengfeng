using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.TestSupport;

namespace FendaoZhengfeng.Mod;

/// <summary>Native fixture for real public card packets, selector cancellation, copies and receipt replay.</summary>
internal static class NativeTimelineVerification
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("验证失败：" + name);
        Godot.GD.Print("[FDZF TIMELINE PASS] " + name);
    }

    private static async Task Wait(Func<bool> ready, string reason)
    {
        long end = Environment.TickCount64 + 30000;
        while (!ready())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException(reason);
            await Task.Delay(30);
        }
    }

    private static List<CardModel> CombatCards(Player player) =>
        new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play }
            .SelectMany(pile => pile.GetPile(player).Cards).ToList();

    private static int FindSnapshotCardIndex(IReadOnlyList<MarketCard> cards, RunState run, Player player, Func<CardModel, bool> predicate)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            CardModel decoded = NativeCheckpoint.DecodeCard(cards[i].Packet, run, player);
            try { if (predicate(decoded)) return i; }
            finally { decoded.RemoveFromState(); }
        }
        return -1;
    }

    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var saveManager = new SaveManager(new MockGodotFileIo("user://fdzf-timeline-verification"));
            saveManager.InitProfileId(1); saveManager.InitSettingsDataForTest(); saveManager.InitPrefsDataForTest(); saveManager.InitProgressData();
            saveManager.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(saveManager);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";

            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), "FDZFTIMELINE");
            var session = RaceSession.Current!;
            var player = run.Players.Single();
            var modifier = RaceModifier.Active!;
            var ctx = new ThrowingPlayerChoiceContext();

            int initialTimelineCount = player.Deck.Cards.OfType<ExtraTimelineCard>().Count();
            string grantOne = Guid.NewGuid().ToString("N");
            var firstReceipt = new Receipt(grantOne + ":timeline", "timeline_card", grantOne);
            await modifier.ApplyTimelineReceipt(ctx, firstReceipt);
            int afterOne = player.Deck.Cards.OfType<ExtraTimelineCard>().Count();
            await modifier.ApplyTimelineReceipt(ctx, firstReceipt);
            Check(afterOne == initialTimelineCount + 1 && player.Deck.Cards.OfType<ExtraTimelineCard>().Count() == afterOne,
                "same timeline receipt replay adds exactly one permanent card");

            string grantTwo = Guid.NewGuid().ToString("N");
            await modifier.ApplyTimelineReceipt(ctx, new Receipt(grantTwo + ":timeline", "timeline_card", grantTwo));
            var grantedCards = player.Deck.Cards.OfType<ExtraTimelineCard>().ToArray();
            Check(grantedCards.Length == initialTimelineCount + 2 && grantedCards.All(card => card.CurrentUpgradeLevel == 0 && card.MaxUpgradeLevel == 0),
                "distinct boon acquisitions add one base card each without strengthening earlier cards");

            await MockTesting.GrantEffect("second_forge");
            var forged = player.Deck.Cards.First(card => card.Type == CardType.Skill && card.MaxUpgradeLevel >= 2 && card.DynamicVars.Values.OfType<BlockVar>().Any());
            CardCmd.Upgrade(forged); CardCmd.Upgrade(forged);
            Check(forged.CurrentUpgradeLevel == 2 && SecondForge.Bonus(forged) > 0,
                "real permanent skill carries a second-forge upgrade before public snapshot capture");

            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && session.Me.InBattle, "战斗回合未就绪。");

            // Mock peer 2 receives an actual PublicProfiles snapshot from this run. No online-state
            // flag is passed to TimelineRules or TimelineEffects, matching a retained snapshot after disconnect.
            await session.Submit(session.New("profile") with { Actor = "2", Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            var peer = session.Engine.State.Racers["2"];
            var retainedSnapshot = peer.MarketCards.ToArray();
            bool IsTimelinePacket(MarketCard stored)
            {
                var decoded = NativeCheckpoint.DecodeCard(stored.Packet, run, player);
                try { return decoded is ExtraTimelineCard; }
                finally { decoded.RemoveFromState(); }
            }
            var eligibleSnapshot = retainedSnapshot.Where(card => !IsTimelinePacket(card)).ToArray();
            int snapshotForgeIndex = FindSnapshotCardIndex(eligibleSnapshot, run, player,
                card => card.Id == forged.Id && card.CurrentUpgradeLevel == 2 && SecondForge.Bonus(card) == SecondForge.Bonus(forged));
            Check(snapshotForgeIndex >= 0 && TimelineRules.OpponentSnapshots(session.Engine.State, session.LocalId)
                    .Single(snapshot => snapshot.Id == "2").Cards.SequenceEqual(retainedSnapshot),
                "retained peer snapshot exposes real permanent packets, including second-forge data, without a live-connection check");

            var displayed = player.Creature.CombatState!.CreateCard<ExtraTimelineCard>(player);
            await CardPileCmd.AddGeneratedCardToCombat(displayed, PileType.Hand, player);
            await NativeAcceptanceVisuals.CaptureCard("timeline-01-native-card", displayed);
            var permanentBefore = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
            int energyBefore = player.PlayerCombatState!.Energy;
            int copiedForgeCount = CombatCards(player).Count(card => card.Id == forged.Id && card.DeckVersion == null);
            var chooseForge = new TestCardSelector(); chooseForge.PrepareToSelect(new[] { snapshotForgeIndex });
            using (CardSelectCmd.UseSelector(chooseForge))
                await TimelineEffects.PlayFromOpponent(ctx, player, "2");
            var copiedForge = CombatCards(player).LastOrDefault(card => card.Id == forged.Id && card.DeckVersion == null);
            Check(copiedForge != null && CombatCards(player).Count(card => card.Id == forged.Id && card.DeckVersion == null) == copiedForgeCount + 1
                && copiedForge.CurrentUpgradeLevel == 2 && SecondForge.Bonus(copiedForge) == SecondForge.Bonus(forged),
                "native card selector plays a free combat copy with both upgrades and second-forge bonus intact");
            Check(player.PlayerCombatState.Energy == energyBefore && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(permanentBefore)
                && session.Engine.State.Racers["2"].MarketCards.SequenceEqual(retainedSnapshot),
                "free copy spends no energy and leaves both permanent decks and the source snapshot unchanged");

            int copiesBeforeCardCancel = CombatCards(player).Count(card => card.Id == forged.Id && card.DeckVersion == null);
            var cancelCard = new TestCardSelector(); cancelCard.PrepareToSelect(Array.Empty<int>());
            using (CardSelectCmd.UseSelector(cancelCard))
                await TimelineEffects.PlayFromOpponent(ctx, player, "2");
            Check(CombatCards(player).Count(card => card.Id == forged.Id && card.DeckVersion == null) == copiesBeforeCardCancel
                && player.PlayerCombatState.Energy == energyBefore,
                "canceling the native card grid creates no combat copy and spends no copied-card resources");

            int targetIndex = FindSnapshotCardIndex(eligibleSnapshot, run, player,
                card => card.Type == CardType.Attack && card.TargetType == TargetType.AnyEnemy);
            Check(targetIndex >= 0, "real peer snapshot contains a native single-enemy card for target-cancel verification");
            var targetProbe = NativeCheckpoint.DecodeCard(eligibleSnapshot[targetIndex].Packet, run, player);
            string targetCardId = targetProbe.Id.ToString(); targetProbe.RemoveFromState();
            int targetCopiesBefore = CombatCards(player).Count(card => card.Id.ToString() == targetCardId && card.DeckVersion == null);
            var chooseTargetCard = new TestCardSelector(); chooseTargetCard.PrepareToSelect(new[] { targetIndex });
            using (CardSelectCmd.UseSelector(chooseTargetCard))
            {
                Task copyTask = TimelineEffects.PlayFromOpponent(ctx, player, "2");
                await Wait(() => NTargetManager.Instance.IsInSelection, "原生目标选择器未启动。");
                NTargetManager.Instance.CancelTargeting();
                await copyTask;
            }
            Check(CombatCards(player).Count(card => card.Id.ToString() == targetCardId && card.DeckVersion == null) == targetCopiesBefore
                && player.PlayerCombatState.Energy == energyBefore,
                "canceling native target selection removes the temporary clone before AutoPlay");
            Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(permanentBefore)
                && session.Engine.State.Racers["2"].MarketCards.SequenceEqual(retainedSnapshot),
                "target cancellation preserves the player's deck and the disconnected peer's retained snapshot");

            var timelinesOnly = retainedSnapshot.Where(IsTimelinePacket).ToList();
            Check(timelinesOnly.Count >= 2, "the real peer fixture contains timeline cards that must be excluded");
            peer.MarketCards = timelinesOnly;
            int cardsBeforeExcluded = CombatCards(player).Count;
            try
            {
                var excluded = TimelineEffects.PlayFromOpponent(ctx, player, "2");
                Check(await Task.WhenAny(excluded, Task.Delay(2000)) == excluded,
                    "a timeline-only opposing deck opens no useless card picker");
                await excluded;
                Check(CombatCards(player).Count == cardsBeforeExcluded && player.PlayerCombatState.Energy == energyBefore,
                    "excluding timeline cards creates no ineffective copies or copied-card resource cost");
            }
            finally { peer.MarketCards = retainedSnapshot.ToList(); }

            Godot.GD.Print("[FDZF TIMELINE COMPLETE] Native timeline checks passed using real public card packets.");
            game.GetTree().Quit(0);
        }
        catch (Exception error)
        {
            Godot.GD.PushError("[FDZF TIMELINE FAILED] " + error);
            game.GetTree().Quit(1);
        }
    }
}
