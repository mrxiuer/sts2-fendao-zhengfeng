namespace FendaoZhengfeng.Core;

public static class MatchCompatibility
{
    // design.15 keeps unbounded boons for the entire run. Preserve existing
    // holdings and history while clearing obsolete act expiry markers.
    public static Match Read(string envelopeVersion, string json)
    {
        if (envelopeVersion != Catalog.RulesVersion && envelopeVersion != "1.0.0-design.13" && envelopeVersion != "1.0.0-design.14")
            throw new InvalidDataException("比赛存档规则版本不兼容。");
        var match = StateJson.Deserialize<Match>(json);
        if (match.Version != envelopeVersion) throw new InvalidDataException("比赛存档内外版本不一致。");
        if (match.EnabledEffects.Any(id => !Catalog.IsKnown(id)) || match.Racers.Values.Any(r => r.Effects.Keys.Any(id => !Catalog.IsKnown(id)))
            || match.Offers.Values.Any(o => o.Candidates.Any(id => !Catalog.IsKnown(id))))
            throw new InvalidDataException("比赛存档包含未知效果。");
        foreach (var racer in match.Racers.Values)
            foreach (var holding in racer.Effects.Values)
                if (Catalog.Get(holding.Id).Permanent) holding.ExpiresAfterAct = -1;
        match.Version = Catalog.RulesVersion;
        return match;
    }
}
