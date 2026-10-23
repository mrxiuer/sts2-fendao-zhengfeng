using Godot;
using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Nodes;

namespace FendaoZhengfeng.Mod;

public static class RaceUi
{
    private static CanvasLayer? layer;
    private static PanelContainer? panel;
    private static VBoxContainer? list;
    private static Label? notice;
    private static Button? debugToggle;
    private static Godot.Timer? timer;
    private static bool refreshing;
    private static int busy;
    private static long renderedRevision = -1;
    private static bool renderedLive;
    private static long noticeUntil;
    private static readonly Theme uiTheme = new() { DefaultFontSize = 25 };
    public static void Install(NGame game)
    {
        if (layer != null && GodotObject.IsInstanceValid(layer)) return;
        layer = new CanvasLayer { Layer = 90 }; game.AddChild(layer);
        var toggle = new Button { Text = L10n.T("分道争锋"), Position = new Vector2(1040, 12), Theme = uiTheme }; layer.AddChild(toggle); debugToggle = toggle; toggle.Visible = RaceConfig.DebugVisible;
        EffectHud.Install(layer); RaceRoster.Install(layer);
        panel = new PanelContainer { Position = new Vector2(16, 76), CustomMinimumSize = new Vector2(740, 620), Visible = false, Theme = uiTheme }; layer.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", PanelStyle());
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(700, 580), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; panel.AddChild(scroll);
        list = new VBoxContainer { CustomMinimumSize = new Vector2(675, 0) }; list.AddThemeConstantOverride("separation", 16); scroll.AddChild(list);
        notice = new Label { Text = "", Position = new Vector2(800, 100), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1000, 0), Theme = uiTheme }; layer.AddChild(notice);
        toggle.Pressed += TogglePanel;
        game.GetTree().ProcessFrame += () =>
        {
            try { RaceSession.Current?.PumpNetwork(); }
            catch (Exception e) { Error(L10n.T(e.Message)); }
        };
        timer = new Godot.Timer { WaitTime = 0.05, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        timer.Timeout += () =>
        {
            try
            {
                RaceSession.Current?.Tick();
                EffectHud.AlignWithRelics(); RaceRoster.Tick(); CardDurabilityUi.Tick(); CombatEffectDisplay.Tick(); AdvancedEffects.Tick();
                if (debugToggle != null) debugToggle.Visible = RaceConfig.DebugVisible;
                if (notice != null && System.Environment.TickCount64 > noticeUntil) notice.Text = "";
                if ((RaceSession.Current?.Engine.State.Revision ?? -1) != renderedRevision || RaceRuntime.Live != renderedLive) Refresh();
            }
            catch (Exception e) { Error(L10n.T(e.Message)); }
        }; layer.AddChild(timer);
        Refresh();
    }
    internal static void RefreshLanguage()
    {
        if (debugToggle != null) debugToggle.Text = L10n.T("分道争锋");
        Refresh();
    }
    internal static void ConcealPanel() { if (panel != null) panel.Visible = false; }
    internal static bool DebugWinButtonVisible => list?.GetChildren().OfType<Button>().Any(b => b.Text == L10n.T("测试：击杀当前敌人并结算奖励")) == true;
    internal static bool DebugButtonVisible => debugToggle?.Visible == true;
    internal static void TogglePanel() { if (panel != null) { panel.Visible = !panel.Visible; Refresh(); } }
    public static void Notify(string message) { noticeUntil = System.Environment.TickCount64 + 8000; if (notice != null) notice.Text = L10n.T(message); }
    public static void Error(string message) { Godot.GD.PushWarning(L10n.T("[分道争锋] ") + message); Notify(message); }
    public static void Run(Func<Task> action) => _ = Safe(action);
    private static async Task Safe(Func<Task> action) { busy++; try { await action(); } catch (Exception e) { Error(L10n.T(e.Message)); } finally { busy--; } }
    public static async Task WhenIdle() { while (busy > 0) await Task.Delay(20); }
    public static void Refresh()
    {
        if (list == null || refreshing) return; refreshing = true;
        try
        {
            renderedRevision = RaceSession.Current?.Engine.State.Revision ?? -1;
            renderedLive = RaceRuntime.Live;
            EffectHud.Refresh(); RaceRoster.Refresh();
            if (panel?.Visible != true) return;
            foreach (var node in list.GetChildren()) { list.RemoveChild(node); node.QueueFree(); }
            if (RaceSession.Current is not { } s)
            {
                Text(L10n.T("分道争锋 · 调试"));
                Text(L10n.T("模式开关、效果列表和调试开关位于模组配置 → 分道争锋。本局玩法配置以房主为准。"));
                if (RaceConfig.DebugVisible)
                {
                    var resume = new CheckButton { Text = L10n.T("恢复上次共同检查点（原玩家与角色）"), ButtonPressed = RaceRuntime.ResumeRequested }; resume.Toggled += value => RaceRuntime.ResumeRequested = value; list.AddChild(resume);
                    Button(L10n.T("本地模拟测试（无需好友）"), MockTesting.Start);
                }
                return;
            }
            Text(RaceConfig.DebugVisible ? L10n.T("比赛进度 · ") + s.Engine.State.Id : L10n.T("比赛进度"));
            if (s.IsHost && !s.IsMock) Button(s.PauseRequested ? L10n.T("继续比赛 / 取消共同暂停") : L10n.T("保存并暂停比赛"), () => { if (s.PauseRequested) s.ContinueRace(); else s.RequestPause(); return Task.CompletedTask; });
            if (s.IsMock && RaceConfig.DebugVisible)
            {
                Text(L10n.T("本地模拟 · 对手为测试数据，不连接其他玩家，不写入原版进度，也不替代 Steam 网络验证。"));
                Button(L10n.T("测试：模拟对手施加诅咒"), () => MockTesting.SelectEffect(EffectKind.Curse));
                Button(L10n.T("测试：取得自身双刃契约"), () => MockTesting.SelectEffect(EffectKind.Contract));
                Button(L10n.T("测试：取得自身增益"), () => MockTesting.SelectEffect(EffectKind.Boon));
                Button(L10n.T("测试：进入普通 / 精英 / Boss 战斗"), MockTesting.EnterBattle);
                Button(L10n.T("测试：进入下一幕（最多第五幕）"), MockTesting.NextAct);
                Button(L10n.T("测试：进入本幕第一 / 第二个Boss"), MockTesting.SelectCampaignBoss);
                Button(L10n.T("测试：进入商店 / 营地"), MockTesting.SelectService);
                Button(L10n.T("测试：模拟对手抢先击杀本幕 Boss"), MockTesting.OpponentBoss);
                Button(L10n.T("测试：自己获得 500 金币"), MockTesting.Gold);
                Button(L10n.T("测试：模拟对手获得 100 金币"), MockTesting.OpponentGold);
            }
            if (RaceRuntime.Live && RaceConfig.DebugVisible)
            {
                Button(L10n.T("测试：击杀当前敌人并结算奖励"), DebugTesting.WinBattle);
                Button(L10n.T("测试：进入第五幕心脏战"), DebugTesting.EnterHeart);
            }
            if (s.PauseRequested) Text(s.Paused ? L10n.T("共同检查点已保存，比赛已暂停。") : L10n.T("正在准备共同存档：完成当前房间结算后，选择下一个地图节点即可进入保存屏障。"));
            if (s.WaitingAtCheckpoint) { Text(L10n.T("个人进度已冻结，等待其他玩家到达检查点或房主继续。")); return; }
            foreach (var p in s.Engine.State.Racers.Values)
            {
                var rank = s.Engine.State.FinishOrder.IndexOf(p.Id);
                Text(L10n.F($"{L10n.RacerName(p)}{(p.Id == s.LocalId ? L10n.T("（你）") : "")}  第{p.Act + 1}幕 / {p.Floor}层  {RoomName(p.Room)}  生命{p.Health}/{p.MaxHealth}  ") + (p.Eliminated ? L10n.T("已淘汰") : rank >= 0 ? L10n.F($"第{rank + 1}名") : L10n.T("进行中")));
            }
            Text(L10n.F($"诅咒护盾 {s.Me.Shields}  替死鬼 {s.Me.Ghosts}  激活负担 {s.Me.Burden}"));
            if (RaceConfig.DebugVisible && RaceModifier.Active is { } modifier) Text(L10n.F($"本回合伤害倍率 {modifier.Dice}%"));
            foreach (var h in s.Me.Effects.Values)
            {
                var e = Catalog.Get(h.Id); int active = s.Me.Count(h.Id); int queued = h.Grants.Count - active;
                var sources = h.Grants.Select(g => g.Source).Where(id => id != s.LocalId).Distinct().Select(id => s.Engine.State.Racers.TryGetValue(id, out var source) ? L10n.RacerName(source) : id).ToList();
                Text(L10n.T(e.Name) + (e.Kind == EffectKind.Boon ? L10n.F($" · 强化{h.Rank}") : h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + $" · {(e.Kind == EffectKind.Boon ? L10n.T("增益") : TierName(e.Tier))}" + (queued > 0 ? L10n.F($" · {queued}次待激活") : "") + "\n" + L10n.T(e.Description) + (sources.Count > 0 ? L10n.T("\n来源：") + string.Join("、", sources) : ""));
            }
            foreach (var prize in s.Me.BossPrizes.ToList())
            {
                var captured = prize;
                if (prize.EndsWith(":first", StringComparison.Ordinal))
                {
                    if (Catalog.All.Any(e => e.FirstKillPool && s.Engine.State.EnabledEffects.Contains(e.Id)))
                        Button(L10n.T("领取本幕首杀增益"), async () => { await ChooseEffect(captured, Route.FirstKill, true); Refresh(); });
                }
                else Button(L10n.T("领取本幕净化奖励"), async () => { var id = await ChooseCleanse(CleanseAt.Boss); if (id != null) await s.Submit(s.New("boss_cleanse", captured, id)); await Apply(); Refresh(); });
                Button(L10n.T("领取一层诅咒护盾"), async () => { await s.Submit(s.New("boss_shield", captured)); Refresh(); });
            }
        }
        finally { refreshing = false; }
    }
    private static async Task Apply() { if (RaceModifier.Active is { } m) { await m.ApplyReceipts(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), false); await m.UpdateMaxHp(); } }
    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color("13212cf5"), BorderColor = new Color("8b7951"),
        BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
        ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 18, ContentMarginBottom = 18
    };
    private static void Text(string value) => list!.AddChild(new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(665, 0) });
    private static void Button(string text, Func<Task> action) { var b = new Godot.Button { Text = text }; b.Pressed += () => Run(action); list!.AddChild(b); }
    public static string TierName(Tier tier) => tier switch { Tier.Ordinary => L10n.T("普通档"), Tier.Elite => L10n.T("精英档"), _ => L10n.T("Boss档") };
    private static string RoomName(string room) => room switch { "Monster" => L10n.T("普通战斗"), "Elite" => L10n.T("精英战斗"), "Boss" => L10n.T("Boss战斗"), "Shop" => L10n.T("商店"), "RestSite" => L10n.T("营地"), "Event" => L10n.T("事件"), "Treasure" => L10n.T("宝箱"), _ => L10n.T("准备中") };
    public static Task<int> Choose(string title, IReadOnlyList<string> choices, IReadOnlyList<Texture2D>? icons = null)
    {
        if (layer == null) throw new InvalidOperationException(L10n.T("界面尚未初始化。"));
        if (RaceRuntime.Live) return NativeChoiceUi.Choose(layer, title, choices, icons);
        var done = new TaskCompletionSource<int>();
        var veil = new ColorRect { Color = new Color(0, 0, 0, 0.8f) }; veil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); layer.AddChild(veil);
        var box = new PanelContainer { Position = new Vector2(360, 180), CustomMinimumSize = new Vector2(1000, 0), Theme = uiTheme }; box.AddThemeStyleboxOverride("panel", PanelStyle()); veil.AddChild(box);
        var choiceScroll = new ScrollContainer { CustomMinimumSize = new Vector2(970, Math.Min(720, 100 + choices.Count * 120)), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(choiceScroll);
        var content = new VBoxContainer { CustomMinimumSize = new Vector2(950, 0) }; content.AddThemeConstantOverride("separation", 14); choiceScroll.AddChild(content);
        content.AddChild(new Label { Text = title, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        void Finish(int index) { if (done.TrySetResult(index)) veil.QueueFree(); }
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var b = new Godot.Button { Text = choices[i], AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(950, 76) };
            if (icons != null) { b.Icon = icons[i]; b.AddThemeConstantOverride("icon_max_width", 64); }
            b.Pressed += () => Finish(index); content.AddChild(b);
        }
        var cancel = new Godot.Button { Text = L10n.T("返回") }; cancel.Pressed += () => Finish(-1); content.AddChild(cancel);
        veil.TreeExiting += () => done.TrySetResult(-1); return done.Task;
    }
    public static async Task<bool> ChooseEffect(string reward, Route route, bool boss)
    {
        var s = RaceSession.Current ?? throw new InvalidOperationException(L10n.T("会话已结束。"));
        await s.Submit(s.New("offer", reward, boss: boss, route: route));
        while (ReferenceEquals(RaceSession.Current, s))
        {
            var offer = s.Engine.State.Offers[s.LocalId + ":" + reward + ":" + route];
            var candidates = offer.Candidates.ToArray();
            var labels = candidates.Select(id => { var e = Catalog.Get(id); return L10n.T(e.Name) + "\n" + TierName(e.Tier) + " · " + (e.Kind == EffectKind.Curse ? L10n.T("诅咒 · 所有对手") : e.Kind == EffectKind.Contract ? L10n.T("双刃契约 · 自己") : L10n.T("增益 · 自己")) + "\n" + L10n.T(e.Description); }).ToList();
            var icons = candidates.Select(EffectIcons.Get).ToList();
            var usage = RewardRerollRules.OfferUsage(s.Me, offer.Id);
            bool canReroll = usage.CanRefresh;
            if (canReroll) { labels.Add(L10n.F($"刷新候选 · 剩余{usage.Remaining}次\n为这份奖励重新生成一组效果。")); icons.Add(EffectIcons.Get("encore_rewards")); }
            int index = await Choose(L10n.T("选择效果 · ") + (route == Route.Mixed ? L10n.T("混合三选一") : route == Route.Disrupt ? L10n.T("施加给所有对手") : L10n.T("施加给自己")), labels, icons);
            if (index < 0 || !ReferenceEquals(RaceSession.Current, s)) return false;
            if (canReroll && index == candidates.Length)
            { await s.Submit(s.New("offer_reroll", reward, route: route)); continue; }
            await s.Submit(s.New("choose", offer.Id, candidates[index]) with { MaxHealth = RaceRuntime.State!.Players.Single().Creature.MaxHp }); await Apply(); RaceRuntime.Checkpoint(); Refresh(); return true;
        }
        return false;
    }

    public static async Task<string?> ChooseCleanse(CleanseAt place)
    {
        if (place == CleanseAt.Shop) { Error(L10n.T("商店不提供模式效果净化。")); return null; }
        var s = RaceSession.Current!;
        var effects = s.Me.Effects.Values.Where(h => Catalog.Get(h.Id).Kind != EffectKind.Boon)
            .Where(h => place == CleanseAt.Boss || place == CleanseAt.Camp && Catalog.Get(h.Id).Tier == Tier.Ordinary || place == CleanseAt.Shop && Catalog.Get(h.Id).Tier != Tier.Boss).ToList();
        if (effects.Count == 0) { Error(L10n.T("当前没有可通过此渠道净化的效果。")); return null; }
        int index = await Choose(L10n.T("选择净化效果（包括全部强化）"), effects.Select(h => L10n.T(Catalog.Get(h.Id).Name) + (h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + (place == CleanseAt.Shop ? L10n.F($" · {Balance.CleansePrice(Catalog.Get(h.Id), h.Grants.Count)} 金币") : "") + "\n" + EffectDetails.Describe(h.Id)).ToList(), effects.Select(h => EffectIcons.Get(h.Id)).ToList());
        return index < 0 ? null : effects[index].Id;
    }
}
