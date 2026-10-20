using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeHeartNetworkVerification
{
    private static void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); GD.Print("[FDZF HEART NETWORK PASS] " + text); }
    private static async Task Wait(Func<bool> test, string reason)
    { long until = System.Environment.TickCount64 + 80000; while (!test()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(30); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host)
    {
        string peer = host ? "2" : "1";
        bool PeerFlag(string key) => session.Engine.State.Racers[peer].UsedServices.Contains("node:" + key);
        while (run.CurrentActIndex < 4)
        {
            await RunManager.Instance.EnterAct(run.CurrentActIndex + 1, false);
            await session.Submit(session.New("progress", amount: run.Players.Single().Creature.CurrentHp, value: "Map"));
        }
        var player = run.Players.Single();
        await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 5000);
        var cardToUpgrade = player.Deck.Cards.First(card => card.IsUpgradable); CardCmd.Upgrade(cardToUpgrade);
        await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        await session.Submit(session.New("node_end", "heart-ready"));
        await Wait(() => PeerFlag("heart-ready"), "peer fifth-act profile ready");
        var donor = PublicProfiles.Read(session.Engine.State.Racers[peer])!;
        var expectedDonor = donor.Deck.Cards.Select(NativeCheckpoint.EncodeCard).Order().ToList();
        var ownDeck = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToList();
        Check(donor.Character.Id != player.Character.Id, "actual peers use different characters for cross-character copy");
        Check(run.Act.SecondBossEncounter is TowerHeartEncounter, "both peers share the new final heart encounter");
        await DebugTesting.EnterHeart();
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && session.Me.InBattle, "heart player turn");
        var heart = player.Creature.CombatState!.Enemies.Single().Monster as TowerHeart ?? throw new InvalidOperationException("heart missing");
        Check(heart.Creature.MaxHp == 1260, "both peers receive single-player heart HP with fifth-act scaling");
        async Task Advance()
        {
            int round = player.Creature.CombatState!.RoundNumber;
            CombatManager.Instance.SetReadyToEndTurn(player, false);
            await Wait(() => player.Creature.CombatState!.RoundNumber > round && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "advance own heart round " + round);
        }
        if (!host)
        {
            await Wait(() => PeerFlag("heart-copied"), "host independently reached copy phase");
            Check(player.Creature.CombatState!.RoundNumber == 1, "host advances to round five without advancing client combat");
        }
        while (player.Creature.CombatState!.RoundNumber < 5) await Advance();
        Check(heart.EchoDeck.Active, "real peer profile becomes a temporary local combat deck");
        var copied = CardPile.GetCards(player, PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust)
            .Where(card => card.Type is not (CardType.Status or CardType.Curse)).Select(NativeCheckpoint.EncodeCard).Order().ToList();
        var expectedPlayable = donor.Deck.Cards.Where(card => card.Type is not (CardType.Status or CardType.Curse)).Select(NativeCheckpoint.EncodeCard).Order().ToList();
        Check(copied.SequenceEqual(expectedPlayable), "copied cards preserve donor content and upgrades across actual ENet");
        Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(ownDeck), "own permanent deck is unchanged while using foreign copy");
        Check(PublicProfiles.Read(session.Engine.State.Racers[peer])!.Deck.Cards.Select(NativeCheckpoint.EncodeCard).Order().SequenceEqual(expectedDonor), "copy leaves remote permanent deck untouched");
        if (host)
        {
            var zap = CardPile.GetCards(player, PileType.Hand, PileType.Draw, PileType.Discard).First(card => card is MegaCrit.Sts2.Core.Models.Cards.Zap);
            await CardPileCmd.Add(zap, PileType.Hand, skipVisuals: true);
            await zap.OnPlayWrapper(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), null, false, default, true);
            Check(player.PlayerCombatState!.OrbQueue.Orbs.Count > 0, "copied Defect card functions for Ironclad using native cross-character orb support");
        }
        await session.Submit(session.New("node_end", "heart-copied"));
        if (host)
        {
            while (player.Creature.CombatState!.RoundNumber < 7) await Advance();
            await Wait(() => PeerFlag("heart-copied"), "client completed snapshot copy before host victory");
            await DebugTesting.WinBattle();
            await Wait(() => session.Me.Finished, "host heart finish");
            Check(session.Engine.State.FinishOrder.FirstOrDefault() == session.LocalId, "host first heart kill locks first place");
            await Wait(() => session.Engine.State.Racers[peer].Finished, "client continues after host finishes");
        }
        else
        {
            await Wait(() => session.Engine.State.Racers[peer].Finished, "host final victory synchronized");
            Check(heart.EchoDeck.Active && session.Me.InBattle, "opponent finishing does not invalidate already copied deck or stop local combat");
            while (player.Creature.CombatState!.RoundNumber < 7) await Advance();
            Check(!heart.EchoDeck.Active, "client restores its own exact combat piles after foreign snapshot phase");
            await DebugTesting.WinBattle();
            await Wait(() => session.Me.Finished, "client heart finish");
        }
        Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(ownDeck), "actual ENet victory never writes opponent cards to permanent deck");
        Check(session.Engine.State.FinishOrder.SequenceEqual(new[] { "1", "2" }), "both peers agree on final race ranking");
        await session.Submit(session.New("node_end", "heart-finished"));
        await Wait(() => PeerFlag("heart-finished"), "peer result checks complete");
        await Task.Delay(500);
        GD.Print("[FDZF HEART NETWORK COMPLETE] " + (host ? "host" : "client")); game.GetTree().Quit(0);
    }
}
