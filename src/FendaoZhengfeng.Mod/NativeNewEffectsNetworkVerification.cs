using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Relics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

// Actual ENet processes and host-owned archive; only reward fixtures are seeded.
internal static class NativeNewEffectsNetworkVerification
{
    private static readonly string[] effects = ["substitute_puppet", "burrow_sanctuary", "giant_form", "extra_timeline", "heartstrings_charm", "twin_slime", "encore_rewards"];
    private sealed record Expected(string BattleKey, string[] Deck, string Puppet, string Twin, int Health,
        int MaxHp, string RerollKey, int BurrowProgress, int BurrowCharges,
        int Gold, int Victories, SavedCombatRewards? Rewards, SavedMerchant? Shop, int ShopCounter, int ResetCount);
    private static void Check(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException(reason); GD.Print("[FDZF NEW EFFECTS PASS] " + reason); }
    private static async Task Wait(Func<bool> ready, string reason)
    { long until = System.Environment.TickCount64 + 45000; while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(30); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        bool reset = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESET_NETWORK") == "1";
        bool four = reset || System.Environment.GetEnvironmentVariable("FDZF_VERIFY_FOUR_PLAYERS") == "1";
        bool owner = host || four && session.LocalId == "3";
        string[] holders = four ? ["1", "3"] : ["1"];
        string[] grants = reset ? [.. effects, "r_key"] : effects;
        int initialHp = reset && owner ? 4000 + 100 * int.Parse(session.LocalId) : four ? 400 + 100 * int.Parse(session.LocalId) : 500;
        string complete = reset ? "[FDZF RESET NETWORK COMPLETE] " : four ? "[FDZF FOUR PLAYERS COMPLETE] " : "[FDZF NEW EFFECTS COMPLETE] ";
        var player = run.Players.Single(); var context = new ThrowingPlayerChoiceContext();
        var received = new HashSet<string>();
        var arrivals = new Dictionary<string, HashSet<string>>();
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        {
            if (!four)
            { if (sender != session.Transport.NetId && message.Kind == "newfx_barrier") received.Add(message.Content); return; }
            if (message.Kind == "four_arrive" && host && session.Engine.State.Racers.ContainsKey(sender.ToString()))
            {
                if (!arrivals.TryGetValue(message.Content, out var set)) arrivals[message.Content] = set = [];
                set.Add(sender.ToString());
            }
            if (message.Kind == "four_release" && sender == session.HostId) received.Add(message.Content);
        });
        async Task Barrier(string phase)
        {
            long until = System.Environment.TickCount64 + 45000;
            if (four && host)
            {
                if (!arrivals.TryGetValue(phase, out var set)) arrivals[phase] = set = [];
                set.Add(session.LocalId);
                await Wait(() => set.Count == 4, "four participant barrier: " + phase);
                session.Transport.SendMessage(new RaceMessage { Kind = "four_release", Content = phase });
                await Task.Delay(150);
                return;
            }
            do
            {
                session.Transport.SendMessage(new RaceMessage { Kind = four ? "four_arrive" : "newfx_barrier", Content = phase });
                await Task.Delay(100);
                if (System.Environment.TickCount64 > until) throw new TimeoutException("new effects peer barrier: " + phase);
            } while (!received.Contains(phase));
            if (!four) session.Transport.SendMessage(new RaceMessage { Kind = "newfx_barrier", Content = phase });
        }
        string expectedPath = Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "new-effects-" + session.LocalId + ".json");
        if (resume)
        {
            var expected = StateJson.Deserialize<Expected>(File.ReadAllText(expectedPath));
            Check(session.Me.ResetCount == expected.ResetCount && (!reset || !owner || session.Me.ResetCharges == 0
                && session.Me.BossPrizes.Contains("lap1:0:first")),
                "personal reset count, spent charge and lap-specific boss first-kill prize survive host archive");
            if (four && !owner)
            {
                await Wait(() => expected.Shop != null ? run.CurrentRoom is MerchantRoom : RaceRuntime.CombatRewards != null,
                    "mixed progress restored service or rewards");
                Check(!session.Me.InBattle && session.Me.VictoryCount == expected.Victories,
                    "finished control battle is not restarted or settled twice while other players resume fights");
                Check(player.Creature.CurrentHp == expected.Health && player.Creature.MaxHp == expected.MaxHp
                    && player.Gold == expected.Gold && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(expected.Deck),
                    "claimed rewards and merchant purchases survive exactly once");
                if (expected.Shop != null)
                {
                    var shop = (MerchantRoom)run.CurrentRoom!;
                    var actual = MerchantRecovery.Capture(shop);
                    Check(StateJson.Serialize(actual) == StateJson.Serialize(expected.Shop),
                        "merchant actual cards relics potions prices sale and removal flags survive four-player archive exactly");
                    Check(player.PlayerRng.Shops.ToSerializable().counter == expected.ShopCounter,
                        "restoring the merchant consumes no shop RNG");
                    var inventory = shop.GetLocalInventory();
                    int gold = player.Gold;
                    var sold = inventory.CharacterCardEntries.First();
                    var slot = game.FindChildren("*", "", true, false).OfType<NMerchantCard>().Single(node => ReferenceEquals(node.Entry, sold));
                    Check(!sold.IsStocked && !slot.Visible && slot.MouseFilter == Control.MouseFilterEnum.Ignore && player.Gold == gold,
                        "sold merchant card restores as a hidden non-interactive native slot");
                    var remaining = inventory.CharacterCardEntries.First(entry => entry.IsStocked);
                    int cost = remaining.Cost, count = player.Deck.Cards.Count;
                    Check(await remaining.OnTryPurchaseWrapper(inventory) && player.Gold == gold - cost
                        && player.Deck.Cards.Count == count + 1 && !remaining.IsStocked,
                        "a restored merchant entry remains actually purchasable with its preserved price");
                }
                else
                {
                    var actual = RewardRecovery.Capture((CombatRoom)run.CurrentRoom!)!;
                    Check(StateJson.Serialize(actual) == StateJson.Serialize(expected.Rewards),
                        "unclaimed rewards remain exact while other players were saved mid-combat or in shop");
                    var reward = RaceRuntime.CombatRewards!.Rewards.OfType<GoldReward>().First();
                    int gold = player.Gold;
                    Check(await reward.SelectUnsynchronized() && player.Gold > gold,
                        "mixed progress restored reward is actually claimable");
                }
                await Barrier("restored");
                GD.Print(complete + session.LocalId + " resumed=true"); game.GetTree().Quit(0); return;
            }
            await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "new effects restored combat turn");
            Check(session.Restored != null && session.Me.BattleKey == expected.BattleKey && RaceModifier.Active!.Turn == 1,
                "host archive restores the same unfinished personal battle at turn one");
            Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(expected.Deck),
                "permanent timeline and charm grants survive restart exactly once with native properties");
            Check(RaceModifier.Active!.PuppetVitalsJson == expected.Puppet && RaceModifier.Active.TwinSlimeStateJson == expected.Twin,
                "puppet vitals and both actual slime body pools survive actual process restart; puppet expected=" + expected.Puppet
                    + " actual=" + RaceModifier.Active.PuppetVitalsJson + "; twin expected=" + expected.Twin + " actual=" + RaceModifier.Active.TwinSlimeStateJson);
            Check(player.Creature.CurrentHp == expected.Health && player.Creature.MaxHp == expected.MaxHp,
                "restored body HP and max HP match the captured body without free healing or re-halving");
            Check(RewardRerollRules.NativeUsage(session.Me, expected.RerollKey).Used == (owner ? 1 : 0),
                "native refresh count survives host-owned recovery and stays isolated to the holder");
            Check(BurrowRules.Read(session.Me).Charges == expected.BurrowCharges
                && BurrowRules.Read(session.Me).Progress == expected.BurrowProgress
                && BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Ready,
                "restarting an unfinished room refunds its unfinished burrow use once and preserves progress");
            Check(owner ? PuppetFollower.Find(player) != null && TwinSlimeRuntime.Find(player) != null
                : PuppetFollower.Find(player) == null && TwinSlimeRuntime.Find(player) == null,
                "native companion and second body are reconstructed only for the actual holder");
            Check(RaceRuntime.RoomKey == expected.BattleKey, "restored room keeps its identity after the opening replay flag clears");
            if (four && session.LocalId == "3")
            {
                CheckOrbDisplay(player, "restored Defect cracked-core opening");
                await OrbCmd.Channel<LightningOrb>(context, player);
                CheckOrbDisplay(player, "channeling another orb after restored split");
            }
            int victories = session.Me.VictoryCount;
            await DebugTesting.WinBattle();
            await Wait(() => !session.Me.InBattle, "restored battle victory");
            Check(session.Me.VictoryCount == victories + 1, "the restored real battle settles exactly once");
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "next battle after restored victory");
            Check(session.Me.InBattle && session.Me.BattleKey != expected.BattleKey,
                "the next native room gets a fresh identity even if its numeric room Id is reused");
            await DebugTesting.WinBattle();
            await Wait(() => !session.Me.InBattle, "next battle victory after restore");
            Check(session.Me.VictoryCount == victories + 2, "the next victory is not mistaken for a duplicate of the restored battle");
            await Barrier("restored");
            GD.Print(complete + session.LocalId + " resumed=true"); game.GetTree().Quit(0); return;
        }
        await CreatureCmd.SetMaxAndCurrentHp(player.Creature, initialHp);
        await Barrier("ready");
        if (host)
        {
            foreach (string holder in holders)
                foreach (string id in grants)
                {
                    session.Engine.State.EnabledEffects.Add(id);
                    string key = "newfx-grant:" + holder + ":" + id;
                    session.Engine.State.Offers[key] = new Offer { Id = key, Player = holder,
                        RewardKey = key, Candidates = [id], Route = Route.Grow };
                }
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        }
        await Wait(() => holders.All(holder => grants.All(id => session.Engine.State.Offers.ContainsKey("newfx-grant:" + holder + ":" + id))), "host offers reach all participants");
        if (owner)
        {
            foreach (string id in grants)
                await session.Submit(session.New("choose", "newfx-grant:" + session.LocalId + ":" + id, id));
            await RaceModifier.Active!.ApplyReceipts(context, false);
        }
        await Wait(() => holders.All(holder => grants.All(id => session.Engine.State.Racers[holder].Has(id))), "all seven actual acquisition commands cross ENet for every holder");
        await Barrier("acquired");
        if (host)
        {
            foreach (string holder in holders)
                foreach (int index in Enumerable.Range(0, 3))
                {
                    var racer = session.Engine.State.Racers[holder];
                    string key = "newfx-charge:" + index;
                    racer.WonBattles.Add(key); BurrowRules.RecordVictory(racer, key);
                }
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        }
        await Wait(() => holders.All(holder => BurrowRules.Read(session.Engine.State.Racers[holder]).Charges == 1), "host seeds initial charge for each holder");
        await Barrier("granted");
        Check(session.Engine.State.Racers.Values.All(racer => grants.All(id => racer.Has(id) == holders.Contains(racer.Id))),
            "all seven grants are host ordered and leave control racers without the effects");
        Check(player.Deck.Cards.OfType<ExtraTimelineCard>().Count() == (owner ? 1 : 0)
            && player.Deck.Cards.OfType<CharmCard>().Count() == (owner ? 1 : 0),
            "native permanent card grants reach only each owner, once each");
        var point = run.Map.StartingMapPoint.Children.OrderBy(item => item.coord.ToString(), StringComparer.Ordinal).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "new effect first native combat");
        await Barrier("battle");
        var combatDeckBefore = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
        var enemy = player.Creature.CombatState!.Enemies.First(creature => creature.IsAlive);
        var puppet = PuppetFollower.Find(player);
        Check((puppet != null) == owner, "the real puppet is spawned only in its owner's separate battle");
        int beforeHit = owner ? puppet!.CurrentHp : player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move | ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
        Check(owner ? puppet!.CurrentHp == beforeHit - 20 && player.Creature.CurrentHp == initialHp
            : player.Creature.CurrentHp == beforeHit - 20,
            "the real incoming attack reaches the owner's puppet and the control player's native defenses and HP");
        await PowerCmd.Apply<WeakPower>(context, player.Creature, 1, enemy, null);
        Check(owner ? puppet!.GetPowerAmount<WeakPower>() == 1 && player.Creature.GetPowerAmount<WeakPower>() == 0
            : player.Creature.GetPowerAmount<WeakPower>() == 1,
            "native combat debuffs are redirected only for the puppet owner");
        if (owner)
        {
            await CreatureCmd.SetCurrentHp(player.Creature, initialHp - 50);
            int beforeHeal = player.Creature.CurrentHp;
            await CreatureCmd.Heal(player.Creature, 10);
            Check(player.Creature.CurrentHp == beforeHeal, "injured puppet owner does not gain HP from native healing");
            await CreatureCmd.SetCurrentHp(player.Creature, initialHp);
            var peer = session.Engine.State.Racers["2"];
            var packets = peer.MarketCards.ToArray();
            int skillIndex = -1;
            for (int index = 0; index < packets.Length; index++)
            {
                var decoded = NativeCheckpoint.DecodeCard(packets[index].Packet, run, player);
                try { if (decoded.Type == CardType.Skill && decoded.TargetType == TargetType.Self) { skillIndex = index; break; } }
                finally { decoded.RemoveFromState(); }
            }
            Check(skillIndex >= 0, "actual ENet peer profile contains a native defensive card to copy");
            var selector = new TestCardSelector(); selector.PrepareToSelect([skillIndex]);
            int energy = player.PlayerCombatState!.Energy; var originalDeck = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
            using (CardSelectCmd.UseSelector(selector)) await TimelineEffects.PlayFromOpponent(context, player, "2");
            Check(player.PlayerCombatState.Energy == energy && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(originalDeck)
                && session.Engine.State.Racers["2"].MarketCards.SequenceEqual(packets) && puppet!.Block > 0,
                "real peer card packets produce a free native copy while both permanent decks remain intact");
            var charm = player.Creature.CombatState!.CreateCard<CharmCard>(player);
            await CardPileCmd.AddGeneratedCardToCombat(charm, PileType.Hand, player);
            await CardCmd.AutoPlay(context, charm, enemy);
            Check(enemy.GetPower<CharmRedirectPower>()?.Amount == 2,
                "the actual charm card installs its two-turn enemy status in the owner's separate fight");
        }
        await Barrier("combat-probed");
        Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(combatDeckBefore),
            "both actual native permanent decks remain unchanged after remote copying and charm");
        await DebugTesting.WinBattle();
        await Wait(() => !session.Me.InBattle && RaceRuntime.CombatRewards != null, "new effect native reward generation");
        var rewards = RaceRuntime.CombatRewards!; var potion = new PotionReward(player); potion.Populate(); rewards.Rewards.Add(potion);
        string rerollKey = RaceRuntime.RewardKey(potion);
        var originalPotion = potion.Potion;
        if (owner)
        {
            await RewardRerollEffect.RefreshAsync(null, potion, rewards);
            Check(RewardRerollRules.NativeUsage(session.Me, rerollKey).Used == 1 && potion.Potion != null && !ReferenceEquals(originalPotion, potion.Potion),
                "native potion reward refresh commits across actual ENet and changes native content");
        }
        else
        {
            bool rejected = false;
            try { await RewardRerollEffect.RefreshAsync(null, potion, rewards); } catch (RuleException) { rejected = true; }
            Check(rejected && RewardRerollRules.NativeUsage(session.Me, rerollKey).Used == 0,
                "control player cannot refresh without owning the effect");
        }
        await Barrier("rewards");
        if (!four || owner)
        {
            if (four)
                for (int extra = 0; extra < int.Parse(session.LocalId) - 1; extra++)
                {
                    await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                    await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "independent progress combat");
                    await DebugTesting.WinBattle();
                    await Wait(() => !session.Me.InBattle, "independent progress victory");
                }
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "new effect second combat ready");
            if (owner)
            {
                puppet = PuppetFollower.Find(player)!;
                Check(puppet.CurrentHp == initialHp - 20 && puppet.Block == 0, "puppet HP persists into a new battle while old block is cleared");
                var enemy2 = player.Creature.CombatState!.Enemies.First(creature => creature.IsAlive);
                await CreatureCmd.Damage(context, player.Creature, initialHp + 1, ValueProp.Unpowered | ValueProp.Unblockable, enemy2, null, null);
                await Wait(() => TwinSlimeRuntime.Find(player)?.SetupTask.IsCompleted == true, "real lethal non-attack splits both native player bodies");
                var pair = TwinSlimeRuntime.Find(player)!;
                Check(pair.First.CurrentHp == initialHp / 2 && pair.Second.CurrentHp == initialHp / 2 && puppet.IsAlive,
                    "lethal non-attack creates two independent half-HP bodies without consuming the puppet");
            if (four && session.LocalId == "3")
            {
                CheckOrbDisplay(player, "existing orb survives the first actual split");
                await OrbCmd.Channel<LightningOrb>(context, player);
                CheckOrbDisplay(player, "channeling after the first actual split");
            }
                var node = pair.Second.GetCreatureNode()!;
                await Wait(() => node.GetNodeOrNull<GiantScaleFollower>("GiantScaleFollower") is { } scale
                    && Math.Abs(node.Visuals.DefaultScale - scale.NaturalScale * 2) < 0.05f,
                    "the second body receives the actual holder's Giant scaling");
                await BurrowEffect.Activate(endTurn: false);
                Check(BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Burrowed && puppet.Block == pair.Bodies.Sum(body => body.MaxHp),
                    "burrow state is committed across ENet and its real generated block reaches the guarding puppet");
            }
        }
        else if (session.LocalId == "2")
        {
            if (reset)
            {
                await RunManager.Instance.EnterMapCoord(run.Map.BossMapPoint.coord);
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "control first boss");
                await DebugTesting.WinBattle();
                await Wait(() => !session.Me.InBattle && RaceRuntime.CombatRewards != null, "control first boss native rewards");
                rewards = RaceRuntime.CombatRewards!;
                Check(session.Engine.State.BossFirstKills.GetValueOrDefault(0) == "2", "control player earns the actual act-one first boss kill before any reset");
            }
            var claimed = new GoldReward(77, player); rewards.Rewards.Add(claimed);
            int gold = player.Gold;
            Check(await claimed.SelectUnsynchronized() && player.Gold == gold + 77,
                "reward control claims native gold and stays on its remaining rewards screen");
        }
        else
        {
            await RelicCmd.Obtain<MealTicket>(player);
            await RunManager.Instance.EnterRoomDebug(RoomType.Shop);
            await PlayerCmd.GainGold(10000, player);
            var inventory = ((MerchantRoom)run.CurrentRoom!).GetLocalInventory();
            var card = inventory.CharacterCardEntries.First();
            int gold = player.Gold, count = player.Deck.Cards.Count, cost = card.Cost;
            Check(await card.OnTryPurchaseWrapper(inventory) && player.Gold == gold - cost
                && player.Deck.Cards.Count == count + 1 && !card.IsStocked,
                "merchant control buys a real native card and leaves its slot sold out before save");
            var boughtPotion = inventory.PotionEntries.First();
            Check(await boughtPotion.OnTryPurchaseWrapper(inventory) && !boughtPotion.IsStocked,
                "merchant control buys a real potion before save");
            var selector = new TestCardSelector(); selector.PrepareToSelect([0]);
            using (CardSelectCmd.UseSelector(selector))
                Check(await inventory.CardRemovalEntry!.OnTryPurchaseWrapper(inventory, cancelable: false),
                    "merchant control uses actual native card removal before save");
            Check(inventory.CardRemovalEntry!.Used, "merchant removal service is consumed before save");
        }
        if (reset)
        {
            await Barrier("reset-premise");
            await Wait(() => session.Engine.State.BossFirstKills.GetValueOrDefault(0) == "2", "actual first-kill reaches all peers");
            int controlGold = player.Gold, controlHp = player.Creature.CurrentHp;
            var controlDeck = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
            var controlRoom = run.CurrentRoom;
            int controlWins = session.Me.VictoryCount;
            if (owner)
            {
                await DebugTesting.WinBattle();
                await Wait(() => !session.Me.InBattle && TwinSlimeRuntime.Find(player) == null, "split bodies collapse before R use");
                await CreatureCmd.SetCurrentHp(player.Creature, initialHp - 123);
                var before = RaceModifier.Active!;
                string[] deck = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
                string relics = string.Join("|", player.Relics.Select(relic => relic.Id + ":" + relic.IsUsedUp));
                int hp = player.Creature.CurrentHp, max = player.Creature.MaxHp, gold = player.Gold;
                string puppetVitals = before.PuppetVitalsJson;
                var burrow = BurrowRules.Read(session.Me);
                string map = NativeCampaignVerification.MapSignature(run.Map);
                await AdvancedEffects.ResetRun();
                Check(run.CurrentActIndex == 0 && run.ActFloor == 0 && session.Me.ResetCount == 1 && session.Me.ResetCharges == 0 && !session.Me.ResetPending,
                    "each actual holder consumes one R charge and returns to the act-one start");
                Check(player.Creature.CurrentHp == hp && player.Creature.MaxHp == max && player.Gold == gold
                    && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(deck)
                    && string.Join("|", player.Relics.Select(relic => relic.Id + ":" + relic.IsUsedUp)) == relics
                    && player.Relics.OfType<LizardTail>().Count() == 1,
                    "R preserves injured native HP, resources, exact permanent timeline/charm cards and relic use state without a second tail");
                Check(before.PuppetVitalsJson == puppetVitals && BurrowRules.Read(session.Me).Charges == burrow.Charges
                    && BurrowRules.Read(session.Me).Progress == burrow.Progress && RewardRerollRules.NativeUsage(session.Me, rerollKey).Used == 1,
                    "R preserves wounded puppet HP, burrow charge/progress and previously spent native refresh count");
                Check(NativeCampaignVerification.MapSignature(run.Map) == map,
                    "R regenerates the same shared act-one map despite different personal battle progress");
                await RunManager.Instance.EnterMapCoord(run.Map.BossMapPoint.coord);
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "post-R actual boss battle");
                var boss = player.Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive);
                Check(boss.MaxHp == (int)Math.Ceiling(before.ResetBaseHp(boss) * 1.2m)
                    && GiantForm.CanExecute(boss.MaxHp, player.Creature.CurrentHp, session.Me.Effects[GiantForm.EffectId].Rank),
                    "post-R boss receives exactly twenty percent HP scaling and meets the seeded Giant execution threshold");
                var attack = player.Deck.Cards.First(card => card.Type == CardType.Attack);
                for (int wave = 0; wave < 8 && session.Me.InBattle; wave++)
                {
                    foreach (var target in player.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive).ToArray())
                    {
                        int hpBeforeHit = target.CurrentHp;
                        var hit = await new MegaCrit.Sts2.Core.Commands.Builders.AttackCommand(1).FromCard(attack, null).Targeting(target).WithNoAttackerAnim().Execute(context);
                        GD.Print($"[FDZF RESET HIT] owner={session.LocalId} rank={GiantForm.Rank} hero={player.Creature.CurrentHp} target={target.ModelId} hp={hpBeforeHit}->{target.CurrentHp} results="
                            + string.Join(";", hit.Results.SelectMany(results => results).Select(result => $"{result.UnblockedDamage}/{result.WasTargetKilled}")));
                    }
                    // This probe uses the native attack builder outside CardPlay;
                    // the normal card wrapper would perform this victory check.
                    await CombatManager.Instance.CheckWinCondition();
                    await Task.Delay(60);
                }
                await Wait(() => !session.Me.InBattle && RaceRuntime.CombatRewards != null, "Giant native boss execution settlement");
                Check(session.Me.BossPrizes.Contains("lap1:0:first") && session.Engine.State.BossFirstKills[0] == "2",
                    "actual Giant boss execution earns R first-kill prize without overwriting the other player's original first kill");
                await RunManager.Instance.EnterNextAct();
                Check(run.CurrentActIndex == 1, "native next-act travel after R and boss execution has no disposed boss map node");
                if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESET_EMPTY_MAP") == "1")
                    await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                else
                {
                    var nextPoint = run.Map.StartingMapPoint.Children.OrderBy(point => point.coord.ToString(), StringComparer.Ordinal).First();
                    await RunManager.Instance.EnterMapCoord(nextPoint.coord);
                }
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "post-R new act battle");
                Check(PuppetFollower.Find(player) is { } keptPuppet && keptPuppet.CurrentHp == initialHp - 20
                    && keptPuppet.MaxHp == initialHp, "R monster scaling never enlarges or heals the allied puppet");
                await CreatureCmd.Damage(context, player.Creature, player.Creature.CurrentHp + 1,
                    ValueProp.Unpowered | ValueProp.Unblockable, player.Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive), null, null);
                await Wait(() => TwinSlimeRuntime.Find(player)?.SetupTask.IsCompleted == true, "new real split after R and act travel");
                if (BurrowRules.Read(session.Me).Charges > 0) await BurrowEffect.Activate(endTurn: false);
                Check(TwinSlimeRuntime.Find(player) is { } savedPair && savedPair.First.MaxHp == initialHp / 2 && savedPair.Second.MaxHp == initialHp / 2,
                    "post-R new combat creates the two actual bodies at the preserved maximum HP");
            }
            await Barrier("reset-probed");
            if (!owner)
                Check(ReferenceEquals(run.CurrentRoom, controlRoom) && session.Me.ResetCount == 0 && session.Me.VictoryCount == controlWins
                    && player.Gold == controlGold && player.Creature.CurrentHp == controlHp
                    && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(controlDeck),
                    "two other players keep their original boss reward/shop progress while both owners reset and advance acts");
        }
        var modifier = RaceModifier.Active!;
        var expectedSave = new Expected(session.Me.BattleKey, player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray(),
            modifier.PuppetVitalsJson, modifier.TwinSlimeStateJson, player.Creature.CurrentHp, player.Creature.MaxHp,
            rerollKey, BurrowRules.Read(session.Me).Progress, BurrowRules.Read(session.Me).Charges + (BurrowRules.Read(session.Me).Phase == BurrowRules.Phase.Burrowed ? 1 : 0),
            player.Gold, session.Me.VictoryCount, run.CurrentRoom is CombatRoom savedRoom ? RewardRecovery.Capture(savedRoom) : null,
            run.CurrentRoom is MerchantRoom savedShop ? MerchantRecovery.Capture(savedShop) : null,
            player.PlayerRng.Shops.ToSerializable().counter, session.Me.ResetCount);
        RaceSession.AtomicWrite(expectedPath, StateJson.Serialize(expectedSave));
        session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
        await Barrier("snapshots");
        if (host)
        {
            await Wait(() => session.Recovery.BackupCount == (four ? 4 : 2) && session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0,
                "control client disconnects after its native snapshot reached the host");
            await session.Recovery.SaveHost();
            Check(RaceRecovery.Read("1").Players.Count == (four ? 4 : 2), "host owns both actual independent native snapshots");
        }
        else { await Task.Delay(300); session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit); }
        GD.Print(complete + session.LocalId + " resumed=false"); game.GetTree().Quit(0);
    }
    private static void CheckOrbDisplay(Player player, string phase)
    {
        var pair = TwinSlimeRuntime.Find(player)!;
        var manager = pair.First.GetCreatureNode()!.OrbManager!;
        var slots = (List<NOrb>)AccessTools.Field(typeof(NOrbManager), "_orbs").GetValue(manager)!;
        var queue = player.PlayerCombatState!.OrbQueue;
        Check(slots.Count == queue.Capacity && slots.Where(slot => slot.Model != null).Select(slot => slot.Model).SequenceEqual(queue.Orbs)
            && !pair.Second.GetCreatureNode()!.OrbManager!.Visible,
            "native shared orb queue has one accurate visual display: " + phase);
    }

}
