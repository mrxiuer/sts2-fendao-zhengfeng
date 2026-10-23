using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// A permanent, once-per-draw-cycle skill granted by the Extra Timeline boon.
/// It only reads public opponent snapshots and creates combat-scoped copies.
/// </summary>
public sealed class ExtraTimelineCard : CardModel
{
    public ExtraTimelineCard() : base(2, CardType.Skill, CardRarity.Rare, TargetType.None) { }

    [SavedProperty] public string GrantReceiptId { get; set; } = "";

    internal bool IsTimelineCopy { get; set; }

    public override string Title => "额外时间线";
    public override CardPoolModel Pool => ModelDb.Card<Dazed>().Pool;
    public override int MaxUpgradeLevel => 0;

    protected override Task OnPlay(PlayerChoiceContext ctx, CardPlay play)
    {
        // A copied copy must not recurse forever when an opponent also owns this skill.
        if (IsTimelineCopy) return Task.CompletedTask;
        return TimelineEffects.PlayFromOpponent(ctx, Owner);
    }
}
