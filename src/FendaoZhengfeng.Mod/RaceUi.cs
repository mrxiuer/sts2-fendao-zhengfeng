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
        var toggle = new Button { Text = "分道争锋", Position = new Vector2(1040, 12), Theme = uiTheme }; layer.AddChild(toggle); debugToggle = toggle; toggle.Visible = RaceConfig.DebugVisible;
        EffectHud.Install(layer); RaceRoster.Install(layer);
        panel = new PanelContainer { Position = new Vector2(16, 76), CustomMinimumSize = new Vector2(740, 620), Visible = false, Theme = uiTheme }; layer.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", PanelStyle());
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(700, 580), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; panel.AddChild(scroll);
        list = new VBoxContainer { CustomMinimumSize = new Vector2(675, 0) }; list.AddThemeConstantOverride("separation", 16); scroll.AddChild(list);
        notice = new Label { Text = "", Position = new Vector2(800, 100), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(1000, 0), Theme = uiTheme }; layer.AddChild(notice);
        toggle.Pressed += TogglePanel;
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
            catch (Exception e) { Error(e.Message); }
        }; layer.AddChild(timer);
        Refresh();
    }
    internal static void ConcealPanel() { if (panel != null) panel.Visible = false; }
    internal static bool DebugButtonVisible => debugToggle?.Visible == true;
    internal static void TogglePanel() { if (panel != null) { panel.Visible = !panel.Visible; Refresh(); } }
    public static void Error(string message) { Godot.GD.PushWarning("[分道争锋] " + message); noticeUntil = System.Environment.TickCount64 + 8000; if (notice != null) notice.Text = message; }
    public static void Run(Func<Task> action) => _ = Safe(action);
    private static async Task Safe(Func<Task> action) { busy++; try { await action(); } catch (Exception e) { Error(e.Message); } finally { busy--; } }
    public static async Task WhenIdle() { while (busy > 0) await Task.Delay(20); }
    public static void Refresh()
    {
        if (list == null || refreshing) return; refreshing = true;
        try
        {
            renderedRevision = RaceSession.Current?.Engine.State.Revision ?? -1;
            renderedLive = RaceRuntime.Live;
            EffectHud.Refresh(); RaceRoster.Refresh();
            foreach (var node in list.GetChildren()) { list.RemoveChild(node); node.QueueFree(); }
            if (RaceSession.Current is not { } s)
            {
                Text("分道争锋 · 调试");
                Text("模式开关、效果列表和调试开关位于模组配置 → 分道争锋。本局玩法配置以房主为准。");
                if (RaceConfig.DebugVisible)
                {
                    var resume = new CheckButton { Text = "恢复上次共同检查点（原玩家与角色）", ButtonPressed = RaceRuntime.ResumeRequested }; resume.Toggled += value => RaceRuntime.ResumeRequested = value; list.AddChild(resume);
                    Button("本地模拟测试（无需好友）", MockTesting.Start);
                }
                return;
            }
            Text(RaceConfig.DebugVisible ? "比赛进度 · " + s.Engine.State.Id : "比赛进度");
            if (s.IsHost && !s.IsMock) Button(s.PauseRequested ? "继续比赛 / 取消共同暂停" : "保存并暂停比赛", () => { if (s.PauseRequested) s.ContinueRace(); else s.RequestPause(); return Task.CompletedTask; });
            if (s.IsMock && RaceConfig.DebugVisible)
            {
                Text("本地模拟 · 对手为测试数据，不连接其他玩家，不写入原版进度，也不替代 Steam 网络验证。");
                Button("测试：模拟对手施加诅咒", () => MockTesting.SelectEffect(EffectKind.Curse));
                Button("测试：取得自身双刃契约", () => MockTesting.SelectEffect(EffectKind.Contract));
                Button("测试：取得自身增益", () => MockTesting.SelectEffect(EffectKind.Boon));
                Button("测试：进入普通 / 精英 / Boss 战斗", MockTesting.EnterBattle);
                Button("测试：进入下一幕（最多第五幕）", MockTesting.NextAct);
                Button("测试：进入本幕第一 / 第二个Boss", MockTesting.SelectCampaignBoss);
                Button("测试：进入商店 / 营地", MockTesting.SelectService);
                Button("测试：击杀当前敌人并结算奖励", MockTesting.WinBattle);
                Button("测试：模拟对手抢先击杀本幕 Boss", MockTesting.OpponentBoss);
                Button("测试：自己获得 500 金币", MockTesting.Gold);
                Button("测试：模拟对手获得 100 金币", MockTesting.OpponentGold);
            }
            if (s.PauseRequested) Text(s.Paused ? "共同检查点已保存，比赛已暂停。" : "正在准备共同存档：完成当前房间结算后，选择下一个地图节点即可进入保存屏障。");
            if (s.WaitingAtCheckpoint) { Text("个人进度已冻结，等待其他玩家到达检查点或房主继续。"); return; }
            foreach (var p in s.Engine.State.Racers.Values)
            {
                var rank = s.Engine.State.FinishOrder.IndexOf(p.Id);
                Text($"{p.Name}{(p.Id == s.LocalId ? "（你）" : "")}  第{p.Act + 1}幕 / {p.Floor}层  {RoomName(p.Room)}  生命{p.Health}/{p.MaxHealth}  " + (p.Eliminated ? "已淘汰" : rank >= 0 ? $"第{rank + 1}名" : "进行中"));
            }
            Text($"诅咒护盾 {s.Me.Shields}  替死鬼 {s.Me.Ghosts}  激活负担 {s.Me.Burden}");
            if (RaceConfig.DebugVisible && RaceModifier.Active is { } modifier) Text($"本回合伤害倍率 {modifier.Dice}%");
            foreach (var h in s.Me.Effects.Values)
            {
                var e = Catalog.Get(h.Id); int active = s.Me.Count(h.Id); int queued = h.Grants.Count - active;
                var sources = h.Grants.Select(g => g.Source).Where(id => id != s.LocalId).Distinct().Select(id => s.Engine.State.Racers.TryGetValue(id, out var source) ? source.Name : id).ToList();
                Text(e.Name + (e.Kind == EffectKind.Boon ? $" · 强化{h.Rank}" : h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + $" · {(e.Kind == EffectKind.Boon ? "增益" : TierName(e.Tier))}" + (queued > 0 ? $" · {queued}次待激活" : "") + "\n" + e.Description + (sources.Count > 0 ? "\n来源：" + string.Join("、", sources) : ""));
            }
            foreach (var prize in s.Me.BossPrizes.ToList())
            {
                var captured = prize;
                if (prize.EndsWith(":first", StringComparison.Ordinal))
                {
                    if (Catalog.All.Any(e => e.FirstKillPool && s.Engine.State.EnabledEffects.Contains(e.Id)))
                        Button("领取本幕首杀增益", async () => { await ChooseEffect(captured, Route.FirstKill, true); Refresh(); });
                }
                else Button("领取本幕净化奖励", async () => { var id = await ChooseCleanse(CleanseAt.Boss); if (id != null) await s.Submit(s.New("boss_cleanse", captured, id)); await Apply(); Refresh(); });
                Button("领取一层诅咒护盾", async () => { await s.Submit(s.New("boss_shield", captured)); Refresh(); });
            }
            if (RaceRuntime.State?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MerchantRoom)
                Button("商店净化", async () => { var id = await ChooseCleanse(CleanseAt.Shop); if (id != null) { await s.Submit(s.New("shop_cleanse", RaceRuntime.RoomKey, id, RaceRuntime.State.Players.Single().Gold)); await Apply(); RaceRuntime.Checkpoint(); } Refresh(); });
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
    public static string TierName(Tier tier) => tier switch { Tier.Ordinary => "普通档", Tier.Elite => "精英档", _ => "Boss档" };
    private static string RoomName(string room) => room switch { "Monster" => "普通战斗", "Elite" => "精英战斗", "Boss" => "Boss战斗", "Shop" => "商店", "RestSite" => "营地", "Event" => "事件", "Treasure" => "宝箱", _ => "准备中" };
    public static Task<int> Choose(string title, IReadOnlyList<string> choices, IReadOnlyList<Texture2D>? icons = null)
    {
        if (layer == null) throw new InvalidOperationException("界面尚未初始化。");
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
        var cancel = new Godot.Button { Text = "返回" }; cancel.Pressed += () => Finish(-1); content.AddChild(cancel);
        veil.TreeExiting += () => done.TrySetResult(-1); return done.Task;
    }
    public static async Task<bool> ChooseEffect(string reward, Route route, bool boss)
    {
        var s = RaceSession.Current ?? throw new InvalidOperationException("会话已结束。");
        await s.Submit(s.New("offer", reward, boss: boss, route: route));
        var offer = s.Engine.State.Offers[s.LocalId + ":" + reward + ":" + route];
        int index = await Choose("选择效果 · " + (route == Route.Mixed ? "混合三选一" : route == Route.Disrupt ? "施加给所有对手" : "施加给自己"), offer.Candidates.Select(id => { var e = Catalog.Get(id); return e.Name + "\n" + TierName(e.Tier) + " · " + (e.Kind == EffectKind.Curse ? "诅咒 · 所有对手" : e.Kind == EffectKind.Contract ? "双刃契约 · 自己" : "增益 · 自己") + "\n" + e.Description; }).ToList(), offer.Candidates.Select(EffectIcons.Get).ToList());
        if (index < 0) return false;
        await s.Submit(s.New("choose", offer.Id, offer.Candidates[index])); await Apply(); RaceRuntime.Checkpoint(); Refresh(); return true;
    }
    public static async Task<string?> ChooseCleanse(CleanseAt place)
    {
        var s = RaceSession.Current!;
        var effects = s.Me.Effects.Values.Where(h => Catalog.Get(h.Id).Kind != EffectKind.Boon)
            .Where(h => place == CleanseAt.Boss || place == CleanseAt.Camp && Catalog.Get(h.Id).Tier == Tier.Ordinary || place == CleanseAt.Shop && Catalog.Get(h.Id).Tier != Tier.Boss).ToList();
        if (effects.Count == 0) { Error("当前没有可通过此渠道净化的效果。"); return null; }
        int index = await Choose("选择净化效果（包括全部强化）", effects.Select(h => Catalog.Get(h.Id).Name + (h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + (place == CleanseAt.Shop ? $" · {Balance.CleansePrice(Catalog.Get(h.Id), h.Grants.Count)} 金币" : "") + "\n" + EffectDetails.Describe(h.Id)).ToList(), effects.Select(h => EffectIcons.Get(h.Id)).ToList());
        return index < 0 ? null : effects[index].Id;
    }
}
