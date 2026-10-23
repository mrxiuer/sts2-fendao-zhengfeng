using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.addons.mega_text;

namespace FendaoZhengfeng.Mod;

internal static class RaceRoster
{
    private sealed record Row(Button Button, TextureRect Portrait, Label Text, ProgressBar Hp);
    private static VBoxContainer? root;
    private static readonly Dictionary<string, Row> rows = [];
    private static string sessionId = "";
    internal static readonly Color[] ColorsBySlot = [new("e1b94f"), new("67c5df"), new("cb8edc"), new("87be69")];
    internal static Color ColorFor(Racer racer)
    {
        var peers = RaceSession.Current!.Engine.State.Racers.Values.ToList();
        int slot = peers.FindIndex(peer => peer.Id == racer.Id);
        var character = PublicProfiles.Read(racer)?.Character;
        if (character == null || character.MapDrawingColor.V < 0.2f) return ColorsBySlot[Math.Max(0, slot) % ColorsBySlot.Length];
        Color color = character.MapDrawingColor;
        int duplicates = peers.Take(slot).Count(peer => PublicProfiles.Read(peer)?.Character.Id == character.Id);
        return duplicates switch { 0 => color, 1 => color.Lightened(0.30f), 2 => color.Darkened(0.25f), _ => Color.FromHsv((color.H + 0.06f) % 1, 0.7f, 0.95f) };
    }
    internal static int Count => rows.Count;
    internal static void Install(CanvasLayer layer)
    {
        root = new VBoxContainer { Name = "FendaoRoster", Visible = false, CustomMinimumSize = new Vector2(275, 0), Theme = new Theme { DefaultFontSize = 20 } };
        root.AddThemeConstantOverride("separation", 6); layer.AddChild(root);
    }
    internal static void Tick()
    {
        if (root == null) return;
        RaceMapOverlay.Ensure(); RaceMapOverlay.Current?.Refresh();
        root.Visible = RaceRuntime.Live && NCapstoneContainer.Instance?.InUse != true && NRun.Instance?.GlobalUi.RelicInventory.IsVisibleInTree() == true
            && NRun.Instance.GlobalUi.RelicInventory.Modulate.A > 0.1f;
        if (!root.Visible) return;
        root.Position = new Vector2(18, EffectHud.Bottom + 10);
        RaceMapOverlay.Ensure(); RaceMapOverlay.Current?.Refresh();
    }
    internal static void Refresh()
    {
        if (root == null || RaceSession.Current is not { } session) return;
        if (sessionId != session.Engine.State.Id)
        {
            foreach (var node in root.GetChildren()) { root.RemoveChild(node); node.QueueFree(); }
            rows.Clear(); sessionId = session.Engine.State.Id;
            var actions = new Button { Text = L10n.T("比赛操作"), TooltipText = L10n.T("Boss额外奖励、净化与共同检查点"), CustomMinimumSize = new Vector2(275, 32) };
            actions.Pressed += RaceUi.TogglePanel; root.AddChild(actions);
        }
        int slot = 0;
        foreach (var racer in session.Engine.State.Racers.Values)
        {
            Color color = ColorFor(racer); slot++;
            if (racer.Id == session.LocalId) continue;
            if (!rows.TryGetValue(racer.Id, out var row))
            {
                string id = racer.Id;
                var button = new Button { CustomMinimumSize = new Vector2(275, 66), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
                var box = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; button.AddChild(box); box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                var portrait = new TextureRect { CustomMinimumSize = new Vector2(58, 58), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
                box.AddChild(portrait);
                var details = new VBoxContainer { CustomMinimumSize = new Vector2(202, 0), MouseFilter = Control.MouseFilterEnum.Ignore }; box.AddChild(details);
                var text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore }; details.AddChild(text);
                var hp = new ProgressBar { CustomMinimumSize = new Vector2(195, 23), ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
                hp.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("ad3434"), CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5, CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5 });
                details.AddChild(hp);
                var hpText = new Label { Name = "HpText", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore }; hp.AddChild(hpText); hpText.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                button.Pressed += () => RaceUi.Run(() => OpenPlayer(id)); root.AddChild(button);
                row = new(button, portrait, text, hp); rows[id] = row;
            }
            string state = racer.Eliminated ? L10n.T("已淘汰") : racer.Finished ? L10n.T("已完赛") : L10n.F($"第{racer.Act + 1}幕 · {racer.Floor}层");
            row.Text.Text = L10n.RacerName(racer) + "\n" + state; row.Text.Modulate = color;
            row.Hp.MaxValue = Math.Max(1, racer.MaxHealth); row.Hp.Value = racer.Health;
            row.Hp.GetNode<Label>("HpText").Text = $"{racer.Health}/{racer.MaxHealth}";
            row.Button.TooltipText = L10n.F($"{L10n.RacerName(racer)}\n{state} · {racer.Room}\n诅咒护盾 {racer.Shields} · 替死鬼 {racer.Ghosts}\n") + string.Join("、", racer.Effects.Values.Select(h => L10n.T(Catalog.Get(h.Id).Name) + (h.Grants.Count > 1 ? "+" + (h.Grants.Count - 1) : ""))) + L10n.T("\n点击查看原生牌组、遗物和药水详情。");
            if (racer.PublicPacket.Length > 0) row.Portrait.Texture = PublicProfiles.Read(racer)?.Character.IconTexture;
        }
        Tick();
    }
    internal static Task OpenPlayer(string id)
    {
        var racer = RaceSession.Current!.Engine.State.Racers[id];
        var player = PublicProfiles.Read(racer) ?? throw new InvalidOperationException(L10n.T("正在等待该玩家的公开资料，请稍后再试。"));
        var screen = NMultiplayerPlayerExpandedState.Create(player);
        NCapstoneContainer.Instance!.Open(screen);
        screen.GetNode<MegaRichTextLabel>("%PlayerNameLabel").Text = L10n.RacerName(racer) + $" · {racer.Health}/{racer.MaxHealth}";
        return Task.CompletedTask;
    }
}
