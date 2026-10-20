using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace FendaoZhengfeng.Mod;

/// <summary>Copy donor data into local temporary combat cards. Never mutates the donor or either permanent deck.</summary>
internal sealed class HeartEchoDeck
{
    private readonly Dictionary<PileType, List<CardModel>> original = [];
    private readonly HashSet<CardModel> copies = [];
    private static readonly PileType[] piles = [PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play];
    internal bool Active => original.Count > 0;
    internal string DonorName { get; private set; } = "";
    internal async Task CopyOpponent(TowerHeart heart)
    {
        if (Active || RaceSession.Current is not { } session || RaceRuntime.State is not { } run) return;
        var donors = session.Engine.State.Racers.Values.Where(r => r.Id != session.LocalId && !r.Eliminated && !r.Finished && !r.ResetPending && r.PublicPacket.Length > 0)
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        if (donors.Count == 0) { RaceUi.Notify("众生回响：没有可复制的对手牌组，本次跳过。"); return; }
        var rng = new StableRandom(run.Rng.Seed + ":heart-copy:" + session.LocalId + ":" + session.Me.ResetCount);
        var donor = donors[(int)(rng.Next() * donors.Count)];
        var source = PublicProfiles.Read(donor);
        if (source == null || source.Deck.Cards.Count == 0) { RaceUi.Notify("众生回响：对手牌组为空，本次跳过。"); return; }
        // Decode into this run BEFORE touching the local combat piles. The source is read-only.
        var player = run.Players.Single();
        var templates = source.Deck.Cards.Select(card => NativeCheckpoint.DecodeCard(NativeCheckpoint.EncodeCard(card), run, player)).ToList();
        var deck = templates.Select(card => player.Creature.CombatState!.CloneCard(card)).ToList();
        foreach (var template in templates) template.RemoveFromState();
        for (int i = deck.Count - 1; i > 0; i--) { int j = (int)(rng.Next() * (i + 1)); (deck[i], deck[j]) = (deck[j], deck[i]); }
        await PowerCmd.Apply<HeartEchoPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), player.Creature, 2, heart.Creature, null);
        if (!player.Creature.HasPower<HeartEchoPower>()) { RaceUi.Notify("众生回响被人工制品抵挡。"); foreach (var card in deck) card.RemoveFromState(); return; }
        foreach (var type in piles) original[type] = type.GetPile(player).Cards.ToList();
        // Detach only from visible piles, never from CombatState. Restore these exact instances
        // without treating them as newly entered cards or retriggering their opening hooks.
        RemoveHandVisuals(player, original[PileType.Hand]);
        foreach (var card in original.Values.SelectMany(cards => cards)) card.RemoveFromCurrentPile();
        foreach (var card in deck) { card.DeckVersion = null; copies.Add(card); await CardPileCmd.Add(card, PileType.Draw, skipVisuals: true); }
        DonorName = donor.Name;
        HeartPresentation.CopyNotice(DonorName);
    }
    internal async Task Restore(Player player)
    {
        if (!Active) return;
        var current = piles.SelectMany(type => type.GetPile(player).Cards).ToList();
        // Newly inflicted statuses survive; the old statuses are already part of their original piles.
        var statuses = piles.ToDictionary(type => type, type => type.GetPile(player).Cards.Where(card => !copies.Contains(card) && card.Type is CardType.Status or CardType.Curse).ToList());
        var carried = statuses.Values.SelectMany(cards => cards).ToHashSet();
        RemoveHandVisuals(player, statuses[PileType.Hand]);
        foreach (var card in carried) card.RemoveFromCurrentPile();
        await CardPileCmd.RemoveFromCombat(current.Where(card => !carried.Contains(card)).ToList());
        foreach (var type in piles)
        {
            foreach (var card in original[type]) type.GetPile(player).AddInternal(card);
            foreach (var card in statuses[type]) type.GetPile(player).AddInternal(card);
        }
        var restoredHand = original[PileType.Hand].Concat(statuses[PileType.Hand]).ToList();
        if (NCombatRoom.Instance is { } room && restoredHand.Count > 0 && !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsEnding)
        {
            var visualResults = restoredHand.Select(card => new MegaCrit.Sts2.Core.Entities.Cards.CardPileAddResult { cardAdded = card, targetPile = PileType.Hand, success = true }).ToList();
            var (tween, _) = CardPileCmd.GetTweenForCardsChangingPiles(visualResults, false);
            if (tween != null) { tween.Play(); await room.ToSignal(tween, Godot.Tween.SignalName.Finished); }
        }
        original.Clear(); copies.Clear(); DonorName = "";
        await PowerCmd.Remove<HeartEchoPower>(player.Creature);
        RaceUi.Notify("众生回响结束：已恢复自己的战斗牌组。");
    }
    private static void RemoveHandVisuals(Player player, IEnumerable<CardModel> cards)
    {
        if (NCombatRoom.Instance?.Ui.Hand is not { } hand) return;
        foreach (var card in cards)
            if (hand.GetCard(card) is { } visual) { hand.Remove(card); visual.QueueFree(); }
    }
    internal async Task Cleanup()
    {
        if (Active && RaceRuntime.State?.Players.SingleOrDefault() is { } player && player.Creature.CombatState != null) await Restore(player);
        original.Clear(); copies.Clear(); DonorName = "";
    }
}
