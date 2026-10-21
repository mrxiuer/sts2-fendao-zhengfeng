namespace FendaoZhengfeng.Core;

public sealed record Command(string Id, string Actor, string Kind, string Key = "", string Effect = "", int Amount = 0,
    int Act = 0, int Floor = 0, string Value = "", bool Boss = false, Route Route = Route.Disrupt, int MaxHealth = 0, string Profile = "");

/// <summary>Host-ordered rules reducer. Failed commands never partially consume a reward or mutate the live state.</summary>
public sealed class Engine(Match state)
{
    public Match State { get; private set; } = state;
    public bool Execute(Command c)
    {
        if (c.Profile.Length > 350000) throw new RuleException("公开资料超出限制。");
        if (c.Id.Length is < 1 or > 128 || c.Key.Length > 256 || c.Value.Length > 1024) throw new RuleException("消息超出限制。");
        if (State.ProcessedCommands.Contains(c.Id)) return false;
        if (!State.Racers.ContainsKey(c.Actor)) throw new RuleException("玩家不属于本局。");
        var next = StateJson.Deserialize<Match>(StateJson.Serialize(State));
        Apply(next, c);
        next.ProcessedCommands.Add(c.Id); next.Revision++; State = next; return true;
    }
    private static void Apply(Match m, Command c)
    {
        var p = m.Racers[c.Actor];
        if (p.Eliminated && c.Kind is not ("ack" or "progress")) throw new RuleException("已淘汰玩家不能执行此操作。");
        switch (c.Kind)
        {
            case "profile":
                var profile = StateJson.Deserialize<PublicProfile>(c.Profile);
                if (profile.Packet.Length > 300000 || profile.Act < p.Act || profile.Act > p.Act + 1
                    || profile.Health < 0 || profile.MaxHealth < 1 || profile.Health > profile.MaxHealth
                    || profile.Floor < 0 || profile.Floor > 200 || profile.Room.Length > 64 || profile.Gold < 0 || profile.Gold > 10000000
                    || profile.Cards?.Count > 1000 || profile.Cards?.Any(card => card.Id.Length > 64 || card.Packet.Length > 15000 || card.Name.Length > 256) == true
                    || profile.Cards?.Select(card => card.Id).Distinct().Count() != profile.Cards?.Count)
                    throw new RuleException("公开资料无效。");
                if (profile.Position is { } point && (point.Act != profile.Act || point.Row < -1 || point.Row > 200 || point.Col < 0 || point.Col > 100))
                    throw new RuleException("地图坐标无效。");
                p.PublicPacket = profile.Packet; p.ProfileRevision++; p.Gold = profile.Gold; p.MarketCards = profile.Cards ?? [];
                p.TailSpent = profile.TailSpent;
                p.Health = profile.Health; p.MaxHealth = profile.MaxHealth; p.Act = profile.Act; p.Floor = profile.Floor; p.Room = profile.Room;
                p.Position = profile.Position;
                if (profile.Position is { } location && (p.Trail.Count == 0 || p.Trail[^1] != location))
                {
                    if (p.Trail.Count >= 600) throw new RuleException("路线记录过长。");
                    p.Trail.Add(location);
                }
                break;
            case "progress":
                if (c.Act < p.Act || c.Act > p.Act + 1 || c.Floor < 0 || c.Floor > 200) throw new RuleException("无效的进度。");
                p.Act = c.Act; p.Floor = c.Floor; p.Room = c.Value; p.Health = Math.Max(0, c.Amount); p.MaxHealth = Math.Max(0, c.MaxHealth); break;
            case "battle_start":
                if (p.InBattle) { if (p.BattleKey == c.Key) break; throw new RuleException("上一场尚未结束。"); }
                p.Battle++; p.BattleKey = c.Key; p.InBattle = true; p.BurdenSnapshot = p.Burden;
                p.AdversityGoldSnapshot = p.Has("adversity_profit") ? p.Effects.Keys.Select(Catalog.Get)
                    .Where(e => e.Kind == EffectKind.Curse && p.Has(e.Id)).Sum(e => Balance.AdversityGoldPerKind(e.Tier)) : 0;
                break;
            case "victory":
                if (c.Key != p.BattleKey || !p.InBattle) throw new RuleException("战斗记录不匹配。");
                p.InBattle = false;
                if (!p.WonBattles.Add(c.Key)) break;
                p.VictoryCount++;
                if (p.Effects.TryGetValue("adversity_profit", out var profit))
                {
                    int gold = Math.Min(p.AdversityGoldSnapshot, Math.Max(0, Balance.AdversityGoldCap(profit.Rank) - profit.Used));
                    if (gold > 0) { profit.Used += gold; p.Receipts.Add(new(c.Id + ":profit", "gold", "", gold)); }
                }
                p.AdversityGoldSnapshot = 0;
                if (p.Has("restoration")) p.Receipts.Add(new(c.Id + ":restoration", "heal", "", 2 + p.Effects["restoration"].Rank));
                if (p.Has("expedition") && c.Value == "Elite") AdvanceExpedition(p, c.Id);

                if (p.Position is { } cleared && p.Room is "Monster" or "Elite" or "Boss") p.ClearedNodes.Add(cleared);
                if (p.Has("scapegoat")) { var g = p.Effects["scapegoat"]; g.Used++; if (g.Used % 5 == 0) p.Ghosts = Math.Min(2, p.Ghosts + 1); }
                if (p.Has("catch_up")) { var h = p.Effects["catch_up"]; if (--h.Used <= 0) p.Effects.Remove("catch_up"); }
                if (c.Boss && p.KilledBossActs.Add(p.Act))
                {
                    foreach (var id in p.Effects.Values.Where(h => !Catalog.Get(h.Id).Permanent && h.ExpiresAfterAct >= 0 && h.ExpiresAfterAct <= p.Act).Select(h => h.Id).ToList()) p.Effects.Remove(id);
                    bool first = p.ResetCount > 0 || m.BossFirstKills.TryAdd(p.Act, p.Id);
                    p.BossPrizes.Add((p.ResetCount > 0 ? "lap" + p.ResetCount + ":" : "") + p.Act + ":" + (first ? "first" : "later"));
                }
                break;
            case "expedition_emit":
                if (!p.ExpeditionRewards.Remove(c.Key)) throw new RuleException("悬赏奖励已处理。"); break;
            case "defeat": p.Eliminated = true; p.InBattle = false; break;
            case "revive": p.RevivalCount++; break;
            case "heart_finish":
                if (!p.InBattle || p.Act != 4 || p.Room != "Boss" || c.Key != p.BattleKey) throw new RuleException("心脏击杀记录不匹配。");
                if (!p.Finished) { p.Finished = true; m.FinishOrder.Add(p.Id); }
                // Keep the battle open until native victory hooks commit its rewards and history.
                break;
            case "finish":
                if (!p.Finished) { p.Finished = true; p.InBattle = false; m.FinishOrder.Add(p.Id); }
                break;
            case "offer":
                if (c.Route != Route.FirstKill && !p.WonBattles.Contains(c.Key.Split('|')[0])) throw new RuleException("献祭缺少获胜记录。");
                if (c.Route == Route.FirstKill && !p.BossPrizes.Contains(c.Key)) throw new RuleException("没有首杀奖励。");
                if (c.Route == Route.FirstKill && !c.Key.EndsWith(":first", StringComparison.Ordinal)) throw new RuleException("该奖励不含增益。");
                if (m.ConsumedRewards.Contains(p.Id + ":" + c.Key)) throw new RuleException("奖励已经处理。");
                var offerId = p.Id + ":" + c.Key + ":" + c.Route;
                if (!m.Offers.ContainsKey(offerId)) m.Offers[offerId] = new Offer
                {
                    Id = offerId,
                    Player = p.Id,
                    RewardKey = c.Key,
                    Route = c.Route,
                    Boss = c.Boss,
                    Candidates = Candidates.Generate(m, p, c.Key, c.Route, c.Boss)
                };
                break;
            case "choose":
                if (!m.Offers.TryGetValue(c.Key, out var offer) || offer.Player != p.Id || offer.Resolved || !offer.Candidates.Contains(c.Effect)) throw new RuleException("无效或已经处理的候选。");
                var token = p.Id + ":" + offer.RewardKey;
                if (m.ConsumedRewards.Contains(token)) throw new RuleException("同一奖励已领取或献祭。");
                var effect = Catalog.Get(c.Effect);
                if (!m.EnabledEffects.Contains(effect.Id)) throw new RuleException("房主已禁用该效果。");
                if (offer.Route == Route.FirstKill && !p.BossPrizes.Contains(offer.RewardKey)) throw new RuleException("首杀奖励已经领取。");
                if (effect.Kind == EffectKind.Curse)
                {
                    foreach (var other in m.Racers.Values.Where(r => (r.Id != p.Id || effect.Id == "farewell_upgrades") && !r.Eliminated && !r.Finished)) Attack(m, p, other, effect, c.Id);
                }
                else Give(m, p, effect, c.Id);
                m.ConsumedRewards.Add(token); offer.Resolved = true;
                if (offer.Route == Route.FirstKill) p.BossPrizes.Remove(offer.RewardKey);
                p.Receipts.Add(new(c.Id + ":choice", "choice", offer.RewardKey));
                break;
            case "claim":
                if (!m.ConsumedRewards.Add(p.Id + ":" + c.Key)) throw new RuleException("奖励已处理。"); break;
            case "boss_shield":
                if (!p.BossPrizes.Remove(c.Key)) throw new RuleException("额外奖励已处理。"); m.ConsumedRewards.Add(p.Id + ":" + c.Key); p.Shields++; break;
            case "boss_cleanse":
                if (!p.BossPrizes.Contains(c.Key) || !c.Key.EndsWith(":later", StringComparison.Ordinal)) throw new RuleException("没有可用净化奖励。");
                Cleanse(p, c.Effect, CleanseAt.Boss, c.Id); p.BossPrizes.Remove(c.Key); break;
            case "camp_cleanse":
                Service(p, c.Key, "camp"); Cleanse(p, c.Effect, CleanseAt.Camp, c.Id); break;
            case "shop_cleanse":
                throw new RuleException("商店不提供模式效果净化。");
            case "repair":
                if (!p.Has("bloom_decay")) throw new RuleException("没有可恢复的契约。"); Service(p, c.Key, "camp"); p.Receipts.Add(new(c.Id + ":repair", "repair")); break;
            case "siphon":
                if (!p.Has("siphon") || !p.WonBattles.Contains(c.Key)) throw new RuleException("当前不能汲取。");
                Service(p, c.Key, "siphon"); p.PendingEnergy += 2; p.Receipts.Add(new(c.Id + ":gold", "gold", "", 10 + 2 * p.Effects["siphon"].Rank)); break;
            case "consume_opening":
                if (!p.InBattle || p.BattleKey != c.Key) throw new RuleException("开战记录不匹配。");
                Service(p, c.Key, "opening"); p.Receipts.Add(new(c.Id + ":energy", "energy", "", p.PendingEnergy)); p.PendingEnergy = 0;
                foreach (var card in p.PendingStatuses) p.Receipts.Add(new(c.Id + ":status:" + p.Receipts.Count, "status", card)); p.PendingStatuses.Clear(); break;
            case "status":
                if (!p.Has("shared_misery") || !p.InBattle || c.Amount is < 1 or > 100) break;
                foreach (var other in m.Racers.Values.Where(r => r.Id != p.Id && !r.Eliminated && !r.Finished))
                    for (int i = 0; i < c.Amount && other.PendingStatuses.Count < 5; i++) other.PendingStatuses.Add(c.Value); break;
            case "gold":
                if (c.Amount is < 0 or > 100000) throw new RuleException("金币收入无效。");
                foreach (var other in m.Racers.Values.Where(r => r.Id != p.Id && r.Has("shared_fortune"))) other.FortuneIncome[p.Id] = other.FortuneIncome.GetValueOrDefault(p.Id) + c.Amount; break;
            case "node_end":
                Service(p, c.Key, "node");
                if (p.Has("shared_fortune"))
                {
                    int income = p.FortuneIncome.Values.DefaultIfEmpty().Max() / 2; int cap = 30 + 5 * p.Effects["shared_fortune"].Rank;
                    if (income > 0) p.Receipts.Add(new(c.Id + ":fortune", "gold", "", Math.Min(cap, income)));
                }
                p.FortuneIncome.Clear(); break;
            case "market_buy":
                if (!p.Has("good_connections") || p.Finished || p.ResetPending) throw new RuleException("当前不能购买卡牌。");
                if (c.Key == p.Id || !m.Racers.TryGetValue(c.Key, out var seller) || seller.Eliminated || seller.Finished || seller.ResetPending) throw new RuleException("卖方不可用。");
                var offeredCard = seller.MarketCards.SingleOrDefault(card => card.Id == c.Effect) ?? throw new RuleException("该牌已不在对手公开牌组中，请刷新。");
                if (m.Trades.Any(trade => trade.Seller == seller.Id && trade.Card.Id == offeredCard.Id)) throw new RuleException("这张牌已被购买。");
                int price = checked(100 + 50 * p.Purchases);
                int pendingSpend = p.Receipts.Where(receipt => receipt.Kind == "spend_gold").Sum(receipt => receipt.Amount);
                if (c.Amount - pendingSpend < price || p.Gold < price) throw new RuleException("金币不足。");
                p.Purchases++; p.Gold -= price;
                p.Receipts.Add(new(c.Id + ":market-cost", "spend_gold", "", price));
                seller.Receipts.Add(new(c.Id + ":market-income", "gold", "", price));
                m.Trades.Add(new CardTrade { Id = c.Id, Seller = seller.Id, Buyer = p.Id, Card = offeredCard, Price = price, SellerBattle = seller.Battle + 1, BuyerBattle = p.Battle + 1 });
                break;
            case "trade_removed":
            case "trade_delivered":
                var transfer = m.Trades.SingleOrDefault(trade => trade.Id == c.Key) ?? throw new RuleException("交易不存在。");
                if (c.Kind == "trade_removed") { if (transfer.Seller != p.Id || !p.InBattle || p.Battle < transfer.SellerBattle) throw new RuleException("卖方交接尚未到期。"); transfer.Removed = true; }
                else { if (transfer.Buyer != p.Id || !p.InBattle || p.Battle < transfer.BuyerBattle) throw new RuleException("买方交接尚未到期。"); transfer.Delivered = true; }
                break;
            case "reset_use":
                if (!p.Has("r_key") || p.ResetCharges <= 0 || p.InBattle || p.Eliminated || p.Finished || p.ResetPending) throw new RuleException("当前不能使用R键。");
                p.ResetCharges--; p.ResetCount++; p.ResetPending = true; p.Act = 0; p.Floor = 0; p.Position = null; p.Room = "Reset";
                p.KilledBossActs.Clear(); p.Trail.Clear(); p.ClearedNodes.Clear(); p.BattleKey = "";
                break;
            case "reset_ready":
                if (!p.ResetPending) throw new RuleException("没有待完成的重置。"); p.ResetPending = false; break;
            case "ghost":
                if (p.Ghosts <= 0) throw new RuleException("没有替死鬼。"); p.Ghosts--; break;
            case "tail_refill":
                if (!p.Has("tail_refill") || p.InBattle || p.Finished || p.ResetPending || !p.TailSpent || p.Receipts.Any(r => r.Kind == "refill_tail")) throw new RuleException("需要在非战斗时补充已消耗的蜥蜴尾巴。");
                if (p.Gold - p.Receipts.Where(r => r.Kind == "spend_gold").Sum(r => r.Amount) < Balance.TailRefillCost) throw new RuleException("需要350金币。");
                p.TailSpent = false;
                p.Receipts.Add(new(c.Id + ":tail-cost", "spend_gold", "", Balance.TailRefillCost));
                p.Receipts.Add(new(c.Id + ":tail", "refill_tail")); break;
            case "ack": p.Receipts.RemoveAll(r => r.Id == c.Key); break;
            default: throw new RuleException("未知操作：" + c.Kind);
        }
    }
    private static void Service(Racer p, string key, string kind)
    { if (!p.UsedServices.Add(kind + ":" + key)) throw new RuleException("当前节点的服务已使用。"); }
    private static void Attack(Match m, Racer source, Racer target, Effect e, string eventId)
    {
        if (target.Shields > 0) { target.Shields--; return; }
        if (!target.Effects.TryGetValue(e.Id, out var h)) { h = new Holding { Id = e.Id }; target.Effects.Add(e.Id, h); }
        h.Grants.Add(new Grant { Source = source.Id, EventId = eventId, ActiveBattle = target.Battle + 1 });
        if (target.Has("retaliation"))
        {
            var r = target.Effects["retaliation"]; int used = r.Counters.GetValueOrDefault(source.Id);
            if (used < 2 + r.Rank) { r.Counters[source.Id] = used + 1; source.Receipts.Add(new(eventId + ":retaliation:" + target.Id, "curse_card")); }
        }
    }
    private static void Give(Match m, Racer p, Effect e, string eventId)
    {
        if (e.Id is "small_light" or "good_connections" or "tail_refill" && p.Has(e.Id)) throw new RuleException("此常驻主动增益不能重复持有。");
        if (e.Id == "war_feeds_war" && Balance.WarFeedsWarStacks(p.Effects.GetValueOrDefault(e.Id)) >= 3) throw new RuleException("以战养战已达到3层上限。");
        if (e.Id == "flight_wings" && p.Has(e.Id) && p.Effects[e.Id].Rank >= Balance.FlightMaxRank) throw new RuleException("凌空之翼已达到80%减伤上限。");
        p.History[e.Id] = p.History.GetValueOrDefault(e.Id) + 1;
        if (e.Id == "r_key") p.ResetCharges++;
        if (e.Id == "expedition" && p.Has(e.Id)) AdvanceExpedition(p, eventId);
        if (e.Kind == EffectKind.Contract)
        {
            if (p.Effects.ContainsKey(e.Id)) throw new RuleException("契约不能重复持有。");
            p.Effects[e.Id] = new Holding { Id = e.Id, Grants = [new Grant { EventId = eventId, Source = p.Id, ActiveBattle = p.Battle + 1 }] }; return;
        }
        if (p.Effects.TryGetValue(e.Id, out var h))
        {
            h.Rank++; if (e.Id == "shared_misery") p.Shields++;
            if (e.Id == "scapegoat") p.Ghosts = Math.Min(2, p.Ghosts + 1);
            if (e.Id == "catch_up") h.Used++;
        }
        else
        {
            h = new Holding { Id = e.Id }; p.Effects[e.Id] = h;
            if (e.Id == "catch_up") h.Used = m.Racers.Values.Any(r => r.Act > p.Act) ? 2 : 1;
        }
        h.ExpiresAfterAct = e.Permanent || e.Id == "catch_up" ? -1 : p.Act + (p.KilledBossActs.Contains(p.Act) ? 1 : 0);
    }
    public static int ExpeditionGoal(int act) => act < 2 ? 3 : act < 4 ? 2 : 1;
    private static void AdvanceExpedition(Racer p, string eventId)
    {
        p.ExpeditionProgress++;
        int goal = ExpeditionGoal(p.Act);
        while (p.ExpeditionProgress >= goal) { p.ExpeditionProgress -= goal; p.ExpeditionRewards.Add(p.BattleKey + "|expedition:" + eventId + ":" + p.ExpeditionRewards.Count); }
    }
    private static int Cleanse(Racer p, string id, CleanseAt at, string commandId)
    {
        var e = Catalog.Get(id);
        if (e.Kind == EffectKind.Boon || !p.Effects.TryGetValue(id, out var h)) throw new RuleException("没有可净化的效果。");
        if (at == CleanseAt.Camp && e.Tier != Tier.Ordinary || at == CleanseAt.Shop && e.Tier == Tier.Boss) throw new RuleException("此渠道不能净化该档位。");
        int cost = at == CleanseAt.Shop ? Balance.CleansePrice(e, h.Grants.Count) ?? throw new RuleException("不可净化。") : 0;
        p.Effects.Remove(id);
        if (id == "no_plan") p.Receipts.Add(new(commandId + ":restore-skills", "restore_skills"));
        if (id == "bloom_decay") p.Receipts.Add(new(commandId + ":restore", "restore_deck"));
        return cost;
    }
}
