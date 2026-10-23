using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace FendaoZhengfeng.Mod;

internal static class NativeAcceptanceVisuals
{
    internal static async Task Capture(string name, bool clearHover = true)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var game = NGame.Instance ?? throw new InvalidOperationException("实际截图缺少游戏节点。");
        if (clearHover)
        {
            Input.WarpMouse(new Vector2(game.GetViewport().GetVisibleRect().End.X - 30, 80));
            MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.Clear();
        }
        await Task.Delay(650);
        Directory.CreateDirectory(directory);
        using var frame = game.GetViewport().GetTexture().GetImage();
        if (frame.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
            throw new IOException("实际运行截图保存失败：" + name);
    }
    internal static async Task CaptureCard(string name, CardModel card)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR"))) return;
        // Let the turn banner and hand entry animation finish, then use the
        // native holder focus path so the real card text is fully readable.
        await Task.Delay(2000);
        var node = NCard.FindOnTable(card) ?? throw new InvalidOperationException("实际预览未找到手牌节点。");
        Node? parent = node.GetParent();
        while (parent != null && parent is not NHandCardHolder) parent = parent.GetParent();
        if (parent is not NHandCardHolder holder) throw new InvalidOperationException("实际预览缺少原生手牌容器。");
        holder.Call(NHandCardHolder.MethodName.OnFocus);
        try { await Capture(name, clearHover: false); }
        finally { if (GodotObject.IsInstanceValid(holder)) holder.Call(NHandCardHolder.MethodName.OnUnfocus); }
    }

}
