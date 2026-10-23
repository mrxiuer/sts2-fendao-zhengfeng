using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// Native reward adapter for Encore Rewards. Engine commits an idempotent
/// native-reroll receipt; the transaction layer applies and acknowledges it.
/// </summary>
internal static class RewardRerollEffect
{
    private const string ButtonName = "EncoreRewardReroll";
    private static readonly System.Reflection.FieldInfo RelicValue = AccessTools.Field(typeof(RelicReward), "_relic")!;
    private static readonly System.Reflection.FieldInfo RelicPredetermined = AccessTools.Field(typeof(RelicReward), "_predeterminedRelic")!;
    private static readonly System.Reflection.FieldInfo SpecialCard = AccessTools.Field(typeof(SpecialCardReward), "_card")!;
    private static readonly System.Reflection.FieldInfo PotionIcon = AccessTools.Field(typeof(PotionReward), "_icon")!;
    private static readonly System.Reflection.FieldInfo BaseRng = AccessTools.Field(typeof(Reward), "_rngOverride")!;
    private static readonly System.Reflection.MethodInfo ReloadButton = AccessTools.Method(typeof(NRewardButton), "Reload")!;
    private static readonly System.Reflection.MethodInfo CardReroll = AccessTools.Method(typeof(CardReward), nameof(CardReward.Reroll))!;
    private static readonly System.Reflection.MethodInfo? PotionSetter = AccessTools.PropertySetter(typeof(PotionReward), nameof(PotionReward.Potion));

    internal static bool IsTrackedReward(RewardsSet set, Reward reward)
    {
        bool Contains(IEnumerable<Reward> rewards) => rewards.Any(item => ReferenceEquals(item, reward)
            || item is LinkedRewardSet linked && Contains(linked.Rewards));
        return Contains(set.Rewards);
    }

    internal static bool IsSupported(Reward reward)
    {
        if (!reward.IsPopulated || reward.SuccessfullySelected) return false;
        if (reward is GoldReward) return false;
        if (reward is PotionReward potion)
            return potion.Potion != null;
        if (reward is RelicReward relic)
            return relic.Relic != null;
        if (reward is CardReward cards)
        {
            var options = (CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions")!.Invoke(cards, null)!;
            return options.CardPools.Count > 0 && options.GetPossibleCards(cards.Player).Any();
        }
        if (reward is SpecialCardReward special)
            return SpecialCardCandidates(special).Any();
        return false;
    }

    internal static bool IsInFlight(RaceSession session, string key)
        => RewardRerollTransaction.IsInFlight(session, key);

    private static string Tooltip(Reward reward)
        => L10n.T("按原奖励来源重新生成内容；次数按每份奖励单独保存。");

    internal static void AttachButton(NRewardButton row, Reward reward, RewardsSet set)
    {
        if (!IsSupported(reward) || row.GetNodeOrNull<Button>(ButtonName) != null) return;
        var button = new Button
        {
            Name = ButtonName,
            Icon = EffectIcons.Get("encore_rewards"),
            ExpandIcon = true,
            TooltipText = Tooltip(reward),
            CustomMinimumSize = new Vector2(54, 54),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 10
        };
        button.AnchorLeft = button.AnchorRight = 1;
        button.AnchorTop = button.AnchorBottom = 0.5f;
        button.OffsetLeft = -66;
        button.OffsetRight = -10;
        button.OffsetTop = -27;
        button.OffsetBottom = 27;
        StyleBoxFlat Style(Color fill, Color border) => new() { BgColor = fill, BorderColor = border,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 5, ContentMarginRight = 5, ContentMarginTop = 3, ContentMarginBottom = 5 };
        button.AddThemeStyleboxOverride("normal", Style(new Color("173b42"), new Color("b29a65")));
        button.AddThemeStyleboxOverride("hover", Style(new Color("28616a"), new Color("eed19a")));
        button.AddThemeStyleboxOverride("pressed", Style(new Color("102f36"), new Color("d7bd83")));
        button.AddThemeStyleboxOverride("disabled", Style(new Color("24383d"), new Color("536467")));
        row.AddChild(button);
        var countLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Modulate = new Color("ffe5aa"), Text = "0/1" };
        countLabel.AddThemeFontSizeOverride("font_size", 14);
        countLabel.AddThemeConstantOverride("outline_size", 3);
        button.AddChild(countLabel); countLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); countLabel.OffsetRight = -3;
        void ReserveLabelSpace()
        {
            if (!GodotObject.IsInstanceValid(row)) return;
            if (row.GetNodeOrNull<Control>("%Label") is not { } label) return;
            label.Size = new Vector2(Math.Max(40, row.Size.X - label.Position.X - 76), label.Size.Y);
            if (label is RichTextLabel text) text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
        row.Resized += ReserveLabelSpace;
        row.TreeEntered += () => Callable.From(ReserveLabelSpace).CallDeferred();
        ReserveLabelSpace();

        bool busy = false;
        void UpdateText()
        {
            if (!GodotObject.IsInstanceValid(button) || RaceSession.Current is not { } session) return;
            string key = RaceRuntime.RewardKey(reward);
            var usage = RewardRerollRules.NativeUsage(session.Me, key);
            countLabel.Text = $"{usage.Remaining}/{usage.Limit}";
            button.TooltipText = L10n.F($"再来一次 · 剩余{usage.Remaining}/{usage.Limit}次\n") + Tooltip(reward);
            button.Modulate = usage.CanRefresh ? Colors.White : new Color(0.65f, 0.7f, 0.7f);
            button.Disabled = busy || !usage.CanRefresh || !IsSupported(reward)
                || IsInFlight(session, key)
                || session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + key);
        }
        button.Pressed += () =>
        {
            if (busy) return;
            busy = true;
            UpdateText();
            RaceUi.Run(async () =>
            {
                try { await RefreshAsync(row, reward, set); }
                finally { busy = false; UpdateText(); }
            });
        };
        UpdateText();
    }

    internal static async Task RefreshAsync(NRewardButton? row, Reward reward, RewardsSet set)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException(L10n.T("比赛会话已结束。"));
        if (!RaceRuntime.Live || !RewardRerollPersistence.IsCurrentSet(set) || !IsTrackedReward(set, reward)
            || !IsSupported(reward)) throw new RuleException(L10n.T("这份奖励当前不能刷新。"));

        string key = RaceRuntime.RewardKey(reward);
        if (!RewardRerollRules.CanRefreshNative(session.Engine.State, session.Me, key))
            throw new RuleException(L10n.T("这份奖励没有可用的刷新次数。"));

        using var transaction = RewardRerollTransaction.Begin(session, key);
        var command = session.New(RewardRerollRules.NativeCommand, key);
        await session.Submit(command);
        var receipt = RewardRerollTransaction.FindCommittedReceipt(session, command.Id + ":reroll", key)
            ?? throw new InvalidDataException(L10n.T("奖励刷新已提交，但未收到对应的原生刷新收据；请恢复后重试该奖励。"));

        // No await may separate native mutation and its recovery marker. A save
        // between them must either replay the receipt or carry the applied token.
        RewardRerollTransaction.ApplyAndMark(session, reward, receipt);
        if (row != null) Reload(row, reward);
        RewardRerollPersistence.CheckpointSet(set);
        await RewardRerollTransaction.AcknowledgeAsync(session, receipt, set);
    }

    /// <summary>Mutates a populated native reward using its own original generation path.</summary>
    internal static void ApplyNativeReroll(Reward reward)
    {
        switch (reward)
        {
            case GoldReward:
                throw new RuleException(L10n.T("金币奖励不能刷新。"));
            case PotionReward potion:
            {
                var replacement = new PotionReward(potion.Player);
                if (BaseRng.GetValue(potion) is Rng rng) replacement.SetRng(rng);
                replacement.Populate();
                if (replacement.Potion == null || PotionSetter == null) throw new InvalidOperationException(L10n.T("原生药水奖励生成失败。"));
                PotionSetter.Invoke(potion, [replacement.Potion]);
                PotionIcon.SetValue(potion, null);
                break;
            }
            case RelicReward relic:
            {
                var old = relic.Relic ?? throw new InvalidOperationException(L10n.T("遗物奖励尚未生成。"));
                var rng = BaseRng.GetValue(relic) as Rng;
                bool predetermined = RelicPredetermined.GetValue(relic) != null;
                var rarity = predetermined
                    ? old.Rarity != RelicRarity.None ? old.Rarity : RelicFactory.RollRarity(rng ?? relic.Player.PlayerRng.Rewards)
                    : relic.Rarity != RelicRarity.None ? relic.Rarity
                    : RelicFactory.RollRarity(rng ?? relic.Player.PlayerRng.Rewards);
                // Pull from the player's real run grab bag and shared bag, using
                // the native rarity and excluding the current option when possible.
                RelicModel replacement;
                try { replacement = RelicFactory.PullNextRelicFromFront(relic.Player, rarity, candidate => candidate.Id != old.Id).ToMutable(); }
                catch (InvalidOperationException)
                {
                    try { replacement = RelicFactory.PullNextRelicFromFront(relic.Player, rarity).ToMutable(); }
                    catch (InvalidOperationException) { replacement = RelicFactory.PullNextRelicFromFront(relic.Player).ToMutable(); }
                }
                RelicValue.SetValue(relic, replacement);
                if (predetermined) RelicPredetermined.SetValue(relic, replacement);
                break;
            }
            case CardReward cards:
                CardReroll.Invoke(cards, null);
                break;
            case SpecialCardReward special:
            {
                var old = (CardModel)SpecialCard.GetValue(special)!;
                var options = SpecialCardOptions(special, old);
                var replacement = CardFactory.CreateForReward(special.Player, 1, options).First().Card;
                special.Player.RunState.CurrentMapPointHistoryEntry?.GetEntry(special.Player.NetId)
                    .CardChoices.Add(new CardChoiceHistoryEntry(old, wasPicked: false));
                SpecialCard.SetValue(special, replacement);
                break;
            }
            default:
                throw new RuleException(L10n.T("这类奖励没有可复用的原生刷新规则。"));
        }
    }

    private static IEnumerable<CardModel> SpecialCardCandidates(SpecialCardReward reward)
    {
        var current = (CardModel)SpecialCard.GetValue(reward)!;
        return SpecialCardOptions(reward, current).GetPossibleCards(reward.Player);
    }

    private static CardCreationOptions SpecialCardOptions(SpecialCardReward reward, CardModel current)
    {
        var player = reward.Player;
        var pools = ModelDb.AllCardPools.Where(pool => pool.GetUnlockedCards(player.UnlockState,
                player.RunState.CardMultiplayerConstraint).Any(card => card.Id == current.Id))
            .ToArray();
        if (pools.Length == 0) pools = [player.Character.CardPool];
        return CardCreationOptions.ForNonCombatWithUniformOdds(pools,
            card => card.Rarity == current.Rarity && card.Id != current.Id);
    }

    internal static void Reload(NRewardButton row, Reward reward)
    {
        if (!GodotObject.IsInstanceValid(row)) return;
        NHoverTipSet.Remove(row);
        var icon = row.GetNode<Control>("%Icon");
        foreach (var child in icon.GetChildren().ToArray())
        {
            icon.RemoveChild(child);
            child.QueueFree();
        }
        // PotionReward caches its visual node, which must be recreated after the model changes.
        if (reward is PotionReward potion) PotionIcon.SetValue(potion, null);
        ReloadButton.Invoke(row, null);
    }
}

/// <summary>
/// Gives each displayed RewardsSet a room-local identity and captures the set
/// into the existing recovery packet. This covers treasure, event, and custom
/// item rewards as well as combat rewards without changing shared recovery code.
/// </summary>
internal static class RewardRerollPersistence
{
    private static readonly System.Reflection.FieldInfo GoldMin = AccessTools.Field(typeof(GoldReward), "_min")!;
    private static readonly System.Reflection.FieldInfo GoldMax = AccessTools.Field(typeof(GoldReward), "_max")!;
    private sealed class SetIdentity(string roomKey, int ordinal, bool combat)
    {
        internal string RoomKey { get; } = roomKey;
        internal int Ordinal { get; } = ordinal;
        internal bool Combat { get; } = combat;
        internal bool Restored { get; set; }
        internal bool KeysBound { get; set; }
        internal string StorageKey => Combat ? RoomKey : RoomKey + "|encore-set:" + Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private sealed record CardFilterIds(string[] Values);

    private static readonly ConditionalWeakTable<RewardsSet, SetIdentity> identities = new();
    private static readonly ConditionalWeakTable<CardReward, CardFilterIds> cardFilterIds = new();
    private static readonly Dictionary<string, int> ordinals = [];
    private static readonly System.Reflection.FieldInfo ScreenSet = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet")!;
    private static readonly System.Reflection.FieldInfo Generated = AccessTools.Field(typeof(RewardsSet), "_isGenerated")!;
    private static readonly System.Reflection.MethodInfo RestoreReward = AccessTools.Method(typeof(RewardRecovery), "RestoreReward")!;
    private static readonly System.Reflection.MethodInfo CaptureReward = AccessTools.Method(typeof(RewardRecovery), "CaptureReward")!;
    private static WeakReference<NRewardsScreen>? visibleScreen;
    private static WeakReference<RewardsSet>? visibleSet;
    private static WeakReference<RewardsSet>? checkpointSet;

    private static SetIdentity Identity(RewardsSet set)
    {
        if (identities.TryGetValue(set, out var identity)) return identity;
        string roomKey = RaceRuntime.RoomKey;
        int ordinal = ordinals.GetValueOrDefault(roomKey);
        ordinals[roomKey] = ordinal + 1;
        identity = new(roomKey, ordinal, RaceRuntime.State?.CurrentRoom is CombatRoom);
        identities.Add(set, identity);
        return identity;
    }

    internal static bool TrackVisibleSet(NRewardsScreen screen, out RewardsSet set)
    {
        set = null!;
        if (!GodotObject.IsInstanceValid(screen) || ScreenSet.GetValue(screen) is not RewardsSet found) return false;
        set = found;
        visibleScreen = new(screen);
        visibleSet = new(set);
        BindRewardKeys(set);
        return true;
    }

    internal static void BindRewardKeys(RewardsSet set)
    {
        var identity = Identity(set);
        if (identity.KeysBound || identity.Restored || RewardRecovery.IsRestored(set)) return;
        foreach (var cardReward in Flatten(set.Rewards).OfType<CardReward>()) FreezeCardFilter(cardReward);
        for (int index = 0; index < set.Rewards.Count; index++)
            BindTree(set.Rewards[index], identity.StorageKey + "|reward:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        identity.KeysBound = true;
    }

    private static void BindTree(Reward reward, string key)
    {
        if (reward is LinkedRewardSet linked)
        {
            for (int index = 0; index < linked.Rewards.Count; index++)
                BindTree(linked.Rewards[index], key + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        // Rewards may already carry an expedition ticket or a committed receipt.
        // Binding screen-local indices must never replace that identity.
        RaceRuntime.BindGeneratedRewardKey(reward, key);
    }

    internal static RewardsSet? CurrentSetFor(Reward reward)
    {
        if (visibleSet == null || !visibleSet.TryGetTarget(out var set) || !IsCurrentSet(set)) return null;
        return RewardRerollEffect.IsTrackedReward(set,reward) ? set : null;
    }

    internal static bool IsCurrentSet(RewardsSet set)
        => visibleSet?.TryGetTarget(out var currentSet) == true && ReferenceEquals(currentSet, set)
            && visibleScreen?.TryGetTarget(out var screen) == true && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree();

    internal static void BeginOffer(RewardsSet set)
    {
        var identity = Identity(set);
        RestorePending(set, identity);
    }

    internal static void RestorePendingAtScreen(RewardsSet set)
        => RestorePending(set, Identity(set));

    private static void RestorePending(RewardsSet set, SetIdentity identity)
    {
        if (RewardRecovery.Pending is not { } pending
            || identity.Combat || RaceRuntime.State?.CurrentRoom is CombatRoom) return;

        // Non-combat set ordinals are process-local. Adopt the saved identity
        // only when its room prefix matches this exact native room.
        string prefix = identity.RoomKey + "|encore-set:";
        if (pending.RoomKey.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(pending.RoomKey[prefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int savedOrdinal)
            && savedOrdinal is >= 0 and < int.MaxValue)
        {
            identities.Remove(set);
            identity = new SetIdentity(identity.RoomKey, savedOrdinal, combat: false);
            identities.Add(set, identity);
            ordinals[identity.RoomKey] = Math.Max(ordinals.GetValueOrDefault(identity.RoomKey), savedOrdinal + 1);
        }
        if (pending.RoomKey != identity.StorageKey) return;

        set.Rewards.Clear();
        foreach (var entry in pending.Rewards)
        {
            var reward = (Reward?)RestoreReward.Invoke(null, [entry, set.Player]);
            if (reward != null) set.Rewards.Add(reward);
        }
        if (pending.DisallowSkipping) set.WithSkippingDisallowed();
        Generated.SetValue(set, true);
        identity.Restored = identity.KeysBound = true;
        RewardRecovery.Pending = null;
    }

    internal static SavedCombatRewards? CaptureVisible(RaceSession session)
    {
        if (visibleSet == null || !visibleSet.TryGetTarget(out var set)) return null;
        if (!IsCurrentSet(set)) return null;
        return CaptureSet(session, set);
    }

    private static SavedCombatRewards CaptureSet(RaceSession session, RewardsSet set)
    {
        var identity = Identity(set);
        var contents = new List<SavedRewardContent>();
        try
        {
            foreach (var reward in set.Rewards)
                if (CaptureReward.Invoke(null, [reward]) is SavedRewardContent content) contents.Add(content);
        }
        catch (System.Reflection.TargetInvocationException error)
        {
            throw error.InnerException ?? error;
        }
        string roomKey = identity.Combat ? RaceRuntime.RoomKey : identity.StorageKey;
        return new(roomKey, contents.ToArray(), set.DisallowSkipping);
    }

    internal static void CheckpointSet(RewardsSet set)
    {
        checkpointSet = new(set);
        try { RaceRuntime.Checkpoint(); }
        finally { checkpointSet = null; }
    }

    internal static SavedCombatRewards? CaptureTransactionSet(RaceSession session)
        => checkpointSet?.TryGetTarget(out var set) == true ? CaptureSet(session, set) : null;

    internal static SavedRewardContent? CaptureNativeReward(Reward reward)
    {
        var session = RaceSession.Current;
        if (session == null) return null;
        string key = RaceRuntime.RewardKey(reward);
        if (reward.SuccessfullySelected || session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + key)) return null;
        if (reward is GoldReward gold)
        {
            var bounds = (Min: (int)GoldMin.GetValue(gold)!, Max: (int)GoldMax.GetValue(gold)!);
            return new(key, Pack(gold.ToSerializable()), Relic: "fdzf:encore-gold-range:" + EncodeJson(new[] { bounds.Min, bounds.Max }));
        }
        if (reward is not CardReward cards) return null;
        var options = (CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions")!.Invoke(cards, null)!;
        if (options.CardPoolFilter == null) return null;
        var allowed = FreezeCardFilter(cards);
        var save = new SerializableReward
        {
            RewardType = RewardType.Card,
            Source = options.Source,
            RarityOdds = options.RarityOdds,
            CardPoolIds = options.CardPools.Select(pool => pool.Id).ToList(),
            OptionCount = cards.Cards.Count()
        };
        return new(key, Pack(save), cards.Cards.Select(NativeCheckpoint.EncodeCard).ToArray(),
            Relic: "fdzf:encore-card-filter:" + EncodeJson(allowed), CanSkip: cards.CanSkip,
            CanReroll: cards.CanReroll,
            HasRerolled: (bool)AccessTools.Field(typeof(CardReward), "_hasBeenRerolled").GetValue(cards)!,
            CardFlags: (int)options.Flags,
            ManualCards: (bool)AccessTools.Field(typeof(CardReward), "_cardsWereManuallySet").GetValue(cards)!);
    }

    internal static bool TryRestoreGold(SavedRewardContent entry, Player player, out Reward result)
    {
        if (entry.Relic?.StartsWith("fdzf:encore-gold-range:", StringComparison.Ordinal) == true)
        {
            var range = DecodeJson<int[]>(entry.Relic["fdzf:encore-gold-range:".Length..]);
            if (range.Length != 2 || range[0] > range[1]) throw new InvalidDataException(L10n.T("金币奖励范围数据无效。"));
            var saved = Unpack<SerializableReward>(entry.Packet);
            var gold = new GoldReward(range[0], range[1], player, saved.WasGoldStolenBack);
            AccessTools.PropertySetter(typeof(GoldReward), nameof(GoldReward.Amount))!.Invoke(gold, [saved.GoldAmount]);
            RaceRuntime.BindRewardKey(gold, entry.Key);
            result = gold;
            return true;
        }
        result = null!;
        return false;
    }

    internal static void RestoreCardFilter(SavedRewardContent entry, ref Reward result)
    {
        if (entry.Cards == null || entry.Relic?.StartsWith("fdzf:encore-card-filter:", StringComparison.Ordinal) != true
            || result is not CardReward cards) return;
        string[] allowed = DecodeJson<string[]>(entry.Relic["fdzf:encore-card-filter:".Length..]);
        var ids = new HashSet<string>(allowed, StringComparer.Ordinal);
        Func<CardModel, bool> filter = card => ids.Contains(card.Id.ToString());
        foreach (string property in new[] { "Options", "RerollOptions" })
            if (AccessTools.PropertyGetter(typeof(CardReward), property)!.Invoke(cards, null) is CardCreationOptions options)
                options.WithFilter(filter);
        if (!cardFilterIds.TryGetValue(cards, out _)) cardFilterIds.Add(cards, new(allowed));
    }

    internal static string[] FreezeCardFilter(CardReward cards)
    {
        if (cardFilterIds.TryGetValue(cards, out var frozen)) return frozen.Values;
        var options = (CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions")!.Invoke(cards, null)!;
        if (options.CardPoolFilter == null) return [];
        var allowed = options.GetPossibleCards(cards.Player).Select(card => card.Id.ToString())
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(allowed, StringComparer.Ordinal);
        Func<CardModel, bool> filter = card => ids.Contains(card.Id.ToString());
        foreach (string property in new[] { "Options", "RerollOptions" })
            if (AccessTools.PropertyGetter(typeof(CardReward), property)!.Invoke(cards, null) is CardCreationOptions creation)
                creation.WithFilter(filter);
        cardFilterIds.Add(cards, new(allowed));
        return allowed;
    }

    private static IEnumerable<Reward> Flatten(IEnumerable<Reward> rewards)
    {
        foreach (var reward in rewards)
        {
            if (reward is LinkedRewardSet linked)
                foreach (var child in Flatten(linked.Rewards)) yield return child;
            else yield return reward;
        }
    }

    private static string Pack(MegaCrit.Sts2.Core.Multiplayer.Serialization.IPacketSerializable value)
    {
        var writer = new PacketWriter();
        value.Serialize(writer);
        return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
    }

    private static T Unpack<T>(string packet) where T : MegaCrit.Sts2.Core.Multiplayer.Serialization.IPacketSerializable, new()
    {
        var reader = new PacketReader();
        reader.Reset(Convert.FromBase64String(packet));
        var value = new T();
        value.Deserialize(reader);
        return value;
    }

    private static string EncodeJson<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
    private static T DecodeJson<T>(string value) => JsonSerializer.Deserialize<T>(Convert.FromBase64String(value))
        ?? throw new InvalidDataException(L10n.T("奖励恢复附加数据无效。"));

    internal static void Reset()
    {
        identities.Clear();
        cardFilterIds.Clear();
        ordinals.Clear();
        visibleScreen = null;
        visibleSet = null;
        checkpointSet = null;
    }
}

[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
internal static class RestoreCustomRewardSetPatch
{
    static void Prefix(RewardsSet __instance)
    {
        if (RaceRuntime.Live) RewardRerollPersistence.BeginOffer(__instance);
    }
}

[HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.ShowScreen))]
internal static class RestoreCustomRewardScreenPatch
{
    static void Prefix(ref RewardsSet set)
    {
        if (!RaceRuntime.Live) return;
        RewardRerollPersistence.RestorePendingAtScreen(set);
        RewardRerollPersistence.BindRewardKeys(set);
        RewardRerollTransaction.ReplayPending(set);
    }
}

[HarmonyPatch(typeof(RaceRecovery), nameof(RaceRecovery.Capture))]
internal static class CaptureVisibleRewardSetPatch
{
    static void Postfix(RaceSession session, ref RecoverySnapshot __result)
    {
        if (!RaceRuntime.Live || session.IsMock) return;
        var saved = RewardRerollPersistence.CaptureTransactionSet(session)
            ?? RewardRerollPersistence.CaptureVisible(session);
        if (saved != null) __result = __result with { Rewards = saved };
    }
}

[HarmonyPatch(typeof(RaceRuntime), nameof(RaceRuntime.Start))]
internal static class ResetRerollSetTrackingOnRunStartPatch
{
    static void Prefix() => RewardRerollPersistence.Reset();
}

[HarmonyPatch(typeof(RaceRuntime), "ResetLocalTracking")]
internal static class ResetRerollSetTrackingPatch
{
    static void Postfix() => RewardRerollPersistence.Reset();
}

[HarmonyPatch]
internal static class CaptureExtendedRewardContentPatch
{
    static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(RewardRecovery), "CaptureReward")!;

    static bool Prefix(Reward reward, ref SavedRewardContent __result)
    {
        bool filteredCards = reward is CardReward cards
            && ((CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions")!.Invoke(cards, null)!).CardPoolFilter != null;
        if (!RaceRuntime.Live || reward is not GoldReward && !filteredCards) return true;
        __result = RewardRerollPersistence.CaptureNativeReward(reward)!;
        return false;
    }
}

[HarmonyPatch]
internal static class RestoreExtendedRewardContentPatch
{
    static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(RewardRecovery), "RestoreReward")!;

    static bool Prefix(SavedRewardContent entry, Player player, ref Reward __result)
    {
        if (RewardRerollPersistence.TryRestoreGold(entry, player, out __result)) return false;
        return true;
    }

    static void Postfix(SavedRewardContent entry, ref Reward __result)
        => RewardRerollPersistence.RestoreCardFilter(entry, ref __result);
}

[HarmonyPatch(typeof(NRewardButton), nameof(NRewardButton.Create))]
internal static class RewardRerollButtonPatch
{
    static void Postfix(Reward reward, NRewardsScreen screen, NRewardButton __result)
    {
        if (screen == null || !RaceRuntime.Live || RaceSession.Current is not { } session
            || RewardRerollRules.Limit(session.Me) <= 0
            || !RewardRerollPersistence.TrackVisibleSet(screen, out var set)
            || !RewardRerollEffect.IsTrackedReward(set, reward)
            || !RewardRerollEffect.IsSupported(reward) || reward is CardReward) return;
        RewardRerollEffect.AttachButton(__result, reward, set);
    }
}

/// <summary>Mode-offer bridge used by RaceUi.ChooseEffect when adding the refresh row.</summary>
internal static class RewardRerollOfferUi
{
    internal static bool CanRefresh(string offerId)
    {
        if (RaceSession.Current is not { } session || !RaceRuntime.Live) return false;
        var usage = RewardRerollRules.OfferUsage(session.Me, offerId);
        return usage.CanRefresh && session.Engine.State.Offers.TryGetValue(offerId, out var offer)
            && !offer.Resolved && !session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + offer.RewardKey);
    }

    internal static string RefreshLabel(string offerId)
    {
        if (RaceSession.Current is not { } session) return L10n.T("刷新候选");
        var usage = RewardRerollRules.OfferUsage(session.Me, offerId);
        return L10n.F($"刷新候选 · 已用 {usage.Used}/{usage.Limit}");
    }

    internal static async Task RefreshAsync(string rewardKey, Route route)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException(L10n.T("比赛会话已结束。"));
        var offerId = RewardRerollRules.OfferId(session.LocalId, rewardKey, route);
        if (!CanRefresh(offerId)) throw new RuleException(L10n.T("这份候选奖励没有可用的刷新次数。"));
        await session.Submit(session.New(RewardRerollRules.OfferCommand, rewardKey, route: route));
        // Offer.Candidates and the usage counter are already in the synchronized
        // Core state; this checkpoint makes the generation durable before UI reopen.
        RaceRuntime.Checkpoint();
    }
}
