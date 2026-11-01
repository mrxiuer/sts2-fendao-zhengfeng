using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace FendaoZhengfeng.Mod;

internal static class SharedBossRewards
{
    internal static async Task<bool> Select(Player owner)
    {
        var session = RaceSession.Current!;
        string? prize = RaceRuntime.RacerFor(owner)?.BossPrizes.FirstOrDefault();
        if (prize == null) return true;
        if (!await ChooseEffect(owner, prize, Route.FirstKill, true)) return false;
        await RaceModifier.ForPlayer(owner)!.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
        RaceUi.Refresh();
        return true;
    }

    internal static async Task<bool> ChooseEffect(Player owner, string rewardKey, Route route, bool boss)
    {
        var session = RaceSession.Current!;
        string ownerId = RaceRuntime.RacerFor(owner)!.Id;
        await SharedChoice.Commit(owner, "offer", rewardKey, boss: boss, route: route);
        while (ReferenceEquals(session, RaceSession.Current))
        {
            var offer = session.Engine.State.Offers[ownerId + ":" + rewardKey + ":" + route];
            var candidates = offer.Candidates.ToArray();
            var usage = RewardRerollRules.OfferUsage(session.Engine.State.Racers[ownerId], offer.Id);
            var labels = candidates.Select(id => { var effect = Catalog.Get(id); return L10n.T(effect.Name) + "\n" + RaceUi.TierName(effect.Tier) + "\n" + L10n.T(effect.Description); }).ToList();
            var icons = candidates.Select(EffectIcons.Get).ToList();
            if (usage.CanRefresh) { labels.Add(L10n.F($"刷新候选 · 剩余{usage.Remaining}次")); icons.Add(EffectIcons.Get("encore_rewards")); }
            int selected = await SharedChoice.Choose(owner, labels.Count, () => RaceUi.Choose(L10n.T("选择效果"), labels, icons));
            if (selected < 0) return false;
            if (selected == candidates.Length && usage.CanRefresh)
            {
                await SharedChoice.Commit(owner, "offer_reroll", rewardKey, route: route, generation: usage.Used);
                continue;
            }
            await SharedChoice.Commit(owner, "choose", offer.Id, candidates[selected], route: route, generation: usage.Used);
            await RaceModifier.ForPlayer(owner)!.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
            return true;
        }
        return false;
    }
}
