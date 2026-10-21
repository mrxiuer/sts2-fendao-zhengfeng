using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeChoiceUi
{
    internal static readonly Dictionary<string, string> Texts = [];
    internal static int ActiveChoices { get; private set; }
    internal static bool IsOpen => ActiveChoices > 0;
    private static int sequence;
    private static readonly List<Control> veils = [];
    internal static Control? ActiveVeil => veils.LastOrDefault(veil => GodotObject.IsInstanceValid(veil) && !veil.IsQueuedForDeletion());
    internal static LocString Text(string text)
    { string key = "choice." + ++sequence; Texts[key] = text; return new(ModEntry.Id, key); }
    internal static Control Icon(Texture2D texture)
    {
        var icon = new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); return icon;
    }
    internal static Task<int> Choose(CanvasLayer layer, string title, IReadOnlyList<string> choices, IReadOnlyList<Texture2D>? icons)
    {
        var done = new TaskCompletionSource<int>();
        var veil = new ColorRect { Color = new Color(0, 0, 0, 0.8f), Name = "FdzfNativeChoice" };
        veil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); layer.AddChild(veil);
        var template = PreloadManager.Cache.GetScene("res://scenes/screens/rewards_screen.tscn").Instantiate<Control>();
        var frame = template.GetNode<Control>("Rewards");
        var header = template.GetNode<Control>("%HeaderLabel");
        var mask = template.GetNode<Control>("%RewardContainerMask");
        var oldContent = template.GetNode<Control>("%RewardsContainer");
        var oldScrollbar = template.GetNodeOrNull<Control>("%Scrollbar");
        template.RemoveChild(frame); veil.AddChild(frame);
        frame.Modulate = Colors.White; header.Set("text", title);
        oldContent.GetParent().RemoveChild(oldContent); oldContent.Free();
        if (oldScrollbar != null) oldScrollbar.Visible = false;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); mask.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 10); scroll.AddChild(rows);
        ActiveChoices++; veils.Add(veil); RaceUi.ConcealPanel(); EffectHud.HideTips();
        void Finish(int index) { if (done.TrySetResult(index)) { veil.QueueFree(); } }
        for (int i = 0; i <= choices.Count; i++)
        {
            int index = i == choices.Count ? -1 : i;
            string text = index < 0 ? "返回" : choices[index];
            string[] parts = text.Split('\n', 2);
            var option = new UiOptionReward(RaceRuntime.State!.Players.Single(), parts[0], parts.Length > 1 ? parts[1] : parts[0],
                index >= 0 && icons != null ? icons[index] : EffectIcons.Get("choice_reward"), () => Finish(index));
            var row = NRewardButton.Create(option, null!);
            row.CustomMinimumSize = new Vector2(540, 96); row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.TreeExiting += () => NHoverTipSet.Remove(row); rows.AddChild(row);
        }
        template.Free();
        veil.TreeExiting += () => { veils.Remove(veil); ActiveChoices--; done.TrySetResult(-1); };
        return done.Task;
    }
    private sealed class UiOptionReward(Player player, string label, string description, Texture2D icon, Action choose) : Reward(player)
    {
        internal Action Choose { get; } = choose;
        protected override RewardType RewardType => RewardType.Card;
        public override int RewardsSetIndex => 0;
        public override LocString Description => Text(label);
        public override bool IsPopulated => true;
        public override void Populate() { }
        public override void MarkContentAsSeen() { }
        public override IEnumerable<IHoverTip> HoverTips => [new HoverTip(Text(label), description, icon)];
        public override Control CreateIcon() => Icon(icon);
        protected override Task<bool> OnSelect() => Task.FromResult(false);
    }
    [HarmonyPatch(typeof(NRewardButton), "OnRelease")]
    private static class NativeOptionClickPatch
    {
        static bool Prefix(NRewardButton __instance)
        {
            if (__instance.Reward is not UiOptionReward option) return true;
            NHoverTipSet.Remove(__instance); option.Choose(); return false;
        }
    }
}

internal sealed class RaceActionReward(Player player, int kind) : Reward(player)
{
    internal const int SerializedType = 464620;
    internal bool Available => kind == 1 ? RaceModifier.Active?.CanRecoverSkill == true : RaceSession.Current?.Me.BossPrizes.Count > 0;
    protected override RewardType RewardType => (RewardType)SerializedType;
    public override int RewardsSetIndex => 4;
    private string Label => kind == 1 ? "技艺封存 · 取回1张技能牌" : "Boss额外奖励 · 2选1";
    public override LocString Description => NativeChoiceUi.Text(Label);
    public override bool IsPopulated => true;
    public override void Populate() { }
    public override void MarkContentAsSeen() { }
    public override Control CreateIcon() => NativeChoiceUi.Icon(EffectIcons.Get("choice_reward"));
    public override IEnumerable<IHoverTip> HoverTips => [new HoverTip(NativeChoiceUi.Text(Label), kind == 1
        ? $"从技艺封存暂存的 {RaceModifier.Active?.VaultCount ?? 0} 张技能牌中任选1张取回；保留原升级与耐久。每场胜利最多1次。"
        : "点击进入选择：首杀可选增益或1层诅咒护盾；非首杀可选净化或1层诅咒护盾。与遗物献祭独立。", EffectIcons.Get("choice_reward"))];
    protected override async Task<bool> OnSelect()
    {
        if (kind == 1) return await RaceModifier.Active!.RecoverSkill();
        var session = RaceSession.Current!;
        var prize = session.Me.BossPrizes.FirstOrDefault(); if (prize == null) return true;
        bool first = prize.EndsWith(":first", StringComparison.Ordinal);
        int action = await RaceUi.Choose("Boss额外奖励 · 2选1", [first ? "选择首杀增益\n从增益候选中选择1项；R键重置后始终使用首杀奖励。" : "净化1项效果\n选择移除一整项诅咒或双刃契约，包括全部强化。", "获得诅咒护盾\n抵挡下一次收到的诅咒攻击；不移除已有诅咒。"]);
        if (action < 0) return false;
        if (action == 1) await session.Submit(session.New("boss_shield", prize));
        else if (first) { if (!await RaceUi.ChooseEffect(prize, Route.FirstKill, true)) return false; }
        else { var id = await RaceUi.ChooseCleanse(CleanseAt.Boss); if (id == null) return false; await session.Submit(session.New("boss_cleanse", prize, id)); }
        await RaceModifier.Active!.ApplyReceipts(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), session.Me.InBattle);
        RaceRuntime.Checkpoint(); RaceUi.Refresh(); return true;
    }
    public override SerializableReward ToSerializable() => new() { RewardType = (RewardType)SerializedType, GoldAmount = kind };
}
[HarmonyPatch(typeof(Reward), nameof(Reward.FromSerializable))]
internal static class RestoreActionRewardPatch
{
    static bool Prefix(SerializableReward save, Player player, ref Reward __result)
    { if ((int)save.RewardType != RaceActionReward.SerializedType) return true; __result = new RaceActionReward(player, save.GoldAmount); return false; }
}

[HarmonyPatch(typeof(RelicReward), nameof(RelicReward.CreateIcon))]
internal static class SacrificeRewardIconPatch
{
    internal static bool Eligible => RaceRuntime.Live && RaceRuntime.State?.CurrentRoom?.RoomType is MegaCrit.Sts2.Core.Rooms.RoomType.Elite or MegaCrit.Sts2.Core.Rooms.RoomType.Boss;
    static void Postfix(TextureRect __result) { if (Eligible) __result.Texture = EffectIcons.Get("choice_reward"); }
}
[HarmonyPatch(typeof(RelicReward), "get_Description")]
internal static class SacrificeRewardTitlePatch
{
    static void Postfix(ref LocString __result) { if (SacrificeRewardIconPatch.Eligible) __result = new(ModEntry.Id, "reward.sacrifice"); }
}
[HarmonyPatch(typeof(Reward), "get_HoverTips")]
internal static class SacrificeRewardHintPatch
{
    static void Postfix(Reward __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (__instance is not RelicReward || !SacrificeRewardIconPatch.Eligible) return;
        __result = __result.Append(new HoverTip(NativeChoiceUi.Text("领取或献祭 · 2选1"), "点击后可领取这份遗物，或献祭并从混合效果池中3选1。其他奖励保留。", EffectIcons.Get("choice_reward"))).ToList();
    }
}
[HarmonyPatch(typeof(CardModel), "get_IsRemovable")]
internal static class ReservedCardRemovalAvailabilityPatch
{ static void Postfix(CardModel __instance, ref bool __result) { if (RaceRuntime.Live && RaceModifier.Active?.IsReserved(__instance) == true) __result = false; } }

[HarmonyPatch(typeof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow), [typeof(Control), typeof(IEnumerable<IHoverTip>), typeof(HoverTipAlignment)])]
internal static class ModalNativeTooltipLayerPatch
{
    static void Postfix(NHoverTipSet? __result)
    {
        if (__result == null || NativeChoiceUi.ActiveVeil is not { } veil) return;
        __result.Reparent(veil, true); __result.ZIndex = 100;
    }
}
