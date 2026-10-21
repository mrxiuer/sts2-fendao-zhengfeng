using FendaoZhengfeng.Core;

int passed = 0;
void Test(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); Environment.ExitCode = 1; } }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
Engine New() => new(new Match { Id = "test", Seed = "seed", Racers = new() { ["a"] = new() { Id = "a" }, ["b"] = new() { Id = "b" }, ["c"] = new() { Id = "c" } } });
void Grant(Engine engine, string player, string id, int count = 1) { engine.State.Racers[player].Effects[id] = new() { Id = id, Grants = Enumerable.Range(0, count).Select(i => new Grant { EventId = i.ToString(), Source = "a", ActiveBattle = 1 }).ToList() }; }
Test("owned permanent actives leave every pool and stale grants are atomic", () => {
    var e = New(); Grant(e, "a", "small_light"); Grant(e, "a", "good_connections"); Grant(e, "a", "tail_refill");
    foreach (var route in new[] { Route.Mixed, Route.Grow, Route.FirstKill })
        for (int i = 0; i < 40; i++) {
            var picks = Candidates.Generate(e.State, e.State.Racers["a"], "owned" + i, route, true);
            Check(!picks.Contains("small_light") && !picks.Contains("good_connections") && !picks.Contains("tail_refill"));
        }
    foreach (var id in new[] { "small_light", "good_connections", "tail_refill" }) {
        e.State.Offers[id] = new() { Id = id, Player = "a", RewardKey = "stale:" + id, Route = Route.Grow, Candidates = [id] };
        var before = StateJson.Serialize(e.State); bool rejected = false;
        try { e.Execute(new("duplicate:" + id, "a", "choose", id, id)); } catch (RuleException) { rejected = true; }
        Check(rejected && StateJson.Serialize(e.State) == before);
    }
});
Test("small and light is a rare boss boon with no elite sacrifice access", () => {
    var effect = Catalog.Get("small_light");
    Check(effect.Tier == Tier.Boss && effect.Rarity == Rarity.Rare && !effect.ElitePool && effect.BossPool && effect.FirstKillPool);
    Check(effect.Description.Contains("失去3最大生命"));
});
Test("catalog has 55 unique effects", () => { Check(Catalog.All.Count == 55); Check(Catalog.All.Select(e => e.Id).Distinct().Count() == 55); });
Test("cleanse prices", () => { Check(Balance.CleansePrice(Catalog.Get("exhaustion"), 1) == 60); Check(Balance.CleansePrice(Catalog.Get("exhaustion"), 3) == 80); Check(Balance.CleansePrice(Catalog.Get("carapace"), 3) == 195); Check(Balance.CleansePrice(Catalog.Get("hard_shell"), 1) == null); });
Test("repeat weight floor", () => { Check(Balance.RepeatWeight(0) == 1); Check(Balance.RepeatWeight(1) == 0.4); Check(Math.Abs(Balance.RepeatWeight(2) - 0.16) < 1e-10); Check(Balance.RepeatWeight(10) == 0.1); });
Test("burden saturates monotonically", () => { Check(Balance.RareMultiplier(0, false) == 1); Check(Balance.RareMultiplier(8, false) == 2); Check(Balance.RareMultiplier(100000, false) < 3); Check(Balance.RareMultiplier(32, false) > Balance.RareMultiplier(8, false)); });
Test("pending attacks activate only next battle", () => { var e = New(); Grant(e, "b", "exhaustion", 2); Check(e.State.Racers["b"].Burden == 0); e.Execute(new("1", "b", "battle_start", "battle")); Check(e.State.Racers["b"].Burden == 2); Check(e.State.Racers["b"].BurdenSnapshot == 2); });
Test("boss cleanse removes whole family", () => { var e = New(); Grant(e, "b", "ritual", 3); e.State.Racers["b"].BossPrizes.Add("0:later"); e.Execute(new("c", "b", "boss_cleanse", "0:later", "ritual")); Check(!e.State.Racers["b"].Effects.ContainsKey("ritual")); });
Test("failed cleanse is atomic", () => { var e = New(); Grant(e, "b", "ritual"); var before = StateJson.Serialize(e.State); try { e.Execute(new("c", "b", "shop_cleanse", "node", "ritual", 1000)); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); });
Test("insufficient gold does not consume cleanse", () => { var e = New(); Grant(e, "b", "carapace"); var before = StateJson.Serialize(e.State); try { e.Execute(new("c", "b", "shop_cleanse", "node", "carapace", 1)); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); });
Test("command replay is idempotent", () => { var e = New(); var c = new Command("x", "a", "revive"); e.Execute(c); Check(!e.Execute(c)); Check(e.State.Racers["a"].RevivalCount == 1); });
Test("candidate restore is deterministic", () => { var e = New(); var a = Candidates.Generate(e.State, e.State.Racers["a"], "key", Route.Disrupt, false); var b = Candidates.Generate(e.State, e.State.Racers["a"], "key", Route.Disrupt, false); Check(a.SequenceEqual(b)); Check(a.Distinct().Count() == a.Count); });
Test("boss guarantee", () => { var e = New(); var c = Candidates.Generate(e.State, e.State.Racers["a"], "key", Route.Disrupt, true); Check(c.Count == 3); Check(c.Count(id => Catalog.Get(id).Tier == Tier.Boss) == 1); });
Test("contract guarantee and active exclusion", () => { var e = New(); Grant(e, "a", "borrowed_time"); var c = Candidates.Generate(e.State, e.State.Racers["a"], "key", Route.Contract, true); Check(!c.Contains("borrowed_time")); Check(c.Count(id => Catalog.Get(id).Tier == Tier.Elite) >= 2); });
Test("victory recorded once and ghosts charge five wins", () => { var e = New(); e.State.Racers["a"].Effects["scapegoat"] = new() { Id = "scapegoat" }; for (int i = 0; i < 5; i++) { e.Execute(new("s" + i, "a", "battle_start", i.ToString())); e.Execute(new("v" + i, "a", "victory", i.ToString())); } Check(e.State.Racers["a"].VictoryCount == 5); Check(e.State.Racers["a"].Ghosts == 1); });
Test("champion stays first and others can finish", () => { var e = New(); e.Execute(new("1", "b", "finish")); e.Execute(new("2", "a", "finish")); Check(e.State.FinishOrder.SequenceEqual(["b", "a"])); });
Test("persistent state round trip", () => { var e = New(); Grant(e, "b", "exhaustion", 3); e.Execute(new("1", "a", "revive")); var json = StateJson.Serialize(e.State); var copy = StateJson.Deserialize<Match>(json); Check(StateJson.Serialize(copy) == json); });
void ReadyReward(Engine e, string actor = "a", string key = "battle")
{ e.Execute(new("start-" + actor + key, actor, "battle_start", key)); e.Execute(new("win-" + actor + key, actor, "victory", key)); }
void OfferEffect(Engine e, string id, string effect, Route route = Route.Disrupt)
{ e.State.Offers[id] = new() { Id = id, Player = "a", RewardKey = "battle|0", Route = route, Candidates = [effect] }; }
Test("group attack independently consumes shields", () => { var e = New(); e.State.Racers["b"].Shields = 1; OfferEffect(e, "o", "exhaustion"); e.Execute(new("choice", "a", "choose", "o", "exhaustion")); Check(e.State.Racers["b"].Shields == 0); Check(!e.State.Racers["b"].Effects.ContainsKey("exhaustion")); Check(e.State.Racers["c"].Effects["exhaustion"].Grants.Count == 1); Check(!e.State.Racers["a"].Effects.ContainsKey("exhaustion")); });
Test("cannot claim two routes for one reward", () => { var e = New(); OfferEffect(e, "o", "exhaustion"); OfferEffect(e, "other", "imbalance"); e.Execute(new("first", "a", "choose", "o", "exhaustion")); var before = StateJson.Serialize(e.State); try { e.Execute(new("second", "a", "choose", "other", "imbalance")); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); });
Test("contract applies only to self and has zero burden", () => { var e = New(); OfferEffect(e, "o", "borrowed_time", Route.Contract); e.Execute(new("choice", "a", "choose", "o", "borrowed_time")); e.Execute(new("start", "a", "battle_start", "battle")); Check(e.State.Racers["a"].Has("borrowed_time")); Check(e.State.Racers["a"].Burden == 0); Check(!e.State.Racers["b"].Has("borrowed_time")); });
Test("retaliation does not recurse and blocked attacks do not reflect", () => { var e = New(); e.State.Racers["b"].Effects["retaliation"] = new() { Id = "retaliation" }; e.State.Racers["c"].Effects["retaliation"] = new() { Id = "retaliation" }; e.State.Racers["b"].Shields = 1; OfferEffect(e, "o", "exhaustion"); e.Execute(new("choice", "a", "choose", "o", "exhaustion")); Check(e.State.Racers["a"].Receipts.Count(r => r.Kind == "curse_card") == 1); Check(e.State.Racers["b"].Effects["retaliation"].Counters.Count == 0); });
Test("camp cleanses pending and active ordinary stacks together", () => { var e = New(); Grant(e, "b", "exhaustion", 2); e.State.Racers["b"].Effects["exhaustion"].Grants[0].ActiveBattle = 0; e.Execute(new("clean", "b", "camp_cleanse", "node", "exhaustion")); Check(!e.State.Racers["b"].Effects.ContainsKey("exhaustion")); Check(!e.State.Racers["b"].Receipts.Any(r => r.Kind == "spend_gold")); });
Test("first-kill shield invalidates an older boon offer", () => { var e = New(); e.State.Racers["a"].BossPrizes.Add("0:first"); e.Execute(new("offer", "a", "offer", "0:first", Route: Route.FirstKill)); var offer = e.State.Offers.Values.Single(); e.Execute(new("shield", "a", "boss_shield", "0:first")); var before = StateJson.Serialize(e.State); try { e.Execute(new("choose", "a", "choose", offer.Id, offer.Candidates[0])); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); });
Test("status forwarding cap is global across sources", () => { var e = New(); foreach (var id in new[] { "a", "c" }) { e.State.Racers[id].Effects["shared_misery"] = new() { Id = "shared_misery" }; e.Execute(new("start" + id, id, "battle_start", id)); e.Execute(new("status" + id, id, "status", Amount: 4, Value: "slimed")); } Check(e.State.Racers["b"].PendingStatuses.Count == 5); });
Test("opening grants consumed once", () => { var e = New(); e.State.Racers["a"].PendingEnergy = 2; e.Execute(new("start", "a", "battle_start", "battle")); e.Execute(new("opening", "a", "consume_opening", "battle")); Check(e.State.Racers["a"].PendingEnergy == 0); Check(e.State.Racers["a"].Receipts.Single(r => r.Kind == "energy").Amount == 2); var before = StateJson.Serialize(e.State); try { e.Execute(new("again", "a", "consume_opening", "battle")); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); });
Test("unavailable contract offer preserves reward", () => { var e = New(); ReadyReward(e); foreach (var x in Catalog.All.Where(x => x.Kind == EffectKind.Contract)) Grant(e, "a", x.Id); var before = StateJson.Serialize(e.State); try { e.Execute(new("offer", "a", "offer", "battle|0", Boss: true, Route: Route.Contract)); } catch (RuleException) { } Check(StateJson.Serialize(e.State) == before); Check(!e.State.ConsumedRewards.Contains("a:battle|0")); });
Test("permanent boon repeat strengthens one instance", () => { var e = New(); OfferEffect(e, "one", "battle_hardened", Route.Grow); e.Execute(new("1", "a", "choose", "one", "battle_hardened")); e.State.Offers["two"] = new() { Id = "two", Player = "a", RewardKey = "battle|1", Route = Route.Grow, Candidates = ["battle_hardened"] }; e.Execute(new("2", "a", "choose", "two", "battle_hardened")); Check(e.State.Racers["a"].Effects.Count == 1); Check(e.State.Racers["a"].Effects["battle_hardened"].Rank == 1); Check(e.State.Racers["a"].History["battle_hardened"] == 2); });
Test("new post-boss boon survives its granting boss", () => { var e = New(); e.Execute(new("start", "a", "battle_start", "boss")); e.Execute(new("win", "a", "victory", "boss", Boss: true)); OfferEffect(e, "boon", "shared_fortune", Route.Grow); e.Execute(new("pick", "a", "choose", "boon", "shared_fortune")); Check(e.State.Racers["a"].Effects["shared_fortune"].ExpiresAfterAct == 1); });
Test("ghost cap has no hidden charges", () => { var e = New(); e.State.Racers["a"].Effects["scapegoat"] = new() { Id = "scapegoat" }; for (int i = 0; i < 20; i++) ReadyReward(e, "a", i.ToString()); Check(e.State.Racers["a"].Ghosts == 2); Check(e.State.Racers["a"].Effects["scapegoat"].Used == 20); e.Execute(new("spend", "a", "ghost")); Check(e.State.Racers["a"].Ghosts == 1); });
Test("mixed elite pool has no fixed category slots and no boss curses", () => {
    var e = New(); var kinds = new HashSet<EffectKind>(); bool repeatsCategory = false;
    for (int i = 0; i < 200; i++) {
        var picks = Candidates.Generate(e.State, e.State.Racers["a"], "mixed" + i, Route.Mixed, false).Select(Catalog.Get).ToList();
        Check(picks.Count == 3 && picks.Select(x => x.Id).Distinct().Count() == 3);
        Check(picks.All(x => x.Kind != EffectKind.Curse || x.Tier != Tier.Boss));
        foreach (var x in picks) kinds.Add(x.Kind);
        repeatsCategory |= picks.Select(x => x.Kind).Distinct().Count() < 3;
    }
    Check(kinds.Count == 3 && repeatsCategory);
});
Test("mixed boss pool guarantees a boss curse and shuffles its slot", () => {
    var e = New(); var positions = new HashSet<int>();
    for (int i = 0; i < 100; i++) {
        var picks = Candidates.Generate(e.State, e.State.Racers["a"], "boss" + i, Route.Mixed, true).Select(Catalog.Get).ToList();
        Check(picks.Count == 3 && picks.Any(x => x.Kind == EffectKind.Curse && x.Tier == Tier.Boss));
        positions.Add(picks.FindIndex(x => x.Kind == EffectKind.Curse && x.Tier == Tier.Boss));
    }
    Check(positions.Count == 3);
});
Test("mixed selections dispatch by effect kind and consume only their relic", () => {
    foreach (var id in new[] { "exhaustion", "borrowed_time", "battle_hardened" }) {
        var e = New(); OfferEffect(e, "mixed", id, Route.Mixed);
        e.Execute(new("pick", "a", "choose", "mixed", id));
        Check(e.State.ConsumedRewards.Contains("a:battle|0"));
        Check(!e.State.ConsumedRewards.Contains("a:battle|cards"));
        bool curse = Catalog.Get(id).Kind == EffectKind.Curse;
        Check(e.State.Racers["a"].Effects.ContainsKey(id) != curse);
        Check(e.State.Racers["b"].Effects.ContainsKey(id) == curse);
        Check(e.State.Racers["c"].Effects.ContainsKey(id) == curse);
    }
});
Test("mixed candidates survive cancel and restore without reroll", () => {
    var e = New(); ReadyReward(e);
    e.Execute(new("offer1", "a", "offer", "battle|0", Route: Route.Mixed));
    var first = e.State.Offers.Values.Single().Candidates.ToList();
    e = new Engine(StateJson.Deserialize<Match>(StateJson.Serialize(e.State)));
    e.Execute(new("offer2", "a", "offer", "battle|0", Route: Route.Mixed));
    Check(first.SequenceEqual(e.State.Offers.Values.Single().Candidates));
    Check(!e.State.ConsumedRewards.Contains("a:battle|0"));
});
Test("disabled effects stay out of every candidate route", () => {
    var e = New(); var p = e.State.Racers["a"];
    e.State.EnabledEffects.Remove("exhaustion"); e.State.EnabledEffects.Remove("hard_shell"); e.State.EnabledEffects.Remove("battle_hardened");
    foreach (var route in Enum.GetValues<Route>()) foreach (bool boss in new[] {false,true})
        for (int i=0;i<20;i++) Check(Candidates.Generate(e.State,p,"filter"+i,route,boss).All(e.State.EnabledEffects.Contains));
});
Test("disabled boss guarantee yields to allowed pool", () => {
    var e=New(); e.State.EnabledEffects = ["shared_misery", "borrowed_time", "retaliation"];
    var choices=Candidates.Generate(e.State,e.State.Racers["a"],"boss",Route.Mixed,true);
    Check(choices.Count==3 && choices.All(e.State.EnabledEffects.Contains));
});
Test("empty pool preserves reward and rejected disabled offer is atomic", () => {
    var e=New(); ReadyReward(e); e.State.EnabledEffects.Clear(); var before=StateJson.Serialize(e.State);
    try {e.Execute(new("offer","a","offer","battle|0",Route:Route.Mixed));}catch(RuleException){}
    Check(StateJson.Serialize(e.State)==before);
    OfferEffect(e,"old","exhaustion",Route.Mixed); before=StateJson.Serialize(e.State);
    try {e.Execute(new("pick","a","choose","old","exhaustion"));}catch(RuleException){}
    Check(StateJson.Serialize(e.State)==before);
});
Test("public profiles preserve exact route and do not duplicate stationary nodes", () => {
    var e=New();
    void Send(string id,int row,int col,int act=0) => e.Execute(new(id,"b","profile", Profile:StateJson.Serialize(new PublicProfile("packet",53,80,act,row+1,"Elite",new(act,row,col)))));
    Send("1",0,1);Send("2",0,1);Send("3",1,2);Send("4",0,3,1);
    var p=e.State.Racers["b"]; Check(p.Health==53 && p.MaxHealth==80 && p.ProfileRevision==4);
    Check(p.Trail.SequenceEqual(new[]{new RouteStep(0,0,1),new RouteStep(0,1,2),new RouteStep(1,0,3)}));
    Check(StateJson.Deserialize<Match>(StateJson.Serialize(e.State)).Racers["b"].Trail.SequenceEqual(p.Trail));
});
Test("invalid public profile leaves state untouched", () => {
    var e=New();var before=StateJson.Serialize(e.State);
    try { e.Execute(new("bad","b","profile",Profile:StateJson.Serialize(new PublicProfile("x",200,80,0,1,"Elite",new(0,500,1)))));}catch(RuleException){}
    Check(StateJson.Serialize(e.State)==before);
});
Test("completion marks require victory and are independent per player", () => {
    var e=New(); var node=new RouteStep(0,1,2);
    foreach (var id in new[]{"a","b"}) e.Execute(new("p"+id,id,"profile",Profile:StateJson.Serialize(new PublicProfile("x",80,80,0,2,"Monster",node))));
    Check(e.State.Racers.Values.All(p=>p.ClearedNodes.Count==0));
    ReadyReward(e,"a","one"); Check(e.State.Racers["a"].ClearedNodes.SetEquals([node])); Check(e.State.Racers["b"].ClearedNodes.Count==0);
    e.Execute(new("lose-start","b","battle_start","lost")); e.Execute(new("lose","b","defeat")); Check(e.State.Racers["b"].ClearedNodes.Count==0);
    ReadyReward(e,"a","two"); Check(e.State.Racers["a"].ClearedNodes.Count==1);
    Check(StateJson.Deserialize<Match>(StateJson.Serialize(e.State)).Racers["a"].ClearedNodes.SetEquals([node]));
});

Test("global upgrade curse includes caster and remains next-battle activated", () => {
    var e=New(); OfferEffect(e,"global","farewell_upgrades"); e.Execute(new("cast","a","choose","global","farewell_upgrades"));
    Check(e.State.Racers.Values.All(p=>p.Effects.ContainsKey("farewell_upgrades") && !p.Has("farewell_upgrades")));
    e.Execute(new("start-global","b","battle_start","global-next")); Check(e.State.Racers["b"].Has("farewell_upgrades")); Check(!e.State.Racers["a"].Has("farewell_upgrades"));
});
Test("market reserves unique cards and sends real gold receipts", () => {
    var e=New();Grant(e,"a","good_connections");e.State.Racers["a"].Gold=500;
    e.State.Racers["b"].MarketCards=[new("card1","packet","Strike"),new("card2","packet2","Defend")];
    e.Execute(new("buy1","a","market_buy","b","card1",500));
    Check(e.State.Trades.Single().Price==100 && e.State.Trades.Single().BuyerBattle==1 && e.State.Trades.Single().SellerBattle==1);
    Check(e.State.Racers["a"].Purchases==1 && e.State.Racers["a"].Receipts.Single().Amount==100 && e.State.Racers["b"].Receipts.Single().Amount==100);
    var before=StateJson.Serialize(e.State);try{e.Execute(new("duplicate-sale","a","market_buy","b","card1",400));}catch(RuleException){} Check(StateJson.Serialize(e.State)==before);
    e.Execute(new("buy2","a","market_buy","b","card2",500));Check(e.State.Trades.Last().Price==150);
});
Test("market failure is atomic and transfer sides have separate battle gates", () => {
    var e=New();Grant(e,"a","good_connections");e.State.Racers["a"].Gold=99;e.State.Racers["b"].MarketCards=[new("card","packet","Strike")];
    var before=StateJson.Serialize(e.State);try{e.Execute(new("poor","a","market_buy","b","card",99));}catch(RuleException){} Check(StateJson.Serialize(e.State)==before);
    e.State.Racers["a"].Gold=500;e.Execute(new("buy","a","market_buy","b","card",500));
    before=StateJson.Serialize(e.State);try{e.Execute(new("early","b","trade_removed","buy"));}catch(RuleException){}Check(StateJson.Serialize(e.State)==before);
    e.Execute(new("a-start","a","battle_start","a-next"));e.Execute(new("deliver","a","trade_delivered","buy"));Check(e.State.Trades.Single().Delivered && !e.State.Trades.Single().Removed);
    e.Execute(new("b-start","b","battle_start","b-next"));e.Execute(new("remove","b","trade_removed","buy"));Check(e.State.Trades.Single().Removed);
    Check(StateJson.Deserialize<Match>(StateJson.Serialize(e.State)).Trades.Single().Delivered);
});
Test("R grants a charge per acquisition and preserves resources with distinct boss rewards", () => {
    var e=New();OfferEffect(e,"r-grant","r_key");e.Execute(new("take-r","a","choose","r-grant","r_key"));var p=e.State.Racers["a"];
    p.Act=2;p.Gold=123;p.Shields=3;p.RevivalCount=1;p.Ghosts=2; p.KilledBossActs.Add(0);p.BossPrizes.Add("0:first");
    e.Execute(new("reset","a","reset_use"));p=e.State.Racers["a"];
    Check(p.Act==0 && p.ResetCount==1 && p.ResetCharges==0 && p.Gold==123 && p.Shields==3 && p.RevivalCount==1 && p.Ghosts==2 && p.KilledBossActs.Count==0);
    e.Execute(new("ready-reset","a","reset_ready"));e.State.BossFirstKills[0]="b";
    e.Execute(new("reset-battle","a","battle_start","lap1:boss"));e.Execute(new("reset-win","a","victory","lap1:boss",Boss:true));
    Check(e.State.Racers["a"].BossPrizes.Contains("lap1:0:first") && e.State.BossFirstKills[0]=="b");
    var before=StateJson.Serialize(e.State);try{e.Execute(new("again-reset","a","reset_use"));}catch(RuleException){}Check(StateJson.Serialize(e.State)==before);
});
Test("R cannot reset an active fight", () => {
    var e=New();Grant(e,"a","r_key");e.State.Racers["a"].ResetCharges=1;e.Execute(new("active-r","a","battle_start","fight"));
    var before=StateJson.Serialize(e.State);try{e.Execute(new("invalid-r","a","reset_use"));}catch(RuleException){}Check(StateJson.Serialize(e.State)==before);
});
Test("cleansing no plan schedules recovery rather than deleting stored skills", () => {
    var e=New();Grant(e,"a","no_plan");e.State.Racers["a"].BossPrizes.Add("0:later");e.Execute(new("plan-clean","a","boss_cleanse","0:later","no_plan")); Check(e.State.Racers["a"].Receipts.Any(r=>r.Kind=="restore_skills"));
});
Test("boss effects including contracts never leak into elite sacrifices", () => {
    var e=New();
    foreach(var route in new[]{Route.Mixed,Route.Contract,Route.Grow,Route.Disrupt})
        for(int i=0;i<100;i++) Check(Candidates.Generate(e.State,e.State.Racers["a"],route+":"+i,route,false).All(id=>Catalog.Get(id).Tier!=Tier.Boss));
});
Test("boon tier and pool classifications agree", () => {
    foreach(var b in Catalog.All.Where(x=>x.Kind==EffectKind.Boon)) {
        Check(b.ElitePool==(b.Tier!=Tier.Boss));Check(b.BossPool==(b.Tier!=Tier.Ordinary));
    }
    Check(Catalog.Get("no_plan").Tier==Tier.Boss && Catalog.Get("abet").Tier==Tier.Ordinary);
});
Test("promoted boss contracts cannot be cleansed at shops", () => {
    var e=New();Grant(e,"a","bloom_decay");var before=StateJson.Serialize(e.State);bool rejected=false;
    try{e.Execute(new("illegal-cleanse","a","shop_cleanse","shop","bloom_decay",9999));}catch(RuleException){rejected=true;}
    Check(rejected && StateJson.Serialize(e.State)==before);
});
Test("expedition uses only post-acquisition elite wins and scales with act", () => {
    var e=New();var p=e.State.Racers["a"];p.VictoryCount=99;Grant(e,"a","expedition");
    for(int i=0;i<3;i++){e.Execute(new("es"+i,"a","battle_start","elite"+i));e.Execute(new("ev"+i,"a","victory","elite"+i,Value:"Elite"));}
    Check(e.State.Racers["a"].ExpeditionRewards.Count==1 && e.State.Racers["a"].ExpeditionProgress==0);
    e.State.Racers["a"].Act=2;e.Execute(new("m1","a","battle_start","monster"));e.Execute(new("m2","a","victory","monster",Value:"Monster"));Check(e.State.Racers["a"].ExpeditionProgress==0);
    e.State.Racers["a"].Act=4;e.Execute(new("l1","a","battle_start","late"));e.Execute(new("l2","a","victory","late",Value:"Elite"));Check(e.State.Racers["a"].ExpeditionRewards.Count==2);
});
Test("repeat expedition adds progress and emission cannot replay", () => {
    var e=New();Grant(e,"a","expedition");e.State.Racers["a"].Act=4;
    OfferEffect(e,"exp","expedition",Route.Grow);e.Execute(new("pick-exp","a","choose","exp","expedition"));
    var ticket=e.State.Racers["a"].ExpeditionRewards.Single();e.Execute(new("emit","a","expedition_emit",ticket));
    Check(e.State.Racers["a"].ExpeditionRewards.Count==0);var before=StateJson.Serialize(e.State);
    try{e.Execute(new("emit-again","a","expedition_emit",ticket));}catch(RuleException){}Check(StateJson.Serialize(e.State)==before);
});
Test("first double boss does not expire boons or award act-first prize", () => {
    var e=New();Grant(e,"a","sharpen");e.State.Racers["a"].Effects["sharpen"].ExpiresAfterAct=3;e.State.Racers["a"].Act=3;
    e.Execute(new("b1","a","battle_start","boss1"));e.Execute(new("v1","a","victory","boss1",Boss:false,Value:"Boss"));
    Check(e.State.Racers["a"].Has("sharpen") && e.State.Racers["a"].BossPrizes.Count==0);
    e.Execute(new("b2","a","battle_start","boss2"));e.Execute(new("v2","a","victory","boss2",Boss:true,Value:"Boss"));
    Check(!e.State.Racers["a"].Has("sharpen") && e.State.Racers["a"].BossPrizes.Single()=="3:first");
});
Test("retroactive growth keeps pre-acquisition wins and revivals across R and serialization", () => {
    var e=New();for(int i=0;i<3;i++) ReadyReward(e,"a","historical-win:"+i);
    e.Execute(new("real-revive:1","a","revive"));e.Execute(new("real-revive:2","a","revive"));
    Grant(e,"a","battle_hardened");Grant(e,"a","death_defiance");Grant(e,"a","r_key");
    e.State.Racers["a"].ResetCharges=1;
    e.Execute(new("historical-reset","a","reset_use"));e.Execute(new("historical-ready","a","reset_ready"));
    var p=StateJson.Deserialize<Match>(StateJson.Serialize(e.State)).Racers["a"];
    Check(p.VictoryCount==3 && p.RevivalCount==2 && Math.Abs(p.Growth-0.23)<0.00001);
});
Test("scapegoat does not retroactively charge old victories", () => {
    var e=New();for(int i=0;i<5;i++) ReadyReward(e,"a","before-ghost:"+i);
    Grant(e,"a","scapegoat");
    for(int i=0;i<4;i++) ReadyReward(e,"a","after-ghost:"+i);
    Check(e.State.Racers["a"].Ghosts==0);
    ReadyReward(e,"a","after-ghost:4");Check(e.State.Racers["a"].Ghosts==1);
});

Test("burn bridges targets opponents and activates at their next battle with full-stack cleanse", () => {
 var e=New(); ReadyReward(e); OfferEffect(e,"bridges","burn_bridges");
 e.Execute(new("bridge-pick","a","choose","bridges","burn_bridges"));
 Check(Catalog.Get("burn_bridges").Kind == EffectKind.Curse);
 Check(!e.State.Racers["a"].Has("burn_bridges"));
 foreach(var id in new[]{"b","c"}) {
  Check(e.State.Racers[id].Count("burn_bridges")==0);
  e.Execute(new("bridge-start-"+id,id,"battle_start","bridge-battle"));
  Check(e.State.Racers[id].Count("burn_bridges")==1);
 }
 e.State.Racers["b"].BossPrizes.Add("0:later");
 e.Execute(new("bridge-clean","b","boss_cleanse","0:later","burn_bridges"));
 Check(!e.State.Racers["b"].Effects.ContainsKey("burn_bridges"));
});
Test("shared fortune settles highest source once per own node with cap and carry boundaries", () => {
 var e=New(); Grant(e,"a","shared_fortune");
 e.Execute(new("f1","b","gold",Amount:40)); e.Execute(new("f2","b","gold",Amount:40));
 e.Execute(new("f3","c","gold",Amount:30));
 Check(!e.State.Racers["a"].Receipts.Any());
 e.Execute(new("f4","a","node_end","one"));
 Check(e.State.Racers["a"].Receipts.Sum(r=>r.Amount)==30 && e.State.Racers["a"].FortuneIncome.Count==0);
 e.Execute(new("f5","a","node_end","two")); Check(e.State.Racers["a"].Receipts.Count==1);
});

Test("heart schedules one opening and both attacks before each periodic buff for many seeds", () => {
 for(int seed=0;seed<40;seed++) {
  Check(HeartRules.ActionAt(seed.ToString(),1)==HeartAction.Debilitate);
  for(int cycle=0;cycle<12;cycle++) {
   int first=2+cycle*3;
   var attacks=new[]{HeartRules.ActionAt(seed.ToString(),first),HeartRules.ActionAt(seed.ToString(),first+1)};
   Check(attacks.Contains(HeartAction.BloodShots) && attacks.Contains(HeartAction.Echo));
   Check(HeartRules.ActionAt(seed.ToString(),first+2)==HeartAction.Strengthen);
  }
 }
});
Test("heart ward uses quarter of actual HP with rounding and survives R scaling", () => {
 Check(HeartRules.RoundBudget(1260)==315 && HeartRules.RoundBudget(1440)==360);
 Check(HeartRules.RoundBudget(1261)==316 && HeartRules.RoundBudget(1)==1);
});
Test("heart escalation preserves native first cycles and later pressure", () => {
 Check(HeartRules.StrengthForBuff(1)==2 && HeartRules.StrengthForBuff(2)==2 && HeartRules.StrengthForBuff(3)==2);
 Check(HeartRules.StrengthForBuff(4)==12 && HeartRules.StrengthForBuff(5)==22 && HeartRules.StrengthForBuff(10)==22);
 Check(HeartRules.CopyAtRound==4 && HeartRules.RestoreAtRound==7);
});
Test("confirmed heart kill locks rank while leaving native victory bookkeeping open", () => {
 var e=New(); e.State.Racers["a"].Act=4; e.State.Racers["a"].Room="Boss";
 e.Execute(new("heart-start","a","battle_start","heart"));
 e.Execute(new("heart-rank","a","heart_finish","heart"));
 Check(e.State.Racers["a"].Finished && e.State.Racers["a"].InBattle && e.State.FinishOrder.SequenceEqual(["a"]));
 e.Execute(new("other-finish","b","finish"));
 e.Execute(new("native-heart-win","a","victory","heart",Boss:true));
 e.Execute(new("native-heart-finish","a","finish"));
 Check(e.State.FinishOrder.SequenceEqual(["a","b"]) && !e.State.Racers["a"].InBattle && e.State.Racers["a"].VictoryCount==1);
 var before=StateJson.Serialize(e.State); bool rejected=false;
 try { e.Execute(new("invalid-heart","c","heart_finish","bad")); } catch(RuleException) {rejected=true;}
 Check(rejected && StateJson.Serialize(e.State)==before);
});
Test("adversity pays per active curse family and tier on victory", () => {
 var e=New(); Grant(e,"a","adversity_profit"); Grant(e,"a","exhaustion",3); Grant(e,"a","carapace"); Grant(e,"a","ritual"); Grant(e,"a","tomorrow");
 e.Execute(new("start","a","battle_start","battle"));
 Check(e.State.Racers["a"].AdversityGoldSnapshot==9);
 Grant(e,"a","imbalance"); e.State.Racers["a"].Effects["imbalance"].Grants[0].ActiveBattle=2;
 e.Execute(new("win","a","victory","battle"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].Used==9);
 Check(e.State.Racers["a"].Receipts.Single(r=>r.Kind=="gold").Amount==9);
 Check(!e.Execute(new("win","a","victory","battle")));
 e.Execute(new("clean","a","camp_cleanse","camp","exhaustion"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].Used==9);
 e.Execute(new("start2","a","battle_start","battle2"));
 e.Execute(new("win2","a","victory","battle2"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].Used==18);
});
Test("adversity cap is eight hundred plus four hundred per rank without resetting claimed gold", () => {
 var e=New(); Grant(e,"a","adversity_profit"); Grant(e,"a","ritual"); e.State.Racers["a"].Effects["adversity_profit"].Used=797;
 e.Execute(new("s1","a","battle_start","b1")); e.Execute(new("w1","a","victory","b1"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].Used==800 && e.State.Racers["a"].Receipts.Single(r=>r.Kind=="gold").Amount==3);
 e.Execute(new("s2","a","battle_start","b2")); e.Execute(new("w2","a","victory","b2"));
 Check(e.State.Racers["a"].Receipts.Count(r=>r.Kind=="gold")==1);
 OfferEffect(e,"repeat-profit","adversity_profit",Route.Grow);
 e.Execute(new("repeat-profit-pick","a","choose","repeat-profit","adversity_profit"));
 var h=e.State.Racers["a"].Effects["adversity_profit"];
 Check(h.Used==800 && h.Rank==1 && Balance.AdversityGoldCap(h.Rank)==1200);
 e.Execute(new("s3","a","battle_start","b3")); e.Execute(new("w3","a","victory","b3"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].Used==805 && Balance.AdversityGoldCap(2)==1600);
});
Test("shop cleansing is unavailable even with enough gold and ordinary effects", () => {
 var e=New(); Grant(e,"a","exhaustion"); var before=StateJson.Serialize(e.State); bool rejected=false;
 try { e.Execute(new("shop","a","shop_cleanse","shop","exhaustion",100000)); } catch(RuleException) { rejected=true; }
 Check(rejected && StateJson.Serialize(e.State)==before);
});
Test("adversity persists across act boss kills and saved legacy expiry without refreshing its cap", () => {
 var e=New(); OfferEffect(e,"profit","adversity_profit",Route.Grow);
 e.Execute(new("profit-pick","a","choose","profit","adversity_profit"));
 Check(e.State.Racers["a"].Effects["adversity_profit"].ExpiresAfterAct==-1 && Catalog.Get("adversity_profit").Permanent);
 e.State.Racers["a"].Effects["adversity_profit"].Used=75;
 e.State.Racers["a"].Effects["adversity_profit"].ExpiresAfterAct=0;
 e=new Engine(StateJson.Deserialize<Match>(StateJson.Serialize(e.State)));
 for(int act=0;act<3;act++) {
  e.State.Racers["a"].Act=act; e.Execute(new("boss-start"+act,"a","battle_start","boss"+act));
  e.Execute(new("boss-win"+act,"a","victory","boss"+act,Boss:true));
  Check(e.State.Racers["a"].Effects["adversity_profit"].Used==75);
 }
});
Test("war feeds war stacks through acquisition to three and never heals on sacrifice", () => {
 var e=New();
 void Pick(string key,string id) {
  e.State.Offers[key]=new(){Id=key,Player="a",RewardKey=key,Route=Route.Mixed,Candidates=[id]};
  e.Execute(new(key+"-pick","a","choose",key,id));
 }
 for(int i=0;i<3;i++) {
  Pick("war"+i,"war_feeds_war");
  Check(Balance.WarFeedsWarStacks(e.State.Racers["a"].Effects["war_feeds_war"])==i+1);
 }
 for(int i=0;i<4;i++) Pick("attack"+i,"exhaustion");
 Check(e.State.Racers["a"].Receipts.All(r=>r.Kind!="heal_percent"));
 foreach(var route in new[]{Route.Mixed,Route.Grow,Route.FirstKill})
  for(int i=0;i<30;i++) Check(!Candidates.Generate(e.State,e.State.Racers["a"],"capped-war"+i,route,false).Contains("war_feeds_war"));
 e.State.Offers["stale-war"]=new(){Id="stale-war",Player="a",RewardKey="stale-war",Route=Route.Mixed,Candidates=["war_feeds_war"]};
 string before=StateJson.Serialize(e.State); bool rejected=false;
 try{e.Execute(new("stale-war-pick","a","choose","stale-war","war_feeds_war"));}catch(RuleException){rejected=true;}
 Check(rejected && StateJson.Serialize(e.State)==before);
});
Test("tail refill costs 350 and reserves the spent native relic once", () => {
 var e=New(); Grant(e,"a","tail_refill"); var p=e.State.Racers["a"]; p.TailSpent=true; p.Gold=1000;
 var c=new Command("refill","a","tail_refill"); e.Execute(c); p=e.State.Racers["a"];
 Check(!p.TailSpent && p.Receipts.Single(r=>r.Kind=="spend_gold").Amount==350 && p.Receipts.Count(r=>r.Kind=="refill_tail")==1);
 Check(!e.Execute(c)); p.TailSpent=true; var before=StateJson.Serialize(e.State); bool rejected=false;
 try{e.Execute(new("double","a","tail_refill"));}catch(RuleException){rejected=true;}
 Check(rejected && before==StateJson.Serialize(e.State));
});
Test("tail refill invalid conditions preserve money and state", () => {
 foreach(var reason in new[]{"unspent","combat","poor","no-item"}) {
  var e=New(); Grant(e,"a","tail_refill"); var p=e.State.Racers["a"]; p.TailSpent=true; p.Gold=350;
  if(reason=="unspent") p.TailSpent=false; if(reason=="combat") p.InBattle=true; if(reason=="poor") p.Gold=349; if(reason=="no-item") p.Effects.Clear();
  var before=StateJson.Serialize(e.State); bool rejected=false;
  try{e.Execute(new("bad","a","tail_refill"));}catch(RuleException){rejected=true;}
  Check(rejected && before==StateJson.Serialize(e.State));
 }
});
Test("new combat scaling has explicit interval and penetration floors and caps", () => {
 Check(Balance.ChaosInterval(0)==4 && Balance.ChaosInterval(1)==3 && Balance.ChaosInterval(100)==1);
 Check(Balance.PierceRatio(0)==0.15m && Balance.PierceRatio(1)==0.20m && Balance.PierceRatio(100)==1);
 Check(Balance.ConductionRatio(0)==0.20m && Balance.ConductionRatio(1)==0.25m);
});
Test("queued saves coalesce while a disk write is blocked and dispose flushes latest state", () => {
 using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
 var saved=new Dictionary<string,string>(); int writes=0;
 var writer=new LatestFileWriter((path,contents)=>{
  if(Interlocked.Increment(ref writes)==1) { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
  saved[path]=contents;
 });
 try {
  writer.Enqueue("match","old"); Check(entered.Wait(TimeSpan.FromSeconds(10)));
  for(int i=0;i<1000;i++) writer.Enqueue("match",i.ToString());
  writer.Enqueue("checkpoint","native-backup");
 } finally { release.Set(); writer.Dispose(); }
 Check(saved["match"]=="999" && saved["checkpoint"]=="native-backup" && writes==3);
 bool rejected=false; try{writer.Enqueue("match","late");}catch(ObjectDisposedException){rejected=true;} Check(rejected);
});
Test("queued saves report IO failures and a newer write repairs the failed path", () => {
 int attempts=0; string? saved=null;
 using var writer=new LatestFileWriter((_,contents)=>{if(++attempts==1) throw new IOException("disk full"); saved=contents;});
 writer.Enqueue("match","first"); bool rejected=false; try{writer.Flush();}catch(AggregateException){rejected=true;}
 Check(rejected && writer.TakeErrors().Length==1 && writer.TakeErrors().Length==0);
 writer.Enqueue("match","recovered"); writer.Flush(); Check(saved=="recovered");
});
Test("atomic queued snapshots reach the real disk and leave no temporary files", () => {
 string folder=Path.Combine(Path.GetTempPath(),"fdzf-writer-"+Guid.NewGuid().ToString("N"));
 string match=Path.Combine(folder,"match.json"), checkpoint=Path.Combine(folder,"checkpoint.json");
 try {
  using(var writer=new LatestFileWriter()) {
   for(int i=0;i<200;i++) writer.Enqueue(match,StateJson.Serialize(new Match {Id="disk",Revision=i}));
   writer.Enqueue(checkpoint,"native-packet");
  }
  Check(StateJson.Deserialize<Match>(File.ReadAllText(match)).Revision==199 && File.ReadAllText(checkpoint)=="native-packet");
  Check(!File.Exists(match+".tmp") && !File.Exists(checkpoint+".tmp"));
 } finally {
  foreach(string path in new[]{match,checkpoint,match+".tmp",checkpoint+".tmp"}) if(File.Exists(path)) File.Delete(path);
  if(Directory.Exists(folder)) Directory.Delete(folder);
 }
});
Console.WriteLine($"{passed} checks passed");
