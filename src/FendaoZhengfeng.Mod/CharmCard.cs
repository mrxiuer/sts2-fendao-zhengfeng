using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed class CharmCard : CardModel
{
    public CharmCard() : base(2, CardType.Skill, CardRarity.Uncommon, TargetType.AnyEnemy) { }

    [SavedProperty] public string GrantReceiptId { get; set; } = "";

    public override string Title => CharmStrings.Text("card.title").GetFormattedText();
    public override CardPoolModel Pool => ModelDb.Card<Dazed>().Pool;
    public override int MaxUpgradeLevel => 0;
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];

    protected override async Task OnPlay(PlayerChoiceContext context, CardPlay play)
    {
        if (play.Target is { IsAlive: true, IsEnemy: true } target)
            await ApplyTo(context, target);
    }

    internal async Task<CharmRedirectPower?> ApplyTo(PlayerChoiceContext context, Creature target)
    {
        if (Owner is not { } player || !target.IsAlive || !target.IsEnemy || target.CombatState != player.Creature.CombatState)
            return null;

        CharmRedirectPower? power = await CharmPowerCmd.Apply<CharmRedirectPower>(context, target, 2, player.Creature, this);
        if (power != null && target.GetCreatureNode() is { } node)
            await node.UpdateIntent(CharmTargeting.Recipients(target, group: true));
        return power;
    }
}

internal static class CharmCardGrant
{
    // One stable receipt creates one permanent card. Replaying the same receipt is harmless.
    internal static async Task<bool> AddToDeck(RunState run, Player player, string receiptId)
    {
        if (string.IsNullOrWhiteSpace(receiptId))
            throw new ArgumentException(L10n.T("牵心丝卡牌收据缺少稳定标识。"), nameof(receiptId));

        if (player.Deck.Cards.OfType<CharmCard>().Any(card => card.GrantReceiptId == receiptId))
            return false;

        var card = run.CreateCard<CharmCard>(player);
        card.GrantReceiptId = receiptId;
        try
        {
            await CardPileCmd.Add(card, PileType.Deck, skipVisuals: true);
        }
        catch
        {
            if (!player.Deck.Cards.Contains(card))
                card.RemoveFromState();
            throw;
        }

        if (!player.Deck.Cards.Contains(card))
        {
            card.RemoveFromState();
            throw new InvalidOperationException(L10n.T("魅惑没有加入永久牌组，收据应保留待重试。"));
        }
        return true;
    }
}
