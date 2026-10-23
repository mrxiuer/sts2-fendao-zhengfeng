namespace FendaoZhengfeng.Core;

public static class MatchCompatibility
{
    // design.14 adds catalog entries and acquisition caps without changing the
    // persisted shape. Keep an existing match's whitelist and resolved history.
    public static Match Read(string envelopeVersion, string json)
    {
        if (envelopeVersion != Catalog.RulesVersion && envelopeVersion != "1.0.0-design.13")
            throw new InvalidDataException("比赛存档规则版本不兼容。");
        var match = StateJson.Deserialize<Match>(json);
        if (match.Version != envelopeVersion) throw new InvalidDataException("比赛存档内外版本不一致。");
        if (match.EnabledEffects.Any(id => !Catalog.IsKnown(id)) || match.Racers.Values.Any(r => r.Effects.Keys.Any(id => !Catalog.IsKnown(id)))
            || match.Offers.Values.Any(o => o.Candidates.Any(id => !Catalog.IsKnown(id))))
            throw new InvalidDataException("比赛存档包含未知效果。");
        match.Version = Catalog.RulesVersion;
        return match;
    }
}
