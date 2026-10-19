using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace FendaoZhengfeng.Mod;

internal partial class RaceMapOverlay : Control
{
    private NMapScreen map = null!;
    private Dictionary<MapCoord, NMapPoint> points = [];
    private Control inkLayer = null!;
    private Texture2D completedInk = null!;
    internal int DrawnMarks { get; private set; }
    internal static RaceMapOverlay? Current { get; private set; }
    internal static void Ensure()
    {
        var screen = NMapScreen.Instance;
        if (screen == null || !screen.IsOpen || !RaceRuntime.Live) return;
        if (Current != null && GodotObject.IsInstanceValid(Current) && Current.IsInsideTree() && !Current.IsQueuedForDeletion() && Current.map == screen) return;
        var points = (Dictionary<MapCoord, NMapPoint>)AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary").GetValue(screen)!;
        var parent = (Control)AccessTools.Field(typeof(NMapScreen), "_points").GetValue(screen)!;
        Current = new RaceMapOverlay { Name = "FendaoPaths", map = screen, points = points, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 4, ProcessMode = ProcessModeEnum.Always, Size = new Vector2(4000, 8000), Position = new Vector2(-2000, -4000) };
        var overlay = Current;
        overlay.Draw += overlay.Render;
        overlay.completedInk = ResourceLoader.Load<Texture2D>("res://images/atlases/compressed.sprites/map/map_circle_4.tres");
        var shader = new Shader { Code = "shader_type canvas_item; render_mode unshaded; varying vec4 player_color; void vertex(){player_color=COLOR;} void fragment(){ float ink = texture(TEXTURE, UV).a; COLOR = vec4(player_color.rgb, player_color.a * ink); }" };
        overlay.inkLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Size = overlay.Size, Material = new ShaderMaterial { Shader = shader } };
        overlay.inkLayer.Draw += overlay.RenderClears;
        overlay.AddChild(overlay.inkLayer);
        overlay.TreeExiting += () => { if (Current == overlay) Current = null; };
        parent.AddChild(overlay); overlay.QueueRedraw();
    }
    internal string Diagnostic => $"open={map.IsOpen} visible={IsVisibleInTree()} parent={GetParent()?.Name} points={points.Count} marks={DrawnMarks}";
    internal void Refresh()
    {
        Visible = RaceRuntime.Live && map.IsOpen;
        if (Visible) { QueueRedraw(); inkLayer.QueueRedraw(); }
    }
    private Vector2? PositionOf(RouteStep point)
    {
        if (RaceRuntime.State?.CurrentActIndex != point.Act) return null;
        var match = points.FirstOrDefault(pair => pair.Key.row == point.Row && pair.Key.col == point.Col).Value;
        if (match == null || !GodotObject.IsInstanceValid(match)) return null;
        var icon = match.GetNodeOrNull<Control>("%Icon");
        Vector2 center = icon != null
            ? icon.GetGlobalTransform() * (icon.Size * 0.5f)
            : match.GetGlobalTransform() * (match.Size * 0.5f);
        return GetGlobalTransform().AffineInverse() * center;
    }
    private void Render()
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session) return;
        int slot = 0;
        foreach (var racer in session.Engine.State.Racers.Values)
        {
            var color = RaceRoster.ColorFor(racer);
            var offset = new Vector2((slot - 1.5f) * 6, 0); slot++;
            if (racer.Position is { } location && PositionOf(location) is { } current)
            {
                current += offset + new Vector2(0, -22 - slot * 3);
                DrawCircle(current, 15, new Color("15222b")); DrawArc(current, 15, 0, Mathf.Tau, 32, color, 3, true);
                DrawString(ThemeDB.FallbackFont, current + new Vector2(-6, 7), slot.ToString(), fontSize: 20, modulate: color);
                DrawString(ThemeDB.FallbackFont, current + new Vector2(20, 6), racer.Name, fontSize: 18, modulate: color);
            }
        }
    }
    private void RenderClears()
    {
        DrawnMarks = 0;
        if (!RaceRuntime.Live || RaceSession.Current is not { } session) return;
        var peers = session.Engine.State.Racers.Values.ToList();
        foreach (var node in peers.SelectMany(peer => peer.ClearedNodes).Distinct())
        {
            if (PositionOf(node) is not { } position) continue;
            var winners = peers.Where(peer => peer.ClearedNodes.Contains(node)).ToList();
            for (int i = 0; i < winners.Count; i++)
            {
                float size = winners.Count == 1 ? 94 : 60;
                var offset = winners.Count == 1 ? Vector2.Zero : new Vector2((i - (winners.Count - 1) * 0.5f) * 49, 35);
                inkLayer.DrawTextureRect(completedInk, new Rect2(position + offset - Vector2.One * size * 0.5f, Vector2.One * size), false, RaceRoster.ColorFor(winners[i]));
                DrawnMarks++;
            }
        }
    }

}
