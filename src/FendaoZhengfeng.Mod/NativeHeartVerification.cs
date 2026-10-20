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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeHeartVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); GD.Print("[FDZF HEART PASS] " + name); }
    private static async Task Wait(Func<bool> ready, string reason)
    { long until = System.Environment.TickCount64 + 40000; while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(30); } }
    private static async Task Capture(NGame game, string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(500); Directory.CreateDirectory(directory); game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png"));
    }
    internal static async Task Run(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            var memory = new SaveManager(new MockGodotFileIo("user://fdzf-heart-memory"));
            memory.InitProfileId(1); memory.InitSettingsDataForTest(); memory.InitPrefsDataForTest(); memory.InitProgressData(); memory.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(memory);
            RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = false; RaceConfig.DisabledEffects = "";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), "FDZFHEART");
            var session = RaceSession.Current!; var player = run.Players.Single(); var context = new ThrowingPlayerChoiceContext();
            Check(run.Acts[4].SecondBossEncounter is TowerHeartEncounter && run.Acts[3].SecondBossEncounter is not TowerHeartEncounter, "heart replaces only fifth act second boss");
            session.Me.Act = 4;
            await RunManager.Instance.EnterAct(4, false);
            var savedPreview = RunState.FromSerializable(RunManager.Instance.ToSave(null));
            Check(savedPreview.Acts.Count == 5 && savedPreview.Acts[4].SecondBossEncounter is TowerHeartEncounter, "native run serialization preserves custom final encounter");
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 5000);
            var donor = session.Engine.State.Racers["2"];
            donor.PublicPacket = PublicProfiles.Capture(run).Packet; donor.ProfileRevision++;
            string donorBefore = donor.PublicPacket;
            var permanentBefore = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToList();
            run.AddVisitedMapCoord(run.Map.SecondBossMapPoint!.coord);
            await RunManager.Instance.EnterRoomDebug(RoomType.Boss, model: ModelDb.Encounter<TowerHeartEncounter>().ToMutable());
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && session.Me.InBattle, "heart first turn");
            var heart = player.Creature.CombatState!.Enemies.Single().Monster as TowerHeart ?? throw new InvalidOperationException("wrong boss model");
            Check(heart.Creature.MaxHp == 1260, "heart HP is scaled once by fifth act multiplier");
            Check(NCombatRoom.Instance?.GetCreatureNode(heart.Creature)?.Visuals.Body.GetChildCount() >= 2, "custom heart sprite and motion replace native visuals");
            var ward = heart.Creature.GetPower<HeartWardPower>()!;
            Check(ward.Remaining == 315 && ward.HoverTips.Any() && heart.Creature.GetPower<HeartBeatPower>()!.HoverTips.Any(), "native status icons expose budget and beat descriptions");
            await CreatureCmd.Damage(context, heart.Creature, 9999, ValueProp.Unpowered, player.Creature, null, null);
            Check(heart.Creature.CurrentHp == 945 && ward.Remaining == 0, "large damage stops at shared quarter-health budget");
            await CreatureCmd.Damage(context, heart.Creature, 100, ValueProp.Unpowered, null, null, null);
            Check(heart.Creature.CurrentHp == 945, "direct HP damage cannot bypass exhausted ward");
            await Capture(game, "heart-01-opening");
            var motion = HeartPresentation.Motion(heart.Creature)!;
            float minScale = float.MaxValue, maxScale = 0;
            string? videoDirectory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
            void ObserveHeartbeat() { minScale = Math.Min(minScale, motion.Sprite.Scale.X); maxScale = Math.Max(maxScale, motion.Sprite.Scale.X); }
            var frameTree = game.GetTree(); frameTree.ProcessFrame += ObserveHeartbeat;
            try
            {
                for (int frame = 0; frame < 30; frame++)
                {
                    await Task.Delay(60);
                    if (!string.IsNullOrWhiteSpace(videoDirectory))
                    {
                        string frames = Path.Combine(videoDirectory, "heartbeat-frames"); Directory.CreateDirectory(frames);
                        game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(frames, $"frame-{frame:D3}.png"));
                    }
                }
            }
            finally { frameTree.ProcessFrame -= ObserveHeartbeat; }
            Check(maxScale - minScale >= 0.025f, $"native process animates a visible continuous heartbeat without player input ({minScale:F3}–{maxScale:F3})");
            CardModel? carriedWound = null;
            while (player.Creature.CombatState!.RoundNumber < 7)
            {
                int previous = player.Creature.CombatState.RoundNumber;
                CombatManager.Instance.SetReadyToEndTurn(player, false);
                await Wait(() => player.Creature.CombatState!.RoundNumber > previous && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "advance heart round " + previous);
                int round = player.Creature.CombatState.RoundNumber;
                Check(ward.Remaining == 315, "ward resets at player round " + round);
                if (round == 2) Check(player.Creature.GetPowerAmount<VulnerablePower>() > 0 && player.Creature.GetPowerAmount<WeakPower>() > 0, "opening applies native debuffs");
                if (round == 5)
                {
                    Check(heart.EchoDeck.Active && heart.EchoDeck.DonorName.Length > 0, "fourth enemy turn copies opponent combat deck");
                    Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(permanentBefore), "copy leaves own permanent deck unchanged");
                    Check(session.Engine.State.Racers["2"].PublicPacket == donorBefore, "copy does not mutate donor data");
                    Check(heart.Creature.Powers.All(power => power.HoverTips.Any()), "all native and custom heart powers format their hover descriptions after strengthening");
                    var status = player.Creature.CombatState.CreateCard<Wound>(player); carriedWound = status;
                    await CardPileCmd.AddGeneratedCardToCombat(status, PileType.Discard, null);
                    await Capture(game, "heart-02-copied-deck");
                }
                if (round == 6) Check(heart.EchoDeck.Active, "opponent copy lasts for two player turns");
                if (round == 7) Check(!heart.EchoDeck.Active && PileType.Discard.GetPile(player).Cards.Concat(PileType.Draw.GetPile(player).Cards).Concat(PileType.Hand.GetPile(player).Cards).Contains(carriedWound!), "seventh player turn restores original piles and retains new status cards");
            }
            await Capture(game, "heart-03-restored-deck");
            var slimed = player.Creature.CombatState!.CreateCard<Slimed>(player);
            await CardPileCmd.AddGeneratedCardToCombat(slimed, PileType.Hand, player);
            int hpBeforeBeat = player.Creature.CurrentHp;
            await slimed.OnPlayWrapper(context, null, false, default, true);
            Check(player.Creature.CurrentHp == hpBeforeBeat - 1, "native card play triggers one fixed beat damage without fifth-act amplification");
            while (player.Creature.CombatState!.RoundNumber < 17)
            {
                int previous = player.Creature.CombatState.RoundNumber;
                CombatManager.Instance.SetReadyToEndTurn(player, false);
                await Wait(() => player.Creature.CombatState!.RoundNumber > previous && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "later heart round " + previous);
                int round = player.Creature.CombatState.RoundNumber;
                if (round == 8) Check(heart.Creature.GetPowerAmount<HeartBeatPower>() == 2, "second buff raises beat to two");
                if (round == 11) Check(heart.Creature.HasPower<HeartStabsPower>(), "third buff exposes native painful-stabs status");
                if (round >= 12) Check(heart.WoundsThisAction is >= 1 and <= 3, "later attacks add wounds without exceeding per-action cap");
                if (round == 17) Check(heart.Creature.GetPowerAmount<StrengthPower>() == 40, "fifth buff applies the later twenty-two-strength escalation");
                if (round == 14) Check(heart.Creature.GetPowerAmount<StrengthPower>() == 18, "fourth buff adds twelve strength on top of earlier growth");
            }
            var combatPiles = new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust };
            var exactPiles = combatPiles.ToDictionary(pile => pile, pile => pile.GetPile(player).Cards.ToList());
            await heart.EchoDeck.CopyOpponent(heart);
            Check(NCombatRoom.Instance!.Ui.Hand.ActiveHolders.All(holder => PileType.Hand.GetPile(player).Cards.Contains(holder.CardModel)), "copy removes original retained-hand visuals instead of leaving stale cards");
            await heart.EchoDeck.Restore(player);
            Check(PileType.Hand.GetPile(player).Cards.All(card => NCombatRoom.Instance.Ui.Hand.GetCard(card) != null), "restore rebuilds playable native visuals for original hand cards");
            Check(combatPiles.All(pile => pile.GetPile(player).Cards.SequenceEqual(exactPiles[pile])), "manual restore preserves exact card instances and per-pile ordering including hand and exhaust");
            Check(exactPiles.Values.SelectMany(cards => cards).All(card => !card.HasBeenRemovedFromState), "original combat cards stay registered and reusable after restore");
            await PowerCmd.Apply<ArtifactPower>(context, player.Creature, 1, player.Creature, null);
            await heart.EchoDeck.CopyOpponent(heart);
            Check(!heart.EchoDeck.Active && !player.Creature.HasPower<ArtifactPower>() && combatPiles.All(pile => pile.GetPile(player).Cards.SequenceEqual(exactPiles[pile])), "native Artifact blocks deck-copy debuff without touching original piles");
            while (heart.Creature.CurrentHp > ward.Remaining)
            {
                await CreatureCmd.Damage(context, heart.Creature, 9999, ValueProp.Unpowered, player.Creature, null, null);
                int previous = player.Creature.CombatState!.RoundNumber;
                CombatManager.Instance.SetReadyToEndTurn(player, false);
                await Wait(() => player.Creature.CombatState!.RoundNumber > previous && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "normal lethal setup round");
            }
            await heart.EchoDeck.CopyOpponent(heart);
            Check(heart.EchoDeck.Active, "copy fixture active before lethal cleanup");
            await CreatureCmd.Damage(context, heart.Creature, 9999, ValueProp.Unpowered, player.Creature, null, null);
            Check(heart.Creature.IsDead && session.Engine.State.FinishOrder.Contains(session.LocalId), "normal HP damage locks rank before reward or cinematic completion");
            Check(!GodotObject.IsInstanceValid(motion) || !motion.Sprite.Visible, "confirmed death hides original heart before independent fragment animation");
            if (!string.IsNullOrWhiteSpace(videoDirectory)) await Task.Delay(1000);
            await Capture(game, "heart-04-victory");
            await CombatManager.Instance.CheckWinCondition();
            await Wait(() => !session.Me.InBattle, "heart victory settlement");
            Check(!heart.EchoDeck.Active, "boss death cleans up copied deck");
            Check(session.Me.Finished && session.Engine.State.FinishOrder.Contains(session.LocalId), "heart kill finishes fifth-act race through normal hooks");
            Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(permanentBefore), "heart victory never writes copied cards to permanent deck");
            GD.Print("[FDZF HEART COMPLETE] Native heart boss checks passed."); game.GetTree().Quit(0);
        }
        catch (Exception error) { GD.PushError("[FDZF HEART FAILED] " + error); game.GetTree().Quit(1); }
    }
}
