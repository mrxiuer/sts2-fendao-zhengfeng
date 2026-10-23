namespace FendaoZhengfeng.Core;

/// <summary>A frozen public-deck view of one opponent, captured from the match reducer.</summary>
public sealed record TimelineOpponentSnapshot(string Id, string Name, IReadOnlyList<MarketCard> Cards);

public static class TimelineRules
{
    /// <summary>
    /// Returns every other racer with a non-empty real public card packet. Does not require the
    /// racer to be online or active; the last reducer snapshot remains usable after disconnect.
    /// The returned card list is copied so later profile updates cannot change an open picker.
    /// </summary>
    public static IReadOnlyList<TimelineOpponentSnapshot> OpponentSnapshots(Match match, string localPlayerId)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPlayerId);

        return match.Racers.Values
            .Where(racer => !string.Equals(racer.Id, localPlayerId, StringComparison.Ordinal))
            .Select(racer => new TimelineOpponentSnapshot(
                racer.Id,
                racer.Name,
                racer.MarketCards.Where(card => !string.IsNullOrWhiteSpace(card.Packet)).ToArray()))
            .Where(snapshot => snapshot.Cards.Count > 0)
            .OrderBy(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
