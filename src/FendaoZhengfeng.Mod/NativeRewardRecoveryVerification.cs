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
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRewardRecoveryVerification
{
    private sealed record Expected(SavedCombatRewards Rewards, int Gold, int Victories, int DeckSize, string PendingClaimKey = "");
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); GD.Print("[FDZF REWARD RECOVERY PASS] " + name); }
    private static async Task Wait(Func<bool> condition, string reason)
    { long deadline = System.Environment.TickCount64 + 45000; while (!condition()) { if (System.Environment.TickCount64 > deadline) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        bool pendingClaim=System.Environment.GetEnvironmentVariable("FDZF_VERIFY_PENDING_CLAIM")=="1";
        bool confirmedClaim=System.Environment.GetEnvironmentVariable("FDZF_VERIFY_CONFIRMED_CLAIM")=="1";
        bool saveFailure=System.Environment.GetEnvironmentVariable("FDZF_VERIFY_SAVE_FAILURE")=="1";
        bool failureReady=false,failureReleased=false,missingProofRejected=false;
        bool grantInProgress=System.Environment.GetEnvironmentVariable("FDZF_VERIFY_GRANT_IN_PROGRESS")=="1";
        bool grantReady=false,grantSaveStarted=false,chooserReady=false,chooserSaved=false,chooserClicked=false,modeReady=false,modeSaved=false;Task? midGrantSave=null;
        bool peerReady = false, peerDone = false, snapshotAcknowledged=false;
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        { if (sender == session.Transport.NetId) return; if (message.Kind == "reward_test_ready") { peerReady = true; if(host) session.Transport.SendMessage(new RaceMessage {Kind="reward_snapshot_ack"},sender); }
          if(message.Kind=="reject" && message.Content.StartsWith("missing-native-proof-2\n",StringComparison.Ordinal)) missingProofRejected=true;
          if(message.Kind=="mode_ready") modeReady=true;
          if(message.Kind=="mode_saved") modeSaved=true;
          if(message.Kind=="chooser_ready") chooserReady=true;
          if(message.Kind=="chooser_saved") chooserSaved=true;
          if(message.Kind=="chooser_clicked") chooserClicked=true;
          if(message.Kind=="grant_ready") grantReady=true;
          if(message.Kind=="grant_save_started") grantSaveStarted=true;
          if(message.Kind=="save_failure_ready") failureReady=true;
          if(message.Kind=="save_failure_release") failureReleased=true;
          if(message.Kind=="reward_snapshot_ack") snapshotAcknowledged=true; if (message.Kind == "reward_test_done") peerDone = true; });
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
            if(grantInProgress) {
                var modeSelection=rewards.Rewards.OfType<RaceActionReward>().Single().SelectUnsynchronized();
                await Wait(()=>NativeChoiceUi.ActiveVeil!=null,"actual Boss mode picker opens before saving");
                Check(!NativeRewardGrant.IsApplying(session),"reading mode reward choices is a safe non-mutating save point");
                session.Transport.SendMessage(new RaceMessage {Kind="mode_ready"});await Wait(()=>modeReady,"both mode pickers are open");
                if(host) {await session.Recovery.SaveHost();session.Transport.SendMessage(new RaceMessage {Kind="mode_saved"});}
                else await Wait(()=>modeSaved,"host saved with both mode pickers open");
                var back=NativeChoiceUi.ActiveVeil!.FindChildren("*","",true,false).OfType<NRewardButton>().Last();
                AccessTools.Method(typeof(NRewardButton),"OnRelease").Invoke(back,null);
                Check(!await modeSelection,"saving an open mode picker preserves its unclaimed reward and return action");
                await Wait(()=>!NativeChoiceUi.IsOpen,"cancelled native mode picker is closed");
                int deckBefore=player.Deck.Cards.Count;
                var choosing=regular.SelectUnsynchronized();NCardRewardSelectionScreen? screen=null;
                await Wait(()=>(screen=game.GetTree().Root.FindChildren("*","",true,false).OfType<NCardRewardSelectionScreen>()
                    .FirstOrDefault(view=>view.IsVisibleInTree()))!=null
                    && AccessTools.Field(typeof(NCardRewardSelectionScreen),"_completionSource").GetValue(screen)!=null,
                    "actual native card chooser opens before saving");
                await Wait(()=>(bool)AccessTools.Field(typeof(NCardHolder),"_isClickable").GetValue(screen!.GetCardHolder(regular.Cards.First()))!,"original card click guard expires");
                Check(!NativeRewardGrant.IsApplying(session),"reading native card choices is a safe non-mutating save point");
                session.Transport.SendMessage(new RaceMessage {Kind="chooser_ready"});await Wait(()=>chooserReady,"both players read card choices");
                if(host) {
                    await session.Recovery.SaveHost(true);
                    session.Transport.SendMessage(new RaceMessage {Kind="chooser_saved"});
                } else await Wait(()=>chooserSaved && session.Recovery.Frozen,"host saved while the original chooser stays open");
                screen!.GetCardHolder(regular.Cards.First())._GuiInput(new InputEventAction {Action=MegaInput.select,Pressed=true});
                await Task.Delay(50);
                Check(!choosing.IsCompleted && player.Deck.Cards.Count==deckBefore && !NativeRewardGrant.IsApplying(session),
                    "click during save hold cannot grant a card or corrupt its saved choices");
                session.Transport.SendMessage(new RaceMessage {Kind="chooser_clicked"});await Wait(()=>chooserClicked,"both held card clicks are pending");
                if(host) {session.Recovery.Release();session.Transport.SendMessage(new RaceMessage {Kind="recovery_release"});}
                Check(await choosing && player.Deck.Cards.Count==deckBefore+1,"held native card click grants exactly once after release");
                // This variant consumes the ordinary fixture used by the later
                // MoltenEgg check; generate another real native attack choice.
                CardReward spare=null!;
                for(int attempt=0;attempt<64;attempt++) {
                    spare=new CardReward(CardCreationOptions.ForRoom(player,RoomType.Monster).WithFlags(CardCreationFlags.IsFromCombat),3,player);
                    spare.Populate();
                    if(spare.Cards.Any(card=>card.Type==CardType.Attack && card.CurrentUpgradeLevel==0)) break;
                }
                Check(spare.Cards.Any(card=>card.Type==CardType.Attack && card.CurrentUpgradeLevel==0),"native factory supplies an unclaimed attack for the later relic recovery check");
                rewards.Rewards.Add(spare);
            }
            if(confirmedClaim) {
                var outer=new GoldReward(13,player);var inner=new GoldReward(7,player);
                rewards.Rewards.Add(outer);rewards.Rewards.Add(inner);int prior=player.Gold;
                NativeRewardGrant.AfterGrantForVerification=async(owner,reward)=> {
                    if(!ReferenceEquals(owner,session) || !ReferenceEquals(reward,outer)) return;
                    Check(await inner.SelectUnsynchronized() && inner.SuccessfullySelected,
                        "a real native reward hook may complete a nested native reward without deadlock");
                    Check(NativeClaimJournal.Capture(session).Any(claim=>claim.Key==RaceRuntime.RewardKey(inner))
                        && !session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+RaceRuntime.RewardKey(inner)),
                        "nested reward keeps its completion pending until the enclosing native grant is snapshot safe");
                };
                try {Check(await outer.SelectUnsynchronized() && player.Gold==prior+20,"both native rewards grant their actual gold once");}
                finally {NativeRewardGrant.AfterGrantForVerification=null;}
                await Wait(()=>session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+RaceRuntime.RewardKey(inner))
                    && NativeClaimJournal.Capture(session).Length==0,"nested native completion is safely submitted after the enclosing grant");
            }
            int beforeGold = player.Gold;
            bool capturedWindow=false;
            if(confirmedClaim && !host) {
                var missingProof=session.New("claim","missing-native-proof") with {Id="missing-native-proof-2"};
                session.Transport.SendMessage(new RaceMessage {Kind="native_claim",Content=StateJson.Serialize(missingProof)});
                await Wait(()=>missingProofRejected,"host rejects a native claim without its completed inventory backup");
                Check(!session.Engine.State.ProcessedCommands.Contains(missingProof.Id)
                    && !session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+missingProof.Key),
                    "missing backup cannot consume a reward or acknowledge a native grant");
                session.Recovery.SendSnapshot();session.Recovery.SuspendPeriodicCaptureForVerification=true;
                session.Transport.SendMessage(new RaceMessage {Kind="reward_test_ready"});
                await Wait(()=>snapshotAcknowledged,"host has the older pre-grant native backup");
                NativeClaimJournal.AfterSubmitForVerification=(owner,claim)=> {
                    if(!ReferenceEquals(owner,session) || claim.Key!=RaceRuntime.RewardKey(taken)) return Task.CompletedTask;
                    Check(player.Gold==beforeGold+777 && taken.SuccessfullySelected
                        && session.Engine.State.ProcessedCommands.Contains(claim.Id),"actual native gold grant and authoritative claim are confirmed before the post-claim checkpoint");
                    session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit);
                    throw new IOException("forced disconnect after authoritative claim before post-claim checkpoint");
                };
            }
            if(grantInProgress) NativeRewardGrant.AfterGrantForVerification=async(owner,reward)=> {
                if(!ReferenceEquals(owner,session) || !ReferenceEquals(reward,taken)) return;
                Check(player.Gold==beforeGold+777 && !taken.SuccessfullySelected && NativeRewardGrant.IsApplying(session),
                    "native hook is stopped after real wallet mutation before SuccessfullySelected");
                Check(session.Recovery.SendSnapshot()==null,"periodic snapshots skip an unfinished native grant");
                session.Transport.SendMessage(new RaceMessage {Kind="grant_ready"});
                await Wait(()=>grantReady,"both native grants reach the unfinished hook");
                if(host) {
                    midGrantSave=session.Recovery.SaveHost();
                    Check(session.Recovery.IsDraining && !session.Recovery.Frozen && !midGrantSave.IsCompleted,
                        "host save waits for native grants without freezing their existing hooks");
                    session.Transport.SendMessage(new RaceMessage {Kind="grant_save_started"});
                } else await Wait(()=>grantSaveStarted,"host starts saving during the native grant");
            };
            NativeClaimJournal.AfterRecordForVerification=(owner,claim)=>
            {
                if(!ReferenceEquals(owner,session) || claim.Key!=RaceRuntime.RewardKey(taken)) return;
                Check(taken.SuccessfullySelected && player.Gold==beforeGold+777
                    && !session.Engine.State.ProcessedCommands.Contains(claim.Id),"forced stop is after native grant and before authoritative claim");
                var snapshot=StateJson.Deserialize<RecoverySnapshot>(StateJson.Serialize(RaceRecovery.Capture(session)));
                Check(snapshot.CompletedClaims?.Single().Id==claim.Id
                    && snapshot.Rewards!.Rewards.All(r=>r.Key!=claim.Key),"real serialized snapshot retains completion proof and excludes the already granted reward");
                var repaired=RewardClaimRecovery.Merge(session.Engine.State,session.LocalId,snapshot.CompletedClaims!);
                var bundle=new NativeCheckpoint.Bundle("0.5.1",repaired.Version,repaired.Revision,StateJson.Serialize(repaired),
                    snapshot.NativePacket,snapshot.AppliedReceipts,snapshot.BattleKey,snapshot.Rewards);
                var rebuilt=NativeCheckpoint.Restore(bundle);
                Check(rebuilt.Run.Players.Single().Gold==beforeGold+777 && rebuilt.Match.ConsumedRewards.Contains(session.LocalId+":"+claim.Key),
                    "native packet reconstruction preserves one gold grant and repaired consumption");
                Check(StateJson.Serialize(RewardClaimRecovery.Merge(repaired,session.LocalId,snapshot.CompletedClaims!))==StateJson.Serialize(repaired),
                    "replaying the same captured native completion cannot duplicate accounting");
                capturedWindow=true;
            };
            if(pendingClaim && !host) NativeClaimJournal.BeforeSubmitForVerification=async(owner,claim)=>
            {
                if(!ReferenceEquals(owner,session) || claim.Key!=RaceRuntime.RewardKey(taken)) return;
                var gate=(SemaphoreSlim)AccessTools.Field(typeof(RaceSession),"commandGate").GetValue(session)!;
                await gate.WaitAsync();
                try {
                    session.Recovery.SendSnapshot();session.Recovery.SuspendPeriodicCaptureForVerification=true;
                    session.Transport.SendMessage(new RaceMessage {Kind="reward_test_ready"});
                    await Wait(()=>snapshotAcknowledged,"host acknowledged the pending native claim snapshot");
                    Check(!session.Engine.State.ProcessedCommands.Contains(claim.Id),"claim remains unsubmitted at forced real disconnect");
                    session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit);
                } finally {gate.Release();}
            };
            if(saveFailure) NativeClaimJournal.BeforeSubmitForVerification=async(owner,claim)=>
            {
                if(!ReferenceEquals(owner,session) || claim.Key!=RaceRuntime.RewardKey(taken)) return;
                var gate=(SemaphoreSlim)AccessTools.Field(typeof(RaceSession),"commandGate").GetValue(session)!;
                await gate.WaitAsync();
                try {
                    session.Recovery.SendSnapshot();
                    session.Transport.SendMessage(new RaceMessage {Kind="save_failure_ready"});
                    await Wait(()=>failureReady,"both native grants wait before claim submission");
                    if(host) {
                        await Wait(()=>session.Recovery.BackupCount==2,"both completed native grant backups reach host");
                        await session.Recovery.SaveHost();
                        string path=RaceRecovery.ArchivePath(session.LocalId);byte[] previous=File.ReadAllBytes(path);
                        bool failed=false;
                        using(var locked=new FileStream(path,FileMode.Open,System.IO.FileAccess.Read,FileShare.None)) {
                            try {await session.Recovery.SaveHost();} catch(Exception error) when(error is IOException or UnauthorizedAccessException){failed=true;}
                        }
                        Check(failed && File.ReadAllBytes(path).SequenceEqual(previous) && !File.Exists(path+".tmp"),
                            "real locked archive write failure preserves the prior save and cleans temporary data");
                        Check(!session.Recovery.Frozen && !RunManager.Instance.IsPaused
                            && NativeClaimJournal.Capture(session).Length==1,"save failure unfreezes play and retains pending native completion");
                        session.Transport.SendMessage(new RaceMessage {Kind="save_failure_release"});
                    } else await Wait(()=>failureReleased && !session.Recovery.Frozen,"host releases the failed save without disconnecting players");
                    throw new IOException("forced original claim interruption after failed save");
                } finally {gate.Release();}
            };
            try {
                Check(await taken.SelectUnsynchronized() && taken.SuccessfullySelected && player.Gold == beforeGold + 777,
                    "actual gold reward is taken through normal native selection before save");
            } catch(RuleException) when(grantInProgress && session.Recovery.NativeGrantBlocked) {
                Check(capturedWindow && NativeClaimJournal.Capture(session).Length==1,"save freeze retains completed native claim for retry");
            } catch(IOException) when(saveFailure) {
                Check(capturedWindow,"interrupted original submit follows an actual completed native reward");
                await Wait(()=>session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+RaceRuntime.RewardKey(taken))
                    && NativeClaimJournal.Capture(session).Length==0,"idle tick retries the original claim after failed save");
                Check(session.Transport.IsConnected && player.Gold==beforeGold+777,"online retry retains one gold grant and a live connection");
            } catch(IOException) when(confirmedClaim && !host && !session.Transport.IsConnected) {
                Check(capturedWindow && NativeClaimJournal.Capture(session).Length==0,"confirmed claim is not treated as an unconfirmed retry after disconnect");
            } catch(IOException) when(pendingClaim && !host && !session.Transport.IsConnected) {
                Check(capturedWindow && NativeClaimJournal.Capture(session).Length==1,"disconnect preserves the unconfirmed completed native claim");
            } finally { NativeClaimJournal.AfterRecordForVerification=null;NativeClaimJournal.BeforeSubmitForVerification=null;NativeClaimJournal.AfterSubmitForVerification=null;NativeRewardGrant.AfterGrantForVerification=null; }
            if(grantInProgress) {
                if(host) {
                    await midGrantSave!;
                    var archive=RaceRecovery.Read(session.LocalId);
                    Check(archive.Players.Values.All(snapshot=>snapshot.Rewards!.Rewards.All(reward=>reward.Key!=RaceRuntime.RewardKey(taken))),
                        "mid-grant save includes completed inventory and no reclaimable native gold reward");
                    Check(StateJson.Deserialize<Match>(archive.MatchJson).Racers.Keys.All(id=>StateJson.Deserialize<Match>(archive.MatchJson).ConsumedRewards.Contains(id+":"+RaceRuntime.RewardKey(taken))),
                        "mid-grant archive records both authoritative reward consumptions");
                }
                await Wait(()=>!session.Recovery.NativeGrantBlocked && NativeClaimJournal.Capture(session).Length==0,
                    "completed native grant journal drains after saving releases both players");
                Check(player.Gold==beforeGold+777,"mid-grant save leaves exactly one gold grant");
            }
            if(!pendingClaim || host) Check(capturedWindow && NativeClaimJournal.Capture(session).Length==0,"normal acknowledgement clears the completed native claim journal");
            var captured = RewardRecovery.Capture((CombatRoom)run.CurrentRoom!)!;
            Check(captured.Rewards.All(entry => entry.Key != RaceRuntime.RewardKey(taken)), "selected gold reward is absent from pending snapshot");
            Check(captured.Rewards.Count(entry => entry.Cards != null) >= 2 && captured.Rewards.Any(entry => entry.Relic != null)
                && captured.Rewards.Any(entry => entry.Potion != null) && captured.Rewards.Any(entry => entry.Children?.Length == 2),
                "snapshot contains concrete card choices, relic, potion and linked rewards");
            RaceSession.AtomicWrite(expectedPath, StateJson.Serialize(new Expected(captured, player.Gold, session.Me.VictoryCount, player.Deck.Cards.Count,(pendingClaim || confirmedClaim) && !host ? RaceRuntime.RewardKey(taken) : "")));
            if((pendingClaim || confirmedClaim) && !host) {
                GD.Print(confirmedClaim ? "[FDZF REWARD RECOVERY COMPLETE] client disconnected after claim confirmation before post-claim checkpoint" : "[FDZF REWARD RECOVERY COMPLETE] client disconnected before claim submission");game.GetTree().Quit(0);return;
            }
            session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
            session.Transport.SendMessage(new RaceMessage { Kind = "reward_test_ready" });
            await Wait(() => peerReady, "peer completed native reward fixture");
            if (host)
            {
                await Wait(() => session.Recovery.BackupCount == 2, "both reward backups reached host");
                await Wait(() => session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0,
                    "peer disconnects while its exact remaining rewards are only stored by host");
                await session.Recovery.SaveHost();
                if(pendingClaim) {
                    var archive=RaceRecovery.Read("1");var proof=archive.Players["2"].CompletedClaims!.Single();
                    Check(StateJson.Deserialize<Match>(archive.MatchJson).ConsumedRewards.Contains("2:"+proof.Key),"real host archive repairs the disconnected native completion");
                    // This variant deliberately exits at the earlier disconnect;
                    // its completion evidence is the acknowledged backup, actual
                    // peer removal and repaired archive checked immediately above.
                    peerDone=true;
                }
                if(confirmedClaim) {
                    var archive=RaceRecovery.Read("1");var remote=archive.Players["2"];
                    var restored=NativeCheckpoint.Restore(new NativeCheckpoint.Bundle("0.5.1",archive.RulesVersion,0,archive.MatchJson,remote.NativePacket,remote.AppliedReceipts,remote.BattleKey,remote.Rewards));
                    var remoteExpected=StateJson.Deserialize<Expected>(File.ReadAllText(Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!,"expected-2.json")));
                    Check(restored.Run.Players.Single().Gold==remoteExpected.Gold
                        && restored.Match.ConsumedRewards.Contains("2:"+remoteExpected.PendingClaimKey),
                        "host archive pairs the already confirmed claim with the granted native inventory despite immediate disconnect");
                    peerDone=true;
                }
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
            if(expected.PendingClaimKey.Length>0) Check(session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+expected.PendingClaimKey),"fresh resumed process retains the repaired native claim consumption");
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
            var linked=rewards.Rewards.OfType<LinkedRewardSet>().Single();
            var linkedGold=linked.Rewards.OfType<GoldReward>().First();int beforeLinked=player.Gold;
            Check(await linkedGold.SelectUnsynchronized() && player.Gold==beforeLinked+linkedGold.Amount,
                "restored linked native reward grants through the same protected claim path");
            Check(!await linkedGold.SelectUnsynchronized() && player.Gold==beforeLinked+linkedGold.Amount,
                "restored linked native reward cannot be claimed twice");
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
