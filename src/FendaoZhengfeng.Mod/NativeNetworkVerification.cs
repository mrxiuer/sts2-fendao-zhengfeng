using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Entities.Cards;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.Unlocks;

namespace FendaoZhengfeng.Mod;

internal static class NativeNetworkVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); GD.Print("[FDZF NETWORK PASS] " + name); }
    private static async Task Wait(Func<bool> predicate, string name, int milliseconds = 45000)
    { long until = System.Environment.TickCount64 + milliseconds; while (!predicate()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(name); await Task.Delay(30); } }
    public static async Task Run(NGame game)
    {
        bool host = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "host";
        bool eventRecovery = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_EVENT_RECOVERY") == "1";
        bool serviceRecovery = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_SERVICE_RECOVERY") == "1";
        bool performance = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_NETWORK") == "1"
            || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_CHOICES") == "1"
            || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_REFRESH") == "1";
        bool four = performance || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_FOUR_PLAYERS") == "1" || serviceRecovery
            || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESET_NETWORK") == "1";
        ulong localPeer = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") switch { "peer3" => 3UL, "peer4" => 4UL, _ => 2UL };
        bool heartNetwork = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_HEART_NETWORK") == "1";
        bool interactive = System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_NETWORK") == "1";
        ushort port = interactive && ushort.TryParse(System.Environment.GetEnvironmentVariable("FDZF_LOCAL_PORT"), out var selectedPort) ? selectedPort : (ushort)39181;
        INetGameService? transport = null;
        try
        {
            await game.GameStartupComplete;
            var mock = new SaveManager(new MockGodotFileIo("user://fdzf-network-memory")); mock.InitProfileId(1); mock.InitSettingsDataForTest(); mock.InitPrefsDataForTest(); mock.InitProgressData(); mock.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(mock);
            InteractiveUnlockPatch.Enabled = interactive;
            var version = PeerVersionInfo.LocalDefault();
            Godot.Timer? pump = null;
            void Pump(INetGameService service)
            { pump = new Godot.Timer { WaitTime = 0.02, Autostart = true }; pump.Timeout += service.Update; game.AddChild(pump); }
            if (host)
            {
                var service = new NetHostGameService(version); transport = service;
                if (service.StartENetHost(port, 3) is { } error) throw new IOException(error.ToString());
                Pump(service); GD.Print("[FDZF NETWORK] ENet host listening");
                await Wait(() => service.ConnectedPeers.Count == (four ? 3 : 1), "client game handshake");
            }
            else
            {
                await Task.Delay(2000);
                var service = new NetClientGameService(version); transport = service; var client = new ENetClient(service); service.Initialize(client, PlatformType.None); Pump(service);
                if (await client.ConnectToHost(localPeer, "127.0.0.1", port) is { } error) throw new IOException(error.ToString());
                await Wait(() => service.IsConnected, "game connection handshake");
            }
            var listener = new Listener(); var lobby = new StartRunLobby(GameMode.Standard, transport, listener, four ? 4 : 2);
            lobby.Players.Add(new() { id = 1, character = ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            lobby.Players.Add(new() { id = 2, character = four ? ModelDb.Character<Silent>() : heartNetwork ? ModelDb.Character<Defect>() : ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            if (four)
            {
                lobby.Players.Add(new() { id = 3, character = ModelDb.Character<Defect>(), unlockState = UnlockState.all.ToSerializable() });
                lobby.Players.Add(new() { id = 4, character = ModelDb.Character<Necrobinder>(), unlockState = UnlockState.all.ToSerializable() });
            }
            bool remoteBurrowReady = false, remoteMarketReady = false, remoteTailReady = false, remoteActiveReady = false, remoteTimelineReady = false, remoteTimelineDone = false, remoteBossReady = false, remoteWon = false, remoteDone = false, remoteSaved = false, remoteInteractiveReady = false, remoteFortuneDone = false; string? remoteCampaign = null; transport.RegisterMessageHandler<RaceMessage>((msg, sender) => { if (sender == transport.NetId) return; if (msg.Kind == "burrow_ready_fixture") remoteBurrowReady = true; if (msg.Kind == "market_ready") remoteMarketReady = true; if (msg.Kind == "tail_ready") remoteTailReady = true; if (msg.Kind == "active_ready") remoteActiveReady = true; if (msg.Kind == "timeline_ready") remoteTimelineReady = true; if (msg.Kind == "timeline_done") remoteTimelineDone = true; if (msg.Kind == "boss_ready") remoteBossReady = true; if (msg.Kind == "shared_won") remoteWon = true; if (msg.Kind == "fortune_done") remoteFortuneDone = true; if (msg.Kind == "interactive_ready") remoteInteractiveReady = true; if (msg.Kind == "verification_done") remoteDone = true; if (msg.Kind == "verification_saved") remoteSaved = true; if (msg.Kind == "verification_campaign") remoteCampaign = msg.Content; });
            bool resume = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESUME") == "1"; RaceRuntime.ResumeRequested = resume;
            RaceRuntime.ResumeFromHostArchive = resume && (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RECOVERY") == "1" || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_RECOVERY") == "1" || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_NEW_EFFECTS") == "1" || four || eventRecovery || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_MENU_RESUME") == "1");
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_SHARED") == "1")
            {
                RaceConfig.ModeEnabled = host;
                RaceConfig.SynchronousMode = host; // Deliberately disagree: the host must win.
                var shared = await RaceRuntime.StartConfigured(game, lobby, ActModel.GetDefaultList(), [], "FDZFSHARED", 0, false);
                pump?.Stop();
                Check(shared.Players.Count == 2 && RaceSession.Current?.Engine.State.Synchronous == true,
                    "host selects a real native two-player shared run despite opposite client configuration");
                Check(shared.Modifiers.OfType<RaceModifier>().Select(modifier => modifier.OwnerId).ToHashSet().SetEquals(new[] { "1", "2" }),
                    "shared run has exactly one owned modifier for each native player");
                Check(RaceRuntime.LocalPlayer(shared)?.NetId == transport.NetId,
                    "each peer resolves its own native player");
                Check(shared.Players.All(player => !player.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>().Any()), "shared mode does not grant a starting Lizard Tail");
                await RunManager.Instance.EnterRoomDebug(RoomType.Elite);
                await Wait(() => shared.Players.All(player => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
                    && shared.Modifiers.OfType<RaceModifier>().All(modifier => modifier.Turn == 1), "shared first combat ready");
                Check(shared.Players.All(player => player.Creature.CombatState == shared.Players[0].Creature.CombatState),
                    "both native players enter one shared combat state");
                Check(RaceSession.Current!.Engine.State.Racers.Values.All(racer => racer.InBattle),
                    "each owned modifier commits the matching player's battle start");
                if (host)
                {
                    var bearer = RaceSession.Current!.Engine.State.Racers["1"];
                    bearer.Effects["evil_echo"] = new() { Id = "evil_echo" };
                    bearer.Effects["second_forge"] = new() { Id = "second_forge", Rank = 1 };
                    bearer.Effects["bloom_decay"] = new() { Id = "bloom_decay", Grants = [new() { EventId = "verify-wear", Source = "1", ActiveBattle = bearer.Battle }] };
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers["1"].Has("second_forge"), "effect ownership fixture replicated");
                foreach (var participant in shared.Players)
                {
                    var card = shared.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(participant);
                    Check(card.MaxUpgradeLevel == (participant.NetId == 1 ? 2 : 1), "second forge availability belongs only to its holder");
                    if (participant.NetId == 1)
                    {
                        card.UpgradeInternal(); card.UpgradeInternal();
                        Check(SecondForge.Bonus(card) == 4, "holder's second upgrade uses that holder's reinforcement rank");
                    }
                    card.RemoveFromState();
                }
                var echoEnemy = shared.Players[0].Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive);
                if (echoEnemy.GetPower<MegaCrit.Sts2.Core.Models.Powers.ArtifactPower>() is { } ward) await PowerCmd.Remove(ward);
                foreach (var participant in shared.Players)
                {
                    int before = echoEnemy.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.WeakPower>();
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.WeakPower>(new ThrowingPlayerChoiceContext(), echoEnemy, 1, participant.Creature, participant.Deck.Cards.First());
                    Check(echoEnemy.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.WeakPower>() == before + (participant.NetId == 1 ? 2 : 1),
                        "shared enemy debuff uses the actual applier's Evil Echo ownership");
                }
                var local = RaceRuntime.LocalPlayer(shared)!;
                var hand = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand.GetPile(local);
                var attack = hand.Cards.First(card => card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
                int energy = local.PlayerCombatState!.Energy;
                Check(attack.TryManualPlay(local.Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive)),
                    "local player queues a native attack action");
                await Wait(() => attack.Pile?.Type is MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard or MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust,
                    "shared attack resolves to its result pile");
                Check(local.PlayerCombatState.Energy < energy, "shared attack spends its owner's native energy");
                await Wait(() => shared.Players.All(player => player.PlayerCombatState!.Energy == 2) && !RunManager.Instance.ActionExecutor.IsRunning, "both native strikes and their checksums completed");
                transport.SendMessage(new RaceMessage { Kind = "verification_done" });
                await Wait(() => remoteDone, "shared peer startup completion");
                await Wait(() => RaceSession.Current!.Engine.State.Racers.Values.All(racer => racer.PublicPacket.Length > 0), "Timeline donor profiles available");
                var donor = shared.Players.Single(player => player.NetId == 2);
                var fiendTemplate = shared.CreateCard<MegaCrit.Sts2.Core.Models.Cards.FiendFire>(donor);
                await CardPileCmd.Add(fiendTemplate, PileType.Deck);
                await RaceSession.Current!.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                await Wait(() => TimelineRules.OpponentSnapshots(RaceSession.Current!.Engine.State, "1").Any(snapshot => snapshot.Cards.Any(card => card.Name == fiendTemplate.Title)), "Fiend Fire donor profile replicated");
                var timelineOwner = shared.Players.Single(player => player.NetId == 1);
                var timelineCard = timelineOwner.Creature.CombatState!.CreateCard<ExtraTimelineCard>(timelineOwner);
                await CardPileCmd.AddGeneratedCardToCombat(timelineCard, PileType.Hand, timelineOwner);
                var timelineSelector = new MegaCrit.Sts2.Core.TestSupport.TestCardSelector();
                timelineSelector.PrepareToSelect(new[] { donor.Deck.Cards.Count - 1 });
                using (MegaCrit.Sts2.Core.Commands.CardSelectCmd.UseSelector(timelineSelector))
                {
                    transport.SendMessage(new RaceMessage { Kind = "timeline_ready" });
                    await Wait(() => remoteTimelineReady, "both Timeline replicas ready");
                    if (host)
                    {
                        Check(timelineCard.TryManualPlay(null), "Timeline queues a real shared card action");
                        await Wait(() => NativeChoiceUi.ActiveVeil != null, "Timeline owner sees opponent picker");
                        var opponentButton = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false)
                            .OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().First();
                        HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(opponentButton, null);
                    }
                    await Wait(() => timelineCard.Pile?.Type is PileType.Discard or PileType.Exhaust, "Timeline resolves on both peers after owner-only opponent selection");
                    Check(!PileType.Play.GetPile(timelineOwner).Cards.Any() && NativeChoiceUi.ActiveVeil == null,
                        "shared Timeline leaves neither a stuck card nor a remote opponent menu");
                }
                await Wait(() => !RunManager.Instance.ActionExecutor.IsRunning, "Timeline action and checksum completed");
                Check(PileType.Hand.GetPile(timelineOwner).Cards.Count == 0
                    && PileType.Exhaust.GetPile(timelineOwner).Cards.OfType<MegaCrit.Sts2.Core.Models.Cards.FiendFire>().Count() == 1,
                    "copied Fiend Fire exhausts the real hand and resolves in the exhaust pile on both peers");
                Check(donor.Deck.Cards.Contains(fiendTemplate), "Timeline leaves the donor's actual Fiend Fire in its permanent deck");
                transport.SendMessage(new RaceMessage { Kind = "timeline_done" });
                await Wait(() => remoteTimelineDone, "both Timeline actions completed");
                for (int wave = 0; wave < 8 && CombatManager.Instance.IsInProgress; wave++)
                {
                    foreach (var enemy in local.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive).ToArray())
                        await CreatureCmd.Kill(enemy, true);
                    await CombatManager.Instance.CheckWinCondition();
                    await Task.Delay(100);
                }
                await Wait(() => !CombatManager.Instance.IsInProgress && RaceSession.Current!.Engine.State.Racers.Values.All(racer => !racer.InBattle && racer.VictoryCount == 1),
                    "shared victory commits exactly once for both players");
                Check(RaceSession.Current!.Engine.State.Racers.Values.All(racer => racer.VictoryCount == 1),
                    "both players receive exactly one shared victory");
                await Wait(() => shared.Players.All(player =>
                {
                    var state = HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.RewardsSetSynchronizer), "GetRewardStateForPlayer")
                        .Invoke(RunManager.Instance.RewardsSetSynchronizer, [player])!;
                    var stack = (System.Collections.IEnumerable)HarmonyLib.AccessTools.Field(state.GetType(), "rewardsStack").GetValue(state)!;
                    return stack.Cast<object>().Any(entry =>
                    {
                        var set = (MegaCrit.Sts2.Core.Rewards.RewardsSet)HarmonyLib.AccessTools.Field(entry.GetType(), "set").GetValue(entry)!;
                        return set.Rewards.Any(reward => reward is MegaCrit.Sts2.Core.Rewards.RelicReward)
                            && set.Rewards.Any(reward => reward is MegaCrit.Sts2.Core.Rewards.GoldReward);
                    });
                }), "native elite reward sets have finished registering for both owners");
                transport.SendMessage(new RaceMessage { Kind = "interactive_ready" });
                await Wait(() => remoteInteractiveReady, "both peers finished native victory reward registration");
                bool localChoiceOpened = false;
                int sharedChoice = await SharedChoice.Choose(shared.Players.Single(player => player.NetId == 1), 3,
                    () => { localChoiceOpened = true; return Task.FromResult(2); });
                Check(sharedChoice == 2 && localChoiceOpened == host,
                    "native reward choice opens only for its owner and replicates the same index");
                int cancelledChoice = await SharedChoice.Choose(shared.Players.Single(player => player.NetId == 2), 3,
                    () => Task.FromResult(-1));
                Check(cancelledChoice == -1, "remote reward cancellation is replicated without choosing an item");
                var relicCounts = shared.Players.ToDictionary(player => player.NetId, player => player.Relics.Count);
                int buyerHistoryBefore = RaceSession.Current!.Engine.State.Racers["2"].History.Values.Sum();
                var relicSets = shared.Players.Select(player =>
                {
                    var set = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player);
                    set.Rewards.Add(new MegaCrit.Sts2.Core.Rewards.RelicReward(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.Vajra>().ToMutable(), player));
                    return set;
                }).ToArray();
                var relicCompletions = relicSets.Select(set => RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set)).ToArray();
                var relicSelection = RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(relicSets.Single(set => set.Player == local).Rewards.Single());
                await Wait(() => NativeChoiceUi.ActiveVeil != null, "elite relic keep-or-sacrifice menu opens");
                var firstVeil = NativeChoiceUi.ActiveVeil!;
                var relicButton = firstVeil.FindChildren("*", "", true, false).OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().ElementAt(host ? 0 : 1);
                HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(relicButton, null);
                if (!host)
                {
                    await Wait(() => NativeChoiceUi.ActiveVeil != null && NativeChoiceUi.ActiveVeil != firstVeil, "elite cooperative effect candidates open");
                    var effectButton = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false).OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().First();
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(effectButton, null);
                }
                await relicSelection.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.WhenAll(relicCompletions).WaitAsync(TimeSpan.FromSeconds(20));
                Check(shared.Players.All(player => player.Relics.Count == relicCounts[player.NetId] + (player.NetId == 1 ? 1 : 0))
                    && RaceSession.Current!.Engine.State.Racers["2"].History.Values.Sum() == buyerHistoryBefore + 1,
                    "elite rewards let one player keep a relic while the other sacrifices for one cooperative effect");
                var beforeGold = shared.Players.ToDictionary(player => player.NetId, player => player.Gold);
                var goldSets = shared.Players.Select(player =>
                {
                    var set = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player);
                    set.Rewards.Add(new MegaCrit.Sts2.Core.Rewards.GoldReward(player.NetId == 1 ? 17 : 29, player));
                    return set;
                }).ToArray();
                if (!host)
                {
                    var ownerState = HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.RewardsSetSynchronizer), "GetRewardStateForPlayer")
                        .Invoke(RunManager.Instance.RewardsSetSynchronizer, [shared.Players.Single(player => player.NetId == 1)])!;
                    int nextId = (int)HarmonyLib.AccessTools.Field(ownerState.GetType(), "nextId").GetValue(ownerState)!;
                    string earlyClaim = "1:" + RaceRuntime.RoomKey + "|shared:1:" + nextId + ":0";
                    await Wait(() => RaceSession.Current!.Engine.State.ConsumedRewards.Contains(earlyClaim), "host core claim deliberately arrives before remote native gold set");
                    Check(shared.Players.Single(player => player.NetId == 1).Gold == beforeGold[1],
                        "early core claim is not mistaken for an already applied remote inventory grant");
                }
                var goldCompletions = goldSets.Select(set => RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set)).ToArray();
                var localGold = goldSets.Single(set => set.Player == local).Rewards.Single();
                await RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(localGold);
                await Task.WhenAll(goldCompletions).WaitAsync(TimeSpan.FromSeconds(20));
                Check(shared.Players.All(player => player.Gold == beforeGold[player.NetId] + (player.NetId == 1 ? 17 : 29)),
                    "native synchronized gold rewards credit each owner exactly once on both peers");
                Check(goldSets.All(set => RaceSession.Current!.Engine.State.ConsumedRewards.Contains(
                    RaceRuntime.RacerFor(set.Player)!.Id + ":" + RaceRuntime.RewardKey(set.Rewards.Single()))),
                    "shared reward claims are committed for both owners");
                if (host)
                {
                    foreach (var racer in RaceSession.Current!.Engine.State.Racers.Values) racer.BossPrizes.Add("verify:first");
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers.Values.All(racer => racer.BossPrizes.Contains("verify:first")), "Boss fixture replicated from host");
                var boonsBefore = RaceSession.Current.Engine.State.Racers.ToDictionary(pair => pair.Key, pair => pair.Value.History.Values.Sum());
                transport.SendMessage(new RaceMessage { Kind = "boss_ready" });
                await Wait(() => remoteBossReady, "both peers captured Boss fixture baseline");
                var bossSets = shared.Players.Select(player =>
                {
                    var set = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player);
                    set.Rewards.Add(new RaceActionReward(player, 2)); return set;
                }).ToArray();
                var bossCompletions = bossSets.Select(set => RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set)).ToArray();
                var bossSelection = RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(bossSets.Single(set => set.Player == local).Rewards.Single());
                await Wait(() => NativeChoiceUi.ActiveVeil != null, "local Boss reward menu opens");
                var shieldButton = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false)
                    .OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().ElementAt(0);
                HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(shieldButton, null);
                await bossSelection.WaitAsync(TimeSpan.FromSeconds(20));
                await Task.WhenAll(bossCompletions).WaitAsync(TimeSpan.FromSeconds(20));
                Check(RaceSession.Current.Engine.State.Racers.All(pair => pair.Value.History.Values.Sum() == boonsBefore[pair.Key] + 1 && !pair.Value.BossPrizes.Contains("verify:first")),
                    "actual cooperative Boss boon menus consume each owner's prize once across both peers");
                var activeOwner = shared.Players.Single(player => player.NetId == 1);
                if (host)
                {
                    RaceSession.Current.Engine.State.Racers["1"].Effects["small_light"] = new() { Id = "small_light" };
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers["1"].Has("small_light"), "active item fixture replicated");
                int activeDeckBefore = activeOwner.Deck.Cards.Count, activeHpBefore = activeOwner.Creature.MaxHp;
                int otherHpBefore = shared.Players.Single(player => player.NetId == 2).Creature.MaxHp;
                var removeSelector = new MegaCrit.Sts2.Core.TestSupport.TestCardSelector();
                int removeIndex = activeOwner.Deck.Cards.ToList().FindIndex(card => RaceModifier.ForPlayer(activeOwner)!.CardWear(card) == 8);
                removeSelector.PrepareToSelect(new[] { removeIndex });
                using (MegaCrit.Sts2.Core.Commands.CardSelectCmd.UseSelector(removeSelector))
                {
                    transport.SendMessage(new RaceMessage { Kind = "active_ready" });
                    await Wait(() => remoteActiveReady, "both native active item replicas ready");
                    if (host) SharedItemAction.Enqueue("small_light");
                    await Wait(() => activeOwner.Deck.Cards.Count == activeDeckBefore - 1 && activeOwner.Creature.MaxHp == activeHpBefore - 3
                        && !RunManager.Instance.ActionExecutor.IsRunning, "small and light native action resolves on both peers");
                }
                Check(shared.Players.Single(player => player.NetId == 2).Creature.MaxHp == otherHpBefore,
                    "native item action removes one owner card and pays three max health without changing teammate health");
                var retained = shared.Players.ToDictionary(player => player.NetId,
                    player => (player.Creature.CurrentHp, player.Gold, Deck: string.Join("|", player.Deck.Cards.Select(NativeCheckpoint.EncodeCard)), Relics: player.Relics.Count));
                if (host)
                {
                    var bearer = RaceSession.Current.Engine.State.Racers["1"];
                    bearer.Effects["r_key"] = new() { Id = "r_key" }; bearer.ResetCharges = 1;
                    await AdvancedEffects.ResetRun();
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers.Values.All(racer => racer.ResetCount == 1 && !racer.ResetPending), "both peers finish a host-initiated team reset");
                Check(shared.CurrentActIndex == 0 && shared.Players.All(player =>
                    retained[player.NetId] == (player.Creature.CurrentHp, player.Gold, string.Join("|", player.Deck.Cards.Select(NativeCheckpoint.EncodeCard)), player.Relics.Count)),
                    "shared R key preserves every player's health, gold, deck and relics");
                Check(RaceSession.Current!.Engine.State.Racers["1"].ResetCharges == 0
                    && RaceSession.Current.Engine.State.Racers["2"].ResetCharges == 0, "team reset consumes only the initiator's charge");
                var sellerPlayer = shared.Players.Single(player => player.NetId == 1);
                Check(sellerPlayer.Deck.Cards.Any(card => RaceModifier.ForPlayer(sellerPlayer)!.CardWear(card) == 7),
                    "played-card durability survives native peer deck synchronization and team reset");
                var wearProbe = shared.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(sellerPlayer);
                SharedCardWear.Write(wearProbe, 0);
                var wearReloaded = NativeCheckpoint.DecodeCard(NativeCheckpoint.EncodeCard(wearProbe), shared, sellerPlayer);
                Check(RaceModifier.ForPlayer(sellerPlayer)!.CardWear(wearReloaded) == 0, "zero durability survives card packet round trip");
                wearProbe.RemoveFromState(); wearReloaded.RemoveFromState();
                var buyerPlayer = shared.Players.Single(player => player.NetId == 2);
                var soldCard = sellerPlayer.Deck.Cards.First();
                int sellerSize = sellerPlayer.Deck.Cards.Count, buyerSize = buyerPlayer.Deck.Cards.Count;
                string soldId = RaceModifier.ForPlayer(sellerPlayer)!.CardIdentity(soldCard);
                buyerPlayer.Gold = 500;
                if (host)
                {
                    RaceSession.Current.Engine.State.Racers["2"].Effects["good_connections"] = new() { Id = "good_connections" };
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers["2"].Has("good_connections"), "market active item fixture replicated");
                await SharedChoice.Profile(sellerPlayer, "verify-market-seller");
                await SharedChoice.Profile(buyerPlayer, "verify-market-buyer");
                int sellerGoldBefore = sellerPlayer.Gold;
                var marketSelector = new MegaCrit.Sts2.Core.TestSupport.TestCardSelector(); marketSelector.PrepareToSelect(new[] { 0 });
                using (MegaCrit.Sts2.Core.Commands.CardSelectCmd.UseSelector(marketSelector))
                {
                    transport.SendMessage(new RaceMessage { Kind = "market_ready" });
                    await Wait(() => remoteMarketReady, "both market action replicas ready");
                    if (!host)
                    {
                        SharedItemAction.Enqueue("good_connections");
                        await Wait(() => NativeChoiceUi.ActiveVeil != null, "buyer chooses seller in native action");
                        var sellerVeil = NativeChoiceUi.ActiveVeil!;
                        var sellerButton = sellerVeil.FindChildren("*", "", true, false).OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().First();
                        HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(sellerButton, null);
                        await Wait(() => NativeChoiceUi.ActiveVeil != null && NativeChoiceUi.ActiveVeil != sellerVeil, "buyer confirms market price in native action");
                        var buyButton = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false).OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>().First();
                        HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton), "OnRelease").Invoke(buyButton, null);
                    }
                    await Wait(() => RaceSession.Current!.Engine.State.Trades.Count == 1 && buyerPlayer.Gold == 400 && sellerPlayer.Gold == sellerGoldBefore + 100
                        && !RunManager.Instance.ActionExecutor.IsRunning, "full market action and payment complete on both peers");
                }
                Check(RaceSession.Current!.Engine.State.Racers["2"].Purchases == 1 && sellerPlayer.Deck.Cards.Count == sellerSize && buyerPlayer.Deck.Cards.Count == buyerSize,
                    "market action pays the seller 100 gold while both decks remain unchanged until next battle");
                buyerPlayer.Creature.SetCurrentHpInternal(37);
                Check(RaceSession.Current!.Engine.State.Trades.Single().Card.Id == soldId,
                    "both peers assign the same identity to the sold permanent card: local=" + soldId + ", remote=" + RaceSession.Current.Engine.State.Trades.Single().Card.Id);
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => shared.Players.All(player => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play), "shared trade battle starts");
                Check(RaceSession.Current!.Engine.State.Racers["1"].Health == sellerPlayer.Creature.CurrentHp
                    && RaceSession.Current.Engine.State.Racers["2"].Health == 37, "room progress preserves distinct health for each owner");
                Check(!sellerPlayer.Deck.Cards.Contains(soldCard) && sellerPlayer.Deck.Cards.Count == sellerSize - 1
                    && buyerPlayer.Deck.Cards.Count == buyerSize + 1, "trade removes and delivers exactly one card on both peers");
                foreach (var modifier in shared.Modifiers.OfType<RaceModifier>()) await modifier.BeforeAdvancedCombat();
                Check(sellerPlayer.Deck.Cards.Count == sellerSize - 1 && buyerPlayer.Deck.Cards.Count == buyerSize + 1,
                    "rechecking an acknowledged trade does not duplicate its delivery");
                await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.LizardTail>(buyerPlayer);
                await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), buyerPlayer.Creature, 9999,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, buyerPlayer.Creature.CombatState!.Enemies.First(), null, null);
                Check(buyerPlayer.Creature.CurrentHp > 0 && buyerPlayer.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>().Single().WasUsed
                    && RaceSession.Current!.Engine.State.Racers["2"].RevivalCount == 1 && RaceSession.Current.Engine.State.Racers["1"].RevivalCount == 0,
                    "native client revival is counted once for its owner on both peers");
                for (int wave = 0; wave < 8 && CombatManager.Instance.IsInProgress; wave++)
                {
                    foreach (var enemy in buyerPlayer.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive).ToArray()) await CreatureCmd.Kill(enemy, true);
                    await CombatManager.Instance.CheckWinCondition(); await Task.Delay(100);
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers.Values.All(racer => !racer.InBattle), "second shared victory before tail refill");
                buyerPlayer.Gold = 500;
                if (host)
                {
                    RaceSession.Current!.Engine.State.Racers["2"].Effects["tail_refill"] = new() { Id = "tail_refill" };
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => RaceSession.Current!.Engine.State.Racers["2"].Has("tail_refill"), "tail refill fixture replicated");
                transport.SendMessage(new RaceMessage { Kind = "tail_ready" });
                await Wait(() => remoteTailReady, "both tail refill replicas ready");
                if (!host) SharedItemAction.Enqueue("tail_refill");
                await Wait(() => buyerPlayer.Gold == 150 && !buyerPlayer.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>().Single().WasUsed
                    && !RunManager.Instance.ActionExecutor.IsRunning, "client tail refill action completes on both peers");
                Check(buyerPlayer.Gold == 150, "tail refill restores the used relic and spends exactly 350 owner gold on both peers");

                await RunManager.Instance.EnterRoomDebug(RoomType.Boss, model: ModelDb.Encounter<TowerHeartEncounter>().ToMutable());
                await Wait(() => shared.Players.All(player => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play), "shared final boss first turn");
                var heart = shared.Players[0].Creature.CombatState!.Enemies.Select(enemy => enemy.Monster).OfType<TowerHeart>().Single();
                var permanentDecks = shared.Players.ToDictionary(player => player.NetId, player => string.Join("|", player.Deck.Cards.Select(NativeCheckpoint.EncodeCard)));
                var combatPiles = new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play };
                var originalPiles = shared.Players.ToDictionary(player => player.NetId,
                    player => combatPiles.ToDictionary(pile => pile, pile => pile.GetPile(player).Cards.ToArray()));
                foreach (var player in shared.Players)
                    if (player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.ArtifactPower>() is { } sharedHeartWard) await PowerCmd.Remove(sharedHeartWard);
                await heart.EchoDeck.CopyOpponent(heart);
                Check(shared.Players.All(player => player.Creature.HasPower<HeartEchoPower>()
                    && PileType.Draw.GetPile(player).Cards.Count == shared.Players.Single(other => other != player).Deck.Cards.Count
                    && PileType.Draw.GetPile(player).Cards.All(card => card.Owner == player && card.DeckVersion == null)),
                    "shared Heart creates independent temporary teammate deck copies for both players");
                foreach (var player in shared.Players) await heart.EchoDeck.Restore(player);
                Check(!heart.EchoDeck.Active && shared.Players.All(player => !player.Creature.HasPower<HeartEchoPower>()
                    && permanentDecks[player.NetId] == string.Join("|", player.Deck.Cards.Select(NativeCheckpoint.EncodeCard))
                    && combatPiles.All(pile => pile.GetPile(player).Cards.SequenceEqual(originalPiles[player.NetId][pile]))),
                    "shared Heart restores each exact original pile and never changes either permanent deck");
                if (host)
                {
                    var burrowOwner = RaceSession.Current!.Engine.State.Racers["2"];
                    burrowOwner.Effects[BurrowRules.EffectId] = new() { Id = BurrowRules.EffectId };
                    burrowOwner.History["burrow_sanctuary.charges"] = 1;
                    await RaceSession.Current.Submit(RaceSession.Current.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(shared)) });
                }
                await Wait(() => BurrowRules.Read(RaceSession.Current!.Engine.State.Racers["2"]).Charges == 1, "burrow action fixture replicated");
                var shieldTarget = PuppetFollower.Guard(buyerPlayer.Creature) ?? buyerPlayer.Creature;
                int blockBeforeBurrow = shieldTarget.Block;
                transport.SendMessage(new RaceMessage { Kind = "burrow_ready_fixture" });
                await Wait(() => remoteBurrowReady, "both burrow native action replicas ready");
                if (!host) SharedItemAction.Enqueue(BurrowRules.EffectId);
                await Wait(() => BurrowRules.Read(RaceSession.Current!.Engine.State.Racers["2"]).Phase == BurrowRules.Phase.Burrowed
                    && CombatManager.Instance.IsPlayerReadyToEndTurn(buyerPlayer) && !RunManager.Instance.ActionExecutor.IsRunning,
                    "burrow native combat action ends its owner's turn on both peers");
                Check(shieldTarget.Block == blockBeforeBurrow + buyerPlayer.Creature.MaxHp
                    && !CombatManager.Instance.IsPlayerReadyToEndTurn(sellerPlayer),
                    "burrow gives max-health block and ends only the activating teammate's turn");
                transport.SendMessage(new RaceMessage { Kind = "shared_won" });
                await Wait(() => remoteWon, "both peers completed shared victory");
                GD.Print("[FDZF NETWORK COMPLETE] Shared native combat and victory verified.");
                await Task.Delay(500); // Let ENet flush the final peer-completion message before quitting.
                game.GetTree().Quit(0); return;
            }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_VANILLA") == "1")
            {
                RaceConfig.ModeEnabled = !host;
                var normal = await RaceRuntime.StartConfigured(game, lobby, ActModel.GetDefaultList(), [], "FDZFVANILLA", 0, false);
                Check(RaceSession.Current == null && normal.Players.Count == 2 && normal.Modifiers.All(modifier => modifier is not RaceModifier), "host mode-off uses native two-player run despite client mode-on");
                transport.SendMessage(new RaceMessage { Kind = "verification_done" });
                await Wait(() => remoteDone, "vanilla peer completion");
                GD.Print("[FDZF VANILLA COMPLETE] host-off native cooperative flow passed.");
                game.GetTree().Quit(0); return;
            }
            if (interactive) { RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = true; }
            else { RaceConfig.ModeEnabled = host; RaceConfig.DebugMode = host; RaceConfig.DisabledEffects = host ? "fortune_dice" : "ritual,exhaustion"; }
            var agreed = await LobbyRules.Agree(lobby);
            bool resumeFade = resume && System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESUME_FADE") == "1";
            // The real character-select screen blacks out before starting the run.
            // A direct API load without this step cannot detect a stranded transition.
            if (resumeFade) await game.Transition.FadeOut(0.01f);
            if (!interactive) Check(agreed.Enabled && agreed.Debug && !agreed.Effects.Contains("fortune_dice") && agreed.Effects.Contains("exhaustion") && agreed.Effects.Contains("ritual"), "host config overrides different client mode and effect selections");
            var runTask = RaceRuntime.Start(game, lobby, ActModel.GetDefaultList(), [], interactive ? System.Environment.GetEnvironmentVariable("FDZF_LOCAL_SEED") ?? "FDZFLOCAL" : "FDZFNETVERIFY", 0, agreed);
            await Wait(() => RaceSession.Current != null, "session capture"); pump?.QueueFree();
            var run = await runTask; var session = RaceSession.Current!;
            if (resumeFade)
            {
                Check(!game.Transition.InTransition && game.Transition.MouseFilter == Control.MouseFilterEnum.Ignore,
                    "resuming after character-select blackout reveals the run and releases input on each peer");
            }
            if (interactive) Check(run.ExtraFields.StartedWithNeow && run.CurrentRoom is EventRoom opening
                && opening.LocalMutableEvent is MegaCrit.Sts2.Core.Models.Events.Neow && opening.LocalMutableEvent.CurrentOptions.Count == 3,
                "interactive peers retain the native three-choice opening alongside Lizard Tail");
            Check(session.Engine.State.Racers.Count == (four ? 4 : 2), "all real ENet peers share one match");
            Check(run.Players.Count == 1 && run.Players[0].NetId == 1, "gameplay remains isolated singleplayer on each process");
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            string otherId = host ? "2" : "1";
            await Wait(() => session.Engine.State.Racers[otherId].PublicPacket.Length > 0 && PublicProfiles.Read(session.Engine.State.Racers[otherId])?.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail) == true, "peer public profile including late starting relic");
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_VICTORY_NETWORK") == "1")
            { await NativeVictoryNetworkVerification.Run(game, run, session, host); return; }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_MENU_RESUME") == "1")
            { await NativeMenuResumeVerification.Run(game, run, session, host, resume); return; }
            if (performance) { await NativeRewardNetworkPerformance.Run(game, run, session); return; }
            if (eventRecovery)
            { await NativeEventRecoveryVerification.Run(game, run, session, host, resume); return; }
            if (serviceRecovery)
            { await NativeServiceRecoveryVerification.Run(game, run, session, host, resume); return; }
            if (four || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_NEW_EFFECTS") == "1")
            { await NativeNewEffectsNetworkVerification.Run(game, run, session, host, resume); return; }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RECOVERY") == "1")
            { await NativeRecoveryVerification.Run(game, run, session, host, resume); return; }
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_RECOVERY") == "1")
            { await NativeRewardRecoveryVerification.Run(game, run, session, host, resume); return; }
            Check(run.Acts.Count == 5, "race run contains five acts on both real ENet peers");
            if (interactive)
            {
                DisplayServer.WindowSetTitle("分道争锋 · 本地双进程 · " + (host ? "房主 / 1号" : "客户端 / 2号"));
                transport.SendMessage(new RaceMessage { Kind = "interactive_ready" });
                await Wait(() => remoteInteractiveReady, "interactive peer ready");
                GD.Print("[FDZF INTERACTIVE READY] " + (host ? "host" : "client") + " seed=" + run.Rng.Seed);
                if (System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_SMOKE") == "1")
                {
                    await Task.Delay(1500);
                    string? screenshots = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
                    if (!string.IsNullOrWhiteSpace(screenshots))
                    {
                        Directory.CreateDirectory(screenshots);
                        game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(screenshots, host ? "interactive-host.png" : "interactive-client.png"));
                    }
                    GD.Print("[FDZF INTERACTIVE SMOKE COMPLETE] " + (host ? "host" : "client"));
                    game.GetTree().Quit(0);
                }
                return;
            }
            if (heartNetwork) { await NativeHeartNetworkVerification.Run(game, run, session, host); return; }
            var campaignPreview = RunState.FromSerializable(RunManager.Instance.ToSave(null));
            var signatures = new List<string>();
            for (int index = 3; index < 5; index++)
            {
                campaignPreview.CurrentActIndex = index;
                var map = FiveActCampaign.CreateLateMap(campaignPreview);
                signatures.Add(campaignPreview.Act.BossEncounter.Id + "/" + campaignPreview.Act.SecondBossEncounter!.Id + ":" + NativeCampaignVerification.MapSignature(map));
            }
            string campaignDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", signatures))));
            transport.SendMessage(new RaceMessage { Kind = "verification_campaign", Content = campaignDigest });
            await Wait(() => remoteCampaign != null, "remote fourth/fifth act map digest");
            Check(remoteCampaign == campaignDigest, "fourth and fifth maps including links node types and both bosses match across actual ENet processes");
            var peerView = PublicProfiles.Read(session.Engine.State.Racers[otherId]);
            Check(peerView != null && peerView.Deck.Cards.Count > 0 && peerView.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail), "peer native deck and relic snapshot crosses real ENet transport");

            if (resume)
            {
                Check(session.Engine.State.Trades.Count == 1 && session.Engine.State.Trades.Single().Delivered && session.Engine.State.Trades.Single().Removed,
                    "completed card trade survives both processes restarting without redelivery");
                await Wait(() => run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "resumed combat ready");
                Check(session.Restored != null, "loaded matching shared checkpoint");
                Check(run.CurrentMapCoord == session.Restored!.Value.Save.VisitedMapCoords.Last(), "resumed at committed next map node");
                Check(session.Me.Battle == session.Restored!.Value.Match.Racers[session.LocalId].Battle + 1, "resumption advances one new battle only");
                if (!host) Check(session.Me.BurdenSnapshot > 0, "remote curse survives process restart");
                Check(session.Engine.State.Racers["1"].Effects["evil_echo"].Rank == 1, "reinforced evil echo survives real ENet checkpoint restart");
                Check(session.Engine.State.Racers["1"].Effects.ContainsKey("flight_wings"), "flight holding survives real ENet checkpoint restart");
                Check(session.Engine.State.Racers["1"].Effects["gamblers_heart"].Rank == 1, "critical reinforcement survives real ENet checkpoint restart");
                if (host) await Wait(() => remoteDone, "resumed client completion");
                else { transport.SendMessage(new RaceMessage { Kind = "verification_done" }); await Task.Delay(500); }
                GD.Print("[FDZF NETWORK RESUME COMPLETE] " + (host ? "host" : "client") + " restored and entered live combat."); game.GetTree().Quit(0); return;
            }
            if (host)
            {
                foreach (var racer in session.Engine.State.Racers.Values)
                    racer.Effects["shared_fortune"] = new() { Id = "shared_fortune" };
                await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            }
            await Wait(() => session.Me.Has("shared_fortune"), "shared fortune fixture synchronized");
            int fortuneBaseline = run.Players.Single().Gold;
            await PlayerCmd.GainGold(host ? 60 : 20, run.Players.Single());
            await Wait(() => session.Me.FortuneIncome.GetValueOrDefault(otherId) == (host ? 20 : 60), "native opponent gold recorded across ENet");
            Check(run.Players.Single().Gold == fortuneBaseline + (host ? 60 : 20), "shared fortune waits for owner's next node before paying");
            await RunManager.Instance.EnterRoomDebug(RoomType.Shop);
            await Wait(() => run.Players.Single().Gold == fortuneBaseline + (host ? 70 : 50), "shared fortune native node settlement payout");
            Check(run.Players.Single().Gold == fortuneBaseline + (host ? 70 : 50), "shared fortune pays actual native gold on both ENet peers");
            Check(session.Me.FortuneIncome.Count == 0, "shared fortune node settlement clears income window");
            transport.SendMessage(new RaceMessage { Kind = "fortune_done" });
            await Wait(() => remoteFortuneDone, "both fortune payouts finished");
            if (host)
            {
                foreach (var racer in session.Engine.State.Racers.Values) racer.Effects.Remove("shared_fortune");
                await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            }
            await Wait(() => !session.Me.Has("shared_fortune"), "shared fortune fixture cleanup");
            int beforeMarketDeck = run.Players.Single().Deck.Cards.Count;
            int beforeMarketGold = fortuneBaseline + (host ? 70 : 50);
            string tradedCardId;
            if (host)
            {
                session.Me.Effects["good_connections"] = new() { Id = "good_connections" };
                await PlayerCmd.GainGold(500, run.Players.Single());
                tradedCardId = session.Engine.State.Racers["2"].MarketCards.First().Id;
                await AdvancedEffects.BuyCard("2", tradedCardId);
                Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck, "real ENet purchase leaves current buyer deck unchanged until next combat");
            }
            else
            {
                await Wait(() => session.Engine.State.Trades.Count == 1, "host market purchase received");
                tradedCardId = session.Engine.State.Trades.Single().Card.Id;
                Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck, "real ENet sale keeps original card until seller next combat");
            }
            await RunManager.Instance.EnterRoomDebug(host ? RoomType.Elite : RoomType.Monster);
            await Wait(() => RaceModifier.Active?.Turn == 1 && run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "first combat ready");
            if (host)
            {
                session.Me.Effects["evil_echo"] = new() { Id = "evil_echo", Rank = 1 };
                session.Me.Effects["flight_wings"] = new() { Id = "flight_wings" };
                session.Me.Effects["gamblers_heart"] = new() { Id = "gamblers_heart", Rank = 1 };
                await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            }
            await Wait(() => session.Engine.State.Racers["1"].Effects.GetValueOrDefault("evil_echo")?.Rank == 1, "evil echo holding crosses actual ENet transport");
            var echoTarget = run.Players[0].Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive);
            if (echoTarget.GetPower<MegaCrit.Sts2.Core.Models.Powers.ArtifactPower>() is { } copyWard) await PowerCmd.Remove(copyWard);
            int echoWeak = echoTarget.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.WeakPower>();
            await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.WeakPower>(new ThrowingPlayerChoiceContext(), echoTarget, 1, run.Players[0].Creature, run.Players[0].Deck.Cards.First());
            Check(echoTarget.GetPowerAmount<MegaCrit.Sts2.Core.Models.Powers.WeakPower>() == echoWeak + (host ? 3 : 1),
                "real ENet evil echo applies only to its holder's independent battle");
            Check(session.Engine.State.Racers["1"].Effects.ContainsKey("flight_wings"), "flight holding crosses actual ENet transport");
            var flightHero = run.Players.Single().Creature;
            flightHero.LoseBlockInternal(flightHero.Block); int beforeFlightHit = flightHero.CurrentHp;
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), flightHero, 20,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Move | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, echoTarget, null, null);
            Check(flightHero.CurrentHp == beforeFlightHit - (host ? 11 : 20),
                "real ENet flight reduces attacks only in its holder's independent battle");
            Check(session.Engine.State.Racers["1"].Effects.GetValueOrDefault("gamblers_heart")?.Rank == 1, "critical reinforcement crosses actual ENet transport");
            await CreatureCmd.SetMaxAndCurrentHp(echoTarget, Math.Max(echoTarget.MaxHp, 10000));
            echoTarget.LoseBlockInternal(echoTarget.Block); int beforeGamblerDamage = echoTarget.CurrentHp;
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), echoTarget, 10,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, run.Players.Single().Creature, null, null);
            Check(echoTarget.CurrentHp == beforeGamblerDamage - (host ? 11 : 10), "real ENet gambler damage bonus applies only to its holder's independent battle");
            var criticalModifier = RaceModifier.Active!; int beforeCriticalRolls = criticalModifier.CriticalRolls;
            var criticalProbe = await new MegaCrit.Sts2.Core.Commands.Builders.AttackCommand(100)
                .FromCard(run.Players.Single().Deck.Cards.First(card => card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack), null)
                .Targeting(echoTarget).Unpowered().WithNoAttackerAnim().Execute(new ThrowingPlayerChoiceContext());
            Check(criticalModifier.CriticalRolls == beforeCriticalRolls + (host ? 1 : 0)
                && criticalProbe.Results.Single().Single().UnblockedDamage == (host ? criticalModifier.LastAttackCritical ? 165 : 110 : 100),
                "real ENet actual attack rolls criticals only for its holder and applies the matching damage");
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), run.Players.Single().Creature, 9999,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, run.Players.Single().Creature.CombatState!.Enemies.First(), null, null);
            Check(session.Me.RevivalCount == 1 && run.Players.Single().Creature.CurrentHp > 0,
                "real native revival records the correct local racer over ENet before owning its boon");
            await Wait(() => session.Engine.State.Racers[otherId].RevivalCount == 1, "remote revival history synchronization");
            Check(session.Engine.State.Racers[otherId].RevivalCount == 1, "peer revival history arrives exactly once over actual ENet");
            await Wait(() => session.Engine.State.Trades.Single().Delivered && session.Engine.State.Trades.Single().Removed, "independent ENet card handoffs");
            Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck + (host ? 1 : -1), "real ENet handoff changes each native deck exactly once");
            GD.Print($"[FDZF NETWORK GOLD] initial={beforeMarketGold} actual={run.Players.Single().Gold} host={host}");
            Check(run.Players.Single().Gold == beforeMarketGold + (host ? 400 : 100), "real ENet buyer pays and seller receives native gold exactly once");
            if (!host) Check(!RaceModifier.Active!.CaptureMarket().Any(card => card.Id == tradedCardId), "real ENet seller stable card identity is removed");
            if (host)
            {
                await Wait(() => session.Engine.State.Racers["2"].InBattle, "opponent in independent combat");
                session.Me.WonBattles.Add("fixture");
                await session.Submit(session.New("offer", "fixture|0", route: Route.Disrupt));
                var offer = session.Engine.State.Offers["1:fixture|0:Disrupt"];
                await session.Submit(session.New("choose", offer.Id, offer.Candidates[0]));
                Check(session.Engine.State.Racers["2"].Effects.Count == 1, "group sacrifice dispatched to remote player");
                await Wait(() => session.Engine.State.Racers["2"].Battle == 2, "opponent entered next combat");
                Check(session.Me.Battle == 1 && CombatManager.Instance.IsInProgress, "opponent progresses without advancing local combat");
                Check(session.Engine.State.Racers["2"].BurdenSnapshot > 0, "remote curse active only in next combat");
                session.RequestPause();
            }
            else
            {
                await Wait(() => session.Me.Effects.Count == 1, "incoming group attack");
                Check(session.Me.Burden == 0 && session.Me.BurdenSnapshot == 0, "incoming curse does not change ongoing fight");
                await WinCurrent();
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => session.Me.Battle == 2 && run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "second personal combat");
                Check(session.Me.BurdenSnapshot > 0, "queued curse activates on next battle boundary");
                await Wait(() => session.PauseRequested, "host shared-save request");
            }
            var captureTimer = System.Diagnostics.Stopwatch.StartNew();
            RaceRuntime.Checkpoint(); captureTimer.Stop(); session.FlushPersistence();
            var queuedCheckpoint = StateJson.Deserialize<NativeCheckpoint.Bundle>(File.ReadAllText(Path.Combine(session.SaveDirectory, "checkpoint.json")));
            Check(queuedCheckpoint.Revision == session.Engine.State.Revision && queuedCheckpoint.ReplayBattleKey == session.Me.BattleKey,
                "background checkpoint flush preserves latest revision and current combat on real disk");
            var queuedNative = NativeCheckpoint.Restore(queuedCheckpoint);
            Check(queuedNative.Save.Players.Count == 1 && queuedCheckpoint.AppliedReceipts != null,
                "background checkpoint keeps readable native run and receipt journal");
            GD.Print($"[FDZF PERFORMANCE] checkpoint capture and queue {captureTimer.Elapsed.TotalMilliseconds:F2} ms; disk flush measured separately");
            await WinCurrent();
            var next = run.Map.StartingMapPoint.Children.OrderBy(p => p.coord.ToString(), StringComparer.Ordinal).First();
            var barrier = RunManager.Instance.EnterMapCoord(next.coord);
            await Wait(() => session.Paused, "shared checkpoint commit");
            Check(session.WaitingAtCheckpoint, "personal progress frozen at common revision");
            var report = System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!;
            var liveModifier = RaceModifier.Active;
            var saved = NativeCheckpoint.Read(Path.Combine(report, "resume", session.LocalId + ".json"));
            Check(ReferenceEquals(RaceModifier.Active, liveModifier), "reading an isolated checkpoint preview never replaces the active gameplay modifier");
            Check(saved.Match.Revision == session.Engine.State.Revision, "checkpoint contains committed shared revision");
            Check(saved.Run.Players.Count == 1 && saved.Run.Players[0].Deck.Cards.Count == run.Players[0].Deck.Cards.Count, "native personal deck restored from packet");
            if (host) { await Wait(() => remoteSaved, "client persisted checkpoint receipt"); session.ContinueRace(); await barrier; await Wait(() => remoteDone, "client completion receipt"); }
            else { transport.SendMessage(new RaceMessage { Kind = "verification_saved" }); await barrier; transport.SendMessage(new RaceMessage { Kind = "verification_done" }); await Task.Delay(500); }
            GD.Print("[FDZF NETWORK COMPLETE] " + (host ? "host" : "client") + " cross-process checks passed."); game.GetTree().Quit(0);
        }
        catch (Exception e) { GD.PushError("[FDZF NETWORK FAILED] " + (host ? "host" : "client") + " " + e); game.GetTree().Quit(1); }
    }
    private static async Task WinCurrent()
    {
        var session = RaceSession.Current!;
        RaceUi.Refresh();
        RaceUi.TogglePanel();
        Check(RaceUi.DebugWinButtonVisible, "network peer exposes quick victory in debug panel");
        RaceUi.TogglePanel();
        int victories = session.Me.VictoryCount;
        await DebugTesting.WinBattle();
        await Wait(() => !session.Me.InBattle, "combat victory settlement");
        Check(session.Me.VictoryCount == victories + 1, "debug quick victory settles exactly once through native hooks");
    }
    private sealed class Listener : IStartRunLobbyListener
    {
        public void PlayerConnected(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player) { }
        public void PlayerChanged(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
        public void RemotePlayerDisconnected(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player) { }
        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers) { }
        public void LocalPlayerDisconnected(NetErrorInfo info) { }
    }
}

// The opt-in manual test starts from an in-memory profile with full unlocks.
// Never persist changes to the user's progress or alter ordinary game sessions.
[HarmonyLib.HarmonyPatch(typeof(SaveManager), nameof(SaveManager.GenerateUnlockStateFromProgress))]
internal static class InteractiveUnlockPatch
{
    internal static bool Enabled;
    static void Postfix(ref UnlockState __result)
    {
        if (Enabled && System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1"
            && System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_NETWORK") == "1") __result = UnlockState.all;
    }
}
