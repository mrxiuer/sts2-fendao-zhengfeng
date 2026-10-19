using FendaoZhengfeng.Core;

int passed = 0;
void Test(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); Environment.ExitCode = 1; } }
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
Engine New() => new(new Match { Id = "test", Seed = "seed", Racers = new() { ["a"] = new() { Id = "a" }, ["b"] = new() { Id = "b" }, ["c"] = new() { Id = "c" } } });
void Grant(Engine engine, string player, string id, int count = 1) { engine.State.Racers[player].Effects[id] = new() { Id = id, Grants = Enumerable.Range(0, count).Select(i => new Grant { EventId = i.ToString(), Source = "a", ActiveBattle = 1 }).ToList() }; }
Test("catalog has 29 unique effects", () => { Check(Catalog.All.Count == 29); Check(Catalog.All.Select(e => e.Id).Distinct().Count() == 29); });
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
Test("shop cleanses pending and active stacks together", () => { var e = New(); Grant(e, "b", "exhaustion", 2); e.State.Racers["b"].Effects["exhaustion"].Grants[0].ActiveBattle = 0; e.Execute(new("clean", "b", "shop_cleanse", "node", "exhaustion", 100)); Check(!e.State.Racers["b"].Effects.ContainsKey("exhaustion")); Check(e.State.Racers["b"].Receipts.Single(r => r.Kind == "spend_gold").Amount == 70); });
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
Console.WriteLine($"{passed} checks passed");
