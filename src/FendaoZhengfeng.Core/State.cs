using System.Text.Json;
using System.Text.Json.Serialization;

namespace FendaoZhengfeng.Core;

public sealed class Grant
{
    public string EventId { get; set; } = "";
    public string Source { get; set; } = "";
    public long ActiveBattle { get; set; }
}
public sealed class Holding
{
    public string Id { get; set; } = "";
    public List<Grant> Grants { get; set; } = [];
    public int Rank { get; set; }
    public int ExpiresAfterAct { get; set; } = -1;
    public int Used { get; set; }
    public Dictionary<string, int> Counters { get; set; } = [];
}
public sealed record RouteStep(int Act, int Row, int Col);
public sealed record MarketCard(string Id, string Packet, string Name, int Wear = -1);
public sealed record PublicProfile(string Packet, int Health, int MaxHealth, int Act, int Floor, string Room, RouteStep? Position,
    int Gold = 0, List<MarketCard>? Cards = null);
public sealed class CardTrade
{
    public string Id { get; set; } = "";
    public string Seller { get; set; } = "";
    public string Buyer { get; set; } = "";
    public MarketCard Card { get; set; } = new("", "", "");
    public int Price { get; set; }
    public long SellerBattle { get; set; }
    public long BuyerBattle { get; set; }
    public bool Removed { get; set; }
    public bool Delivered { get; set; }
}
public sealed class Racer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicPacket { get; set; } = "";
    public long ProfileRevision { get; set; }
    public RouteStep? Position { get; set; }
    public List<RouteStep> Trail { get; set; } = [];
    public HashSet<RouteStep> ClearedNodes { get; set; } = [];
    public List<MarketCard> MarketCards { get; set; } = [];
    public int Gold { get; set; }
    public int Purchases { get; set; }
    public int ResetCount { get; set; }
    public int ResetCharges { get; set; }
    public bool ResetPending { get; set; }
    public int Act { get; set; }
    public int Floor { get; set; }
    public string Room { get; set; } = "";
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public bool Eliminated { get; set; }
    public bool Finished { get; set; }
    public int VictoryCount { get; set; }
    public int RevivalCount { get; set; }
    public long Battle { get; set; }
    public string BattleKey { get; set; } = "";
    public bool InBattle { get; set; }
    public int BurdenSnapshot { get; set; }
    public int Shields { get; set; }
    public int Ghosts { get; set; }
    public int PendingEnergy { get; set; }
    public List<string> PendingStatuses { get; set; } = [];
    public List<string> PendingCurseCards { get; set; } = [];
    public Dictionary<string, Holding> Effects { get; set; } = [];
    public Dictionary<string, int> History { get; set; } = [];
    public HashSet<string> WonBattles { get; set; } = [];
    public HashSet<int> KilledBossActs { get; set; } = [];
    public HashSet<string> UsedServices { get; set; } = [];
    public Dictionary<string, int> FortuneIncome { get; set; } = [];
    public List<string> BossPrizes { get; set; } = [];
    public List<Receipt> Receipts { get; set; } = [];
    public int Count(string id, bool activeOnly = true) => Effects.TryGetValue(id, out var h)
        ? (Catalog.Get(id).Kind == EffectKind.Boon ? 1 : h.Grants.Count(g => !activeOnly || g.ActiveBattle <= Battle)) : 0;
    public bool Has(string id) => Count(id) > 0;
    [JsonIgnore]
    public int Burden => Effects.Values.Where(h => Catalog.Get(h.Id).Kind == EffectKind.Curse)
        .Sum(h => h.Grants.Count(g => g.ActiveBattle <= Battle) * Catalog.Get(h.Id).Burden);
    public double Growth => (Has("battle_hardened") ? VictoryCount * (0.01 + 0.001 * Effects["battle_hardened"].Rank) : 0)
        + (Has("death_defiance") ? RevivalCount * (0.1 + 0.01 * Effects["death_defiance"].Rank) : 0);
}
public sealed record Receipt(string Id, string Kind, string Value = "", int Amount = 0);
public sealed class Offer
{
    public string Id { get; set; } = "";
    public string Player { get; set; } = "";
    public string RewardKey { get; set; } = "";
    public Route Route { get; set; }
    public bool Boss { get; set; }
    public List<string> Candidates { get; set; } = [];
    public bool Resolved { get; set; }
}
public sealed class Match
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = Catalog.RulesVersion;
    public string Seed { get; set; } = "";
    public HashSet<string> EnabledEffects { get; set; } = Catalog.All.Select(effect => effect.Id).ToHashSet();
    public bool DebugMode { get; set; }
    public long Revision { get; set; }
    public Dictionary<string, Racer> Racers { get; set; } = [];
    public Dictionary<string, Offer> Offers { get; set; } = [];
    public HashSet<string> ConsumedRewards { get; set; } = [];
    public Dictionary<int, string> BossFirstKills { get; set; } = [];
    public List<string> FinishOrder { get; set; } = [];
    public List<CardTrade> Trades { get; set; } = [];
    public HashSet<string> ProcessedCommands { get; set; } = [];
}

public static class StateJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = false, MaxDepth = 64, Converters = { new JsonStringEnumConverter() } };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new RuleException("存档为空。");
}
