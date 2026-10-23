using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed class ClutterCard : CardModel
{
    public ClutterCard() : base(0, CardType.Status, CardRarity.Status, TargetType.None) { }
    public override string Title => L10n.T("杂念");
    public override CardPoolModel Pool => ModelDb.Card<Dazed>().Pool;
    public override string PortraitPath => ModelDb.Card<Dazed>().PortraitPath;
    public override int MaxUpgradeLevel => 0;
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Unplayable, CardKeyword.Ethereal];
    protected override Task OnPlay(PlayerChoiceContext ctx, CardPlay play) => Task.CompletedTask;
}
public sealed class WitheredCard : CardModel
{
    public WitheredCard() : base(1, CardType.Status, CardRarity.Status, TargetType.None) { }
    [SavedProperty] public string OriginalPacket { get; set; } = "";
    public override string Title => L10n.T("枯萎的卡牌");
    public override CardPoolModel Pool => ModelDb.Card<Slimed>().Pool;
    public override string PortraitPath => ModelDb.Card<Slimed>().PortraitPath;
    public override int MaxUpgradeLevel => 0;
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];
    protected override async Task OnPlay(PlayerChoiceContext ctx, CardPlay play) => await CardPileCmd.Draw(ctx, 1, Owner);
}
