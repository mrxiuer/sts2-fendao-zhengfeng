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
    private readonly Dictionary<Player, HeartEchoDeck> perPlayer = [];
    private Player? originalOwner;
    private readonly Dictionary<PileType, List<CardModel>> original = [];
    private readonly HashSet<CardModel> copies = [];
    private static readonly PileType[] piles = [PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play];
    internal bool Active => original.Count > 0 || perPlayer.Values.Any(deck => deck.Active);
    internal string DonorName { get; private set; } = "";
    internal string DonorNameFor(Player? player) => player != null && perPlayer.TryGetValue(player, out var child) ? child.DonorName : DonorName;
    internal async Task CopyOpponent(TowerHeart heart)
    {
        if (Active || RaceSession.Current is not { } session || RaceRuntime.State is not { } run) return;
        if (!session.Engine.State.Synchronous)
        {
            if (RaceRuntime.LocalPlayer(run) is { } player) await CopyFor(heart, player);
            return;
        }
        foreach (var player in run.Players.OrderBy(player => player.NetId).Where(player => player.Creature.IsAlive))
        {
            if (!perPlayer.TryGetValue(player, out var child)) perPlayer[player] = child = new();
            await child.CopyFor(heart, player);
        }
    }
    private async Task CopyFor(TowerHeart heart, Player player)
    {
        if (Active || RaceSession.Current is not { } session || RaceRuntime.State is not { } run) return;
        var racer = RaceRuntime.RacerFor(player)!;
        var rng = new StableRandom(run.Rng.Seed + ":heart-copy:" + racer.Id + ":" + racer.ResetCount);
        IReadOnlyList<CardModel> sourceCards;
        string sourceName;
        if (session.Engine.State.Synchronous)
        {
            // Shared combat already contains each real permanent deck. Read it directly so telemetry
            // arrival order cannot alter the copied cards across peers.
            var donors = run.Players.Where(other => other != player && other.Creature.IsAlive && other.Deck.Cards.Count > 0)
                .OrderBy(other => other.NetId).ToArray();
            if (donors.Length == 0) return;
            var donor = donors[(int)(rng.Next() * donors.Length)];
            sourceCards = donor.Deck.Cards.ToArray();
            sourceName = L10n.RacerName(RaceRuntime.RacerFor(donor)!);
        }
        else
        {
            var donors = session.Engine.State.Racers.Values.Where(other => other.Id != racer.Id && !other.Eliminated && !other.Finished
                    && !other.ResetPending && other.PublicPacket.Length > 0).OrderBy(other => other.Id, StringComparer.Ordinal).ToArray();
            if (donors.Length == 0) { RaceUi.Notify(L10n.T("众生回响：没有可复制的对手牌组，本次跳过。")); return; }
            var donor = donors[(int)(rng.Next() * donors.Length)];
            var source = PublicProfiles.Read(donor);
            if (source == null || source.Deck.Cards.Count == 0) { RaceUi.Notify(L10n.T("众生回响：对手牌组为空，本次跳过。")); return; }
            sourceCards = source.Deck.Cards.ToArray();
            sourceName = L10n.RacerName(donor);
        }
        // Decode before touching any piles, retaining upgrades and supported saved card properties.
        var templates = sourceCards.Select(card => NativeCheckpoint.DecodeCard(NativeCheckpoint.EncodeCard(card), run, player)).ToList();
        var deck = templates.Select(card => player.Creature.CombatState!.CloneCard(card)).ToList();
        foreach (var template in templates) template.RemoveFromState();
        for (int i = deck.Count - 1; i > 0; i--) { int j = (int)(rng.Next() * (i + 1)); (deck[i], deck[j]) = (deck[j], deck[i]); }
        await PowerCmd.Apply<HeartEchoPower>(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), player.Creature, 2, heart.Creature, null);
        if (!player.Creature.HasPower<HeartEchoPower>()) { RaceUi.Notify(L10n.T("众生回响被人工制品抵挡。")); foreach (var card in deck) card.RemoveFromState(); return; }
        originalOwner = player;
        foreach (var type in piles) original[type] = type.GetPile(player).Cards.ToList();
        // Detach only from visible piles, never from CombatState. Restore these exact instances
        // without treating them as newly entered cards or retriggering their opening hooks.
        RemoveHandVisuals(player, original[PileType.Hand]);
        foreach (var card in original.Values.SelectMany(cards => cards)) card.RemoveFromCurrentPile();
        foreach (var card in deck) { card.DeckVersion = null; copies.Add(card); await CardPileCmd.Add(card, PileType.Draw, skipVisuals: true); }
        DonorName = sourceName;
        if (player == RaceRuntime.LocalPlayer()) HeartPresentation.CopyNotice(DonorName);
    }
    internal async Task Restore(Player player)
    {
        if (perPlayer.TryGetValue(player, out var child)) { await child.Restore(player); return; }
        if (original.Count == 0 || player != originalOwner) return;
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
        if (player == RaceRuntime.LocalPlayer() && NCombatRoom.Instance is { } room && restoredHand.Count > 0 && !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsEnding)
        {
            var visualResults = restoredHand.Select(card => new MegaCrit.Sts2.Core.Entities.Cards.CardPileAddResult { cardAdded = card, targetPile = PileType.Hand, success = true }).ToList();
            var (tween, _) = CardPileCmd.GetTweenForCardsChangingPiles(visualResults, false);
            if (tween != null) { tween.Play(); await room.ToSignal(tween, Godot.Tween.SignalName.Finished); }
        }
        original.Clear(); copies.Clear(); originalOwner = null; DonorName = "";
        await PowerCmd.Remove<HeartEchoPower>(player.Creature);
        if (player == RaceRuntime.LocalPlayer()) RaceUi.Notify(L10n.T("众生回响结束：已恢复自己的战斗牌组。"));
    }
    private static void RemoveHandVisuals(Player player, IEnumerable<CardModel> cards)
    {
        if (player != RaceRuntime.LocalPlayer() || NCombatRoom.Instance?.Ui.Hand is not { } hand) return;
        foreach (var card in cards)
            if (hand.GetCard(card) is { } visual) { hand.Remove(card); visual.QueueFree(); }
    }
    internal async Task Cleanup()
    {
        foreach (var entry in perPlayer.ToArray()) await entry.Value.Restore(entry.Key);
        perPlayer.Clear();
        if (originalOwner is { } player && player.Creature.CombatState != null) await Restore(player);
        original.Clear(); copies.Clear(); originalOwner = null; DonorName = "";
    }
}
