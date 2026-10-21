using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRewardRecoveryVerification
{
    private sealed record Expected(SavedCombatRewards Rewards, int Gold, int Victories, int DeckSize);
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); GD.Print("[FDZF REWARD RECOVERY PASS] " + name); }
    private static async Task Wait(Func<bool> condition, string reason)
    { long deadline = System.Environment.TickCount64 + 45000; while (!condition()) { if (System.Environment.TickCount64 > deadline) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        bool peerReady = false, peerDone = false;
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        { if (sender == session.Transport.NetId) return; if (message.Kind == "reward_test_ready") peerReady = true; if (message.Kind == "reward_test_done") peerDone = true; });
        var player = run.Players.Single();
        string expectedPath = Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "expected-" + session.LocalId + ".json");
        if (!resume)
        {
            if (host)
            {
                foreach (var racer in session.Engine.State.Racers.Values) racer.BossPrizes.Add("reward-fixture:first");
                await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            }
            await Wait(() => session.Me.BossPrizes.Count > 0, "both peers receive mode reward fixture");
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
            var point = run.Map.StartingMapPoint.Children.OrderBy(point => point.coord.ToString(), StringComparer.Ordinal).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
            await Wait(() => session.Me.InBattle, "initial combat");
            await DebugTesting.WinBattle();
            await Wait(() => run.CurrentRoom is CombatRoom { IsPreFinished: true } && RaceRuntime.CombatRewards != null, "native combat rewards generated");
            var rewards = RaceRuntime.CombatRewards!;
            var relic = new RelicReward(player); relic.Populate(); rewards.Rewards.Add(relic);
            rewards.Rewards.Add(new RelicReward(ModelDb.Relic<MoltenEgg>().ToMutable(), player));
            var potion = new PotionReward(player); potion.Populate(); rewards.Rewards.Add(potion);
            var nativeCards = rewards.Rewards.OfType<CardReward>().First();
            var options = (CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions").Invoke(nativeCards, null)!;
            var regular = new CardReward(CardCreationOptions.ForRoom(player, RoomType.Monster).WithFlags(CardCreationFlags.IsFromCombat), 3, player);
            regular.Populate(); rewards.Rewards.Add(regular);
            var customCard = run.CreateCard(ModelDb.GetById<CardModel>(nativeCards.Cards.First().Id), player); CardCmd.Upgrade(customCard); SecondForge.Apply(customCard, 3);
            rewards.Rewards.Add(new CardReward([customCard], options.Source, player, options) { CanReroll = true, CanSkip = false });
            rewards.Rewards.Add(new RaceActionReward(player, 2));
            rewards.Rewards.Add(new LinkedRewardSet([new GoldReward(17, player), new GoldReward(29, player)], player));
            var taken = new GoldReward(777, player); rewards.Rewards.Add(taken);
            int beforeGold = player.Gold;
            Check(await taken.SelectUnsynchronized() && taken.SuccessfullySelected && player.Gold == beforeGold + 777,
                "actual gold reward is taken through normal native selection before save");
            var captured = RewardRecovery.Capture((CombatRoom)run.CurrentRoom!)!;
            Check(captured.Rewards.All(entry => entry.Key != RaceRuntime.RewardKey(taken)), "selected gold reward is absent from pending snapshot");
            Check(captured.Rewards.Count(entry => entry.Cards != null) >= 2 && captured.Rewards.Any(entry => entry.Relic != null)
                && captured.Rewards.Any(entry => entry.Potion != null) && captured.Rewards.Any(entry => entry.Children?.Length == 2),
                "snapshot contains concrete card choices, relic, potion and linked rewards");
            RaceSession.AtomicWrite(expectedPath, StateJson.Serialize(new Expected(captured, player.Gold, session.Me.VictoryCount, player.Deck.Cards.Count)));
            session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
            session.Transport.SendMessage(new RaceMessage { Kind = "reward_test_ready" });
            await Wait(() => peerReady, "peer completed native reward fixture");
            if (host)
            {
                await Wait(() => session.Recovery.BackupCount == 2, "both reward backups reached host");
                await Wait(() => session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0,
                    "peer disconnects while its exact remaining rewards are only stored by host");
                await session.Recovery.SaveHost();
                Check(RaceRecovery.Read("1").Players.Values.All(snapshot => snapshot.Rewards != null), "host archive includes exact remaining rewards for both players");
            }
            else
            {
                session.Transport.SendMessage(new RaceMessage { Kind = "reward_test_done" });
                await Task.Delay(300); session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit);
                GD.Print("[FDZF REWARD RECOVERY COMPLETE] client disconnected with pending rewards"); game.GetTree().Quit(0); return;
            }
        }
        else
        {
            await Wait(() => run.CurrentRoom is CombatRoom { IsPreFinished: true } && RaceRuntime.CombatRewards != null, "restored reward room");
            var expected = StateJson.Deserialize<Expected>(File.ReadAllText(expectedPath));
            var rewards = RaceRuntime.CombatRewards!;
            var actual = RewardRecovery.Capture((CombatRoom)run.CurrentRoom!)!;
            RaceSession.AtomicWrite(expectedPath + ".actual", StateJson.Serialize(actual));
            Check(StateJson.Serialize(actual) == StateJson.Serialize(expected.Rewards), "all pending choices, upgraded values, identities and flags survive host resume exactly");
            Check(player.Gold == expected.Gold && player.Deck.Cards.Count == expected.DeckSize, "taken gold and native deck survive without paying rewards twice");
            Check(session.Me.VictoryCount == expected.Victories && !CombatManager.Instance.IsInProgress && !session.Me.InBattle,
                "finished combat does not restart or settle victory again");
            Check(rewards.Rewards.OfType<RaceActionReward>().Count() == 1, "mode extra reward is restored once");
            var extra = rewards.Rewards.OfType<RaceActionReward>().Single(); int shields = session.Me.Shields;
            var selection = extra.SelectUnsynchronized();
            await Wait(() => NativeChoiceUi.ActiveVeil != null, "restored mode choice opens native picker");
            var shieldRow = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false).OfType<NRewardButton>().ElementAt(1);
            AccessTools.Method(typeof(NRewardButton), "OnRelease").Invoke(shieldRow, null);
            Check(await selection && session.Me.Shields == shields + 1, "restored mode choice awards exactly one curse shield through real native picker");
            Check(!await extra.SelectUnsynchronized() && session.Me.Shields == shields + 1,
                "completed mode choice cannot be claimed again after restoration");
            var card = rewards.Rewards.OfType<CardReward>().Single(reward => reward.Cards.Count() == 1);
            Check(card.Cards.Single().CurrentUpgradeLevel == 2 && SecondForge.Bonus(card.Cards.Single()) == 3 && card.CanReroll && !card.CanSkip,
                "manually populated forged choice keeps its native card and selection constraints");
            var ordinaryChoices = rewards.Rewards.OfType<CardReward>()
                .Where(reward => !(bool)AccessTools.Field(typeof(CardReward), "_cardsWereManuallySet").GetValue(reward)!).ToArray();
            var attackChoices = ordinaryChoices
                .SelectMany(reward => reward.Cards).Where(choice => choice.Type == CardType.Attack && choice.CurrentUpgradeLevel == 0).ToArray();
            Check(attackChoices.Length > 0, "native pending choices include an attack for relic update verification");
            var egg = rewards.Rewards.OfType<RelicReward>().Single(reward => reward.Relic is MoltenEgg);
            Check(await egg.SelectUnsynchronized() && ordinaryChoices
                    .SelectMany(reward => reward.Cards).Where(choice => choice.Type == CardType.Attack).All(choice => choice.CurrentUpgradeLevel >= 1),
                "taking a restored native relic still updates pending native card choices");
            var gold = rewards.Rewards.OfType<GoldReward>().First(); int beforeGold = player.Gold;
            Check(await gold.SelectUnsynchronized() && player.Gold > beforeGold, "remaining restored reward is actually selectable");
            int afterGold = player.Gold;
            Check(!await gold.SelectUnsynchronized() && player.Gold == afterGold, "replaying restored selection cannot claim twice");
            var later = new GoldReward(5, player); rewards.Rewards.Add(later);
            Check(await later.SelectUnsynchronized() && player.Gold == afterGold + 5, "new reward identity after resume never aliases an earlier claimed reward");
        }
        // One restored peer can finish before the other installs this test
        // handler. Repeat the completion handshake until both have seen it.
        long doneDeadline = System.Environment.TickCount64 + 45000;
        do
        {
            session.Transport.SendMessage(new RaceMessage { Kind = "reward_test_done" });
            await Task.Delay(50);
            if (System.Environment.TickCount64 > doneDeadline) throw new TimeoutException("peer finished reward recovery stage");
        } while (!peerDone);
        session.Transport.SendMessage(new RaceMessage { Kind = "reward_test_done" }); await Task.Delay(300);
        GD.Print("[FDZF REWARD RECOVERY COMPLETE] " + (host ? "host" : "client") + " resume=" + resume); game.GetTree().Quit(0);
    }
}
