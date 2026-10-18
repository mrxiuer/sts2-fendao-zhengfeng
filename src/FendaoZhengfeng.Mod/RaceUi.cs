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
    private static Godot.Timer? timer;
    private static bool refreshing;
    private static int busy;
    public static void Install(NGame game)
    {
        if (layer != null && GodotObject.IsInstanceValid(layer)) return;
        layer = new CanvasLayer { Layer = 90 }; game.AddChild(layer);
        var toggle = new Button { Text = "分道争锋", Position = new Vector2(16, 12) }; layer.AddChild(toggle);
        panel = new PanelContainer { Position = new Vector2(16, 60), CustomMinimumSize = new Vector2(580, 220), Visible = false }; layer.AddChild(panel);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(580, 220), Size = new Vector2(580, 550) }; panel.AddChild(scroll);
        list = new VBoxContainer { CustomMinimumSize = new Vector2(550, 0) }; scroll.AddChild(list);
        notice = new Label { Text = "", Position = new Vector2(16, 42), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(900, 0) }; layer.AddChild(notice);
        toggle.Pressed += () => { panel.Visible = !panel.Visible; Refresh(); };
        timer = new Godot.Timer { WaitTime = 0.05, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        timer.Timeout += () => { try { RaceSession.Current?.Tick(); } catch (Exception e) { Error(e.Message); } }; layer.AddChild(timer);
        Refresh();
    }
    public static void Error(string message) { Godot.GD.PushWarning("[分道争锋] " + message); if (notice != null) notice.Text = message; }
    public static void Run(Func<Task> action) => _ = Safe(action);
    private static async Task Safe(Func<Task> action) { busy++; try { await action(); } catch (Exception e) { Error(e.Message); } finally { busy--; } }
    public static async Task WhenIdle() { while (busy > 0) await Task.Delay(20); }
    public static void Refresh()
    {
        if (list == null || refreshing) return; refreshing = true;
        try
        {
            foreach (var node in list.GetChildren()) { list.RemoveChild(node); node.QueueFree(); }
            if (RaceSession.Current is not { } s)
            {
                Text("杀戮尖塔2：分道争锋 · 开发预览");
                var enabled = new CheckButton { Text = "下次多人开局使用分道争锋", ButtonPressed = RaceRuntime.Enabled }; enabled.Toggled += value => RaceRuntime.Enabled = value; list.AddChild(enabled);
                var resume = new CheckButton { Text = "恢复上次共同检查点（原玩家与角色）", ButtonPressed = RaceRuntime.ResumeRequested }; resume.Toggled += value => RaceRuntime.ResumeRequested = value; list.AddChild(resume);
                Text("所有玩家须启用同版本 MOD。本模式不影响普通单人开局。"); return;
            }
            Text("比赛进度 · " + s.Engine.State.Id);
            if (s.IsHost) Button(s.PauseRequested ? "继续比赛 / 取消共同暂停" : "保存并暂停比赛", () => { if (s.PauseRequested) s.ContinueRace(); else s.RequestPause(); return Task.CompletedTask; });
            if (s.PauseRequested) Text(s.Paused ? "共同检查点已保存，比赛已暂停。" : "正在准备共同存档：完成当前房间结算后，选择下一个地图节点即可进入保存屏障。");
            if (s.WaitingAtCheckpoint) { Text("个人进度已冻结，等待其他玩家到达检查点或房主继续。"); return; }
            foreach (var p in s.Engine.State.Racers.Values)
            {
                var rank = s.Engine.State.FinishOrder.IndexOf(p.Id);
                Text($"{p.Name}{(p.Id == s.LocalId ? "（你）" : "")}  第{p.Act + 1}幕 / {p.Floor}层  {RoomName(p.Room)}  生命{p.Health}/{p.MaxHealth}  " + (p.Eliminated ? "已淘汰" : rank >= 0 ? $"第{rank + 1}名" : "进行中"));
            }
            Text($"诅咒护盾 {s.Me.Shields}  替死鬼 {s.Me.Ghosts}  激活负担 {s.Me.Burden}");
            if (RaceModifier.Active is { } modifier) Text($"本回合伤害倍率 {modifier.Dice}%");
            foreach (var h in s.Me.Effects.Values)
            {
                var e = Catalog.Get(h.Id); int active = s.Me.Count(h.Id); int queued = h.Grants.Count - active;
                var sources = h.Grants.Select(g => g.Source).Where(id => id != s.LocalId).Distinct().Select(id => s.Engine.State.Racers.TryGetValue(id, out var source) ? source.Name : id).ToList();
                Text(e.Name + (e.Kind == EffectKind.Boon ? $" · 强化{h.Rank}" : h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + $" · {(e.Kind == EffectKind.Boon ? "增益" : TierName(e.Tier))}" + (queued > 0 ? $" · {queued}次待激活" : "") + "\n" + e.Description + (sources.Count > 0 ? "\n来源：" + string.Join("、", sources) : ""));
            }
            foreach (var prize in s.Me.BossPrizes.ToList())
            {
                var captured = prize;
                if (prize.EndsWith(":first", StringComparison.Ordinal)) Button("领取本幕首杀增益", async () => { await ChooseEffect(captured, Route.FirstKill, true); Refresh(); });
                else Button("领取本幕净化奖励", async () => { var id = await ChooseCleanse(CleanseAt.Boss); if (id != null) await s.Submit(s.New("boss_cleanse", captured, id)); await Apply(); Refresh(); });
                Button("领取一层诅咒护盾", async () => { await s.Submit(s.New("boss_shield", captured)); Refresh(); });
            }
            if (RaceRuntime.State?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MerchantRoom)
                Button("商店净化", async () => { var id = await ChooseCleanse(CleanseAt.Shop); if (id != null) { await s.Submit(s.New("shop_cleanse", RaceRuntime.RoomKey, id, RaceRuntime.State.Players.Single().Gold)); await Apply(); RaceRuntime.Checkpoint(); } Refresh(); });
        }
        finally { refreshing = false; }
    }
    private static async Task Apply() { if (RaceModifier.Active is { } m) { await m.ApplyReceipts(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), false); await m.UpdateMaxHp(); } }
    private static void Text(string value) => list!.AddChild(new Label { Text = value, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(535, 0) });
    private static void Button(string text, Func<Task> action) { var b = new Godot.Button { Text = text }; b.Pressed += () => Run(action); list!.AddChild(b); }
    public static string TierName(Tier tier) => tier switch { Tier.Ordinary => "普通档", Tier.Elite => "精英档", _ => "Boss档" };
    private static string RoomName(string room) => room switch { "Monster" => "普通战斗", "Elite" => "精英战斗", "Boss" => "Boss战斗", "Shop" => "商店", "RestSite" => "营地", "Event" => "事件", "Treasure" => "宝箱", _ => "准备中" };
    public static Task<int> Choose(string title, IReadOnlyList<string> choices)
    {
        if (layer == null) throw new InvalidOperationException("界面尚未初始化。");
        var done = new TaskCompletionSource<int>();
        var veil = new ColorRect { Color = new Color(0, 0, 0, 0.8f) }; veil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); layer.AddChild(veil);
        var box = new PanelContainer { Position = new Vector2(200, 120), CustomMinimumSize = new Vector2(760, 0) }; veil.AddChild(box);
        var content = new VBoxContainer { CustomMinimumSize = new Vector2(730, 0) }; box.AddChild(content);
        content.AddChild(new Label { Text = title, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        void Finish(int index) { if (done.TrySetResult(index)) veil.QueueFree(); }
        for (int i = 0; i < choices.Count; i++) { int index = i; var b = new Godot.Button { Text = choices[i], AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(730, 55) }; b.Pressed += () => Finish(index); content.AddChild(b); }
        var cancel = new Godot.Button { Text = "返回" }; cancel.Pressed += () => Finish(-1); content.AddChild(cancel);
        veil.TreeExiting += () => done.TrySetResult(-1); return done.Task;
    }
    public static async Task<bool> ChooseEffect(string reward, Route route, bool boss)
    {
        var s = RaceSession.Current ?? throw new InvalidOperationException("会话已结束。");
        await s.Submit(s.New("offer", reward, boss: boss, route: route));
        var offer = s.Engine.State.Offers[s.LocalId + ":" + reward + ":" + route];
        int index = await Choose("选择效果 · " + (route == Route.Disrupt ? "施加给所有对手" : "施加给自己"), offer.Candidates.Select(id => { var e = Catalog.Get(id); return e.Name + " · " + TierName(e.Tier) + "\n" + e.Description; }).ToList());
        if (index < 0) return false;
        await s.Submit(s.New("choose", offer.Id, offer.Candidates[index])); await Apply(); RaceRuntime.Checkpoint(); Refresh(); return true;
    }
    public static async Task<string?> ChooseCleanse(CleanseAt place)
    {
        var s = RaceSession.Current!;
        var effects = s.Me.Effects.Values.Where(h => Catalog.Get(h.Id).Kind != EffectKind.Boon)
            .Where(h => place == CleanseAt.Boss || place == CleanseAt.Camp && Catalog.Get(h.Id).Tier == Tier.Ordinary || place == CleanseAt.Shop && Catalog.Get(h.Id).Tier != Tier.Boss).ToList();
        if (effects.Count == 0) { Error("当前没有可通过此渠道净化的效果。"); return null; }
        int index = await Choose("选择净化效果（包括全部强化）", effects.Select(h => Catalog.Get(h.Id).Name + (h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : "") + (place == CleanseAt.Shop ? $" · {Balance.CleansePrice(Catalog.Get(h.Id), h.Grants.Count)} 金币" : "")).ToList());
        return index < 0 ? null : effects[index].Id;
    }
}
