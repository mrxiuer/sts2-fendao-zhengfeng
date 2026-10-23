using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// Reflows the native allied creature nodes after the game's player/pet layout. Measurements come from
/// the controls the game actually uses for hitboxes, health bars, power icons and visual bounds.
/// </summary>
internal static class FriendlyFormation
{
    internal sealed record ComponentRect(NCreature Creature, string Part, Rect2 Bounds);

    private sealed class PlacedCreature
    {
        internal required NCreature Node { get; init; }
        internal required ComponentRect[] Components { get; init; }
        internal required Rect2 FootprintOffset { get; init; }
        internal required float RootScale { get; init; }
    }

    private sealed class MeasuredControls
    {
        internal Control[] Values = [];
    }

    private static readonly ConditionalWeakTable<Creature, object> observed = new();
    private static readonly ConditionalWeakTable<NCreature, MeasuredControls> measuredControls = new();
    private static readonly object observedGate = new();

    internal static void Reflow(IEnumerable<NCreature> creatureNodes)
    {
        if (!RaceRuntime.Live) return;
        try
        {
            NCreature[] nodes = creatureNodes
                .Where(IsUsableAlly)
                .GroupBy(node => node.Entity)
                .Select(group => group.First())
                .OrderBy(node => node.Entity.IsPlayer ? 0 : 1)
                .ThenBy(node => node.GetGlobalTransformWithCanvas().Origin.X)
                .ThenBy(node => node.Entity.Monster?.Title.GetFormattedText() ?? "")
                .ToArray();

            foreach (NCreature node in nodes)
                Observe(node.Entity);

            if (nodes.Length == 0 || nodes.Any(node => !node.IsInsideTree()))
                return;

            Viewport viewport = nodes[0].GetViewport();
            Rect2 visible = viewport.GetVisibleRect();
            Transform2D screenToCanvas = viewport.GetCanvasTransform().AffineInverse();
            PlacedCreature[] placed = nodes.Select(Measure).ToArray();
            float cameraScale = placed.Select(item => item.RootScale).Where(scale => scale > 0f).DefaultIfEmpty(1f).Average();

            // NCombatRoom's native layout uses a 150-unit outer inset and a 70-unit gap at the
            // player/enemy boundary. Convert those native values through the actual scene transform.
            float nativeGap = Math.Max(1f, 70f * cameraScale);
            float laneLeft = visible.Position.X + 150f * cameraScale;
            float laneRight = visible.Position.X + visible.Size.X * 0.5f - nativeGap;
            float laneWidth = laneRight - laneLeft;
            if (laneWidth <= 0f)
                return;

            PlacedCreature[][]? selectedRows = null;
            float selectedGap = nativeGap;
            float selectedTotalHeight = 0f;
            float[] gapCandidates = [nativeGap, nativeGap * 0.5f, nativeGap * 0.25f, 5f * cameraScale];

            // Prefer the fewest rows that fit at a useful native spacing. If the native 70-unit gap
            // cannot fit the four bodies, tighten the gap before moving a creature off-screen.
            for (int columns = placed.Length; columns >= 1 && selectedRows == null; columns--)
            {
                PlacedCreature[][] rows = placed.Chunk(columns).Select(row => row).ToArray();
                float[] rowWidths = rows.Select(row => row.Sum(item => item.FootprintOffset.Size.X)
                    + selectedGap * Math.Max(0, row.Length - 1)).ToArray();
                float[] heights = rows.Select(row => row.Max(item => item.FootprintOffset.Size.Y)).ToArray();
                for (int gapIndex = 0; gapIndex < gapCandidates.Length; gapIndex++)
                {
                    float gap = gapCandidates[gapIndex];
                    rowWidths = rows.Select(row => row.Sum(item => item.FootprintOffset.Size.X)
                        + gap * Math.Max(0, row.Length - 1)).ToArray();
                    heights = rows.Select(row => row.Max(item => item.FootprintOffset.Size.Y)).ToArray();
                    float totalHeight = heights.Sum() + gap * Math.Max(0, rows.Length - 1);
                    if (rowWidths.All(width => width <= laneWidth)
                        && totalHeight <= visible.Size.Y - 2f * nativeGap)
                    {
                        selectedRows = rows;
                        selectedGap = gap;
                        selectedTotalHeight = totalHeight;
                        break;
                    }
                }
            }

            if (selectedRows == null)
            {
                // Keep full model scales, including Giant. One column and the tightest native gap is
                // the safe fallback; the acceptance fixture reports any clipping or remaining overlap.
                selectedRows = placed.Chunk(1).Select(row => row).ToArray();
                selectedGap = 5f * cameraScale;
                selectedTotalHeight = selectedRows.Sum(row => row.Max(item => item.FootprintOffset.Size.Y))
                    + selectedGap * Math.Max(0, selectedRows.Length - 1);
            }

            // Anchor the whole formation to a stable battle ground band, rather
            // than feeding the previous row centres back into the next reflow.
            // Unequal row heights otherwise drift the group upward on each pass.
            float minTop = visible.Position.Y + nativeGap;
            float battleBottom = Math.Min(visible.End.Y - nativeGap,
                visible.Position.Y + visible.Size.Y * 0.76f);
            float top = Math.Max(minTop, battleBottom - selectedTotalHeight);

            float rowTop = top;
            foreach (PlacedCreature[] row in selectedRows)
            {
                float rowHeight = row.Max(item => item.FootprintOffset.Size.Y);
                float rowWidth = row.Sum(item => item.FootprintOffset.Size.X) + selectedGap * Math.Max(0, row.Length - 1);
                float cursorX = visible.Position.X + 150f * cameraScale + Math.Max(0f, (laneWidth - rowWidth) * 0.5f);
                foreach (PlacedCreature item in row)
                {
                    float targetRootX = cursorX - item.FootprintOffset.Position.X;
                    float targetRootY = rowTop - item.FootprintOffset.Position.Y;
                    item.Node.GlobalPosition = screenToCanvas * new Vector2(targetRootX, targetRootY);
                    cursorX += item.FootprintOffset.Size.X + selectedGap;
                }

                rowTop += rowHeight + selectedGap;
            }
        }
        catch (Exception ex)
        {
            GD.PushError("Friendly formation could not place allied creatures: " + ex);
        }
    }

    internal static ComponentRect[] MeasureComponents(NCreature node)
    {
        if (!GodotObject.IsInstanceValid(node))
            return [];

        var controls = new List<(Control Control, string Part)>();
        if (GodotObject.IsInstanceValid(node.Hitbox))
            controls.Add((node.Hitbox, "hitbox"));
        if (GodotObject.IsInstanceValid(node.Visuals)
            && GodotObject.IsInstanceValid(node.Visuals.Bounds))
            controls.Add((node.Visuals.Bounds, "visual-bounds"));

        foreach (Node child in node.FindChildren("*", "", true, false))
        {
            if (child is not Control control || !control.IsVisibleInTree())
                continue;

            string? part = control switch
            {
                NHealthBar => "health-bar",
                NPower => "status-row",
                NPowerContainer => "status-container",
                Label => "status-text",
                TextureRect => "status-icon",
                TextureButton => "status-button",
                ProgressBar => "status-meter",
                NinePatchRect => "status-panel",
                ColorRect => "status-panel",
                _ => null
            };
            if (part != null && controls.All(existing => existing.Control != control))
                controls.Add((control, part));
        }

        measuredControls.GetValue(node, static _ => new MeasuredControls()).Values = controls
            .Select(pair => pair.Control)
            .Distinct()
            .ToArray();

        var result = controls
            .Where(pair => pair.Control.IsVisibleInTree())
            .Select(pair => new ComponentRect(node, pair.Part, ScreenRect(pair.Control)))
            .Where(component => component.Bounds.Size.X > 0f && component.Bounds.Size.Y > 0f)
            .ToList();
        // Native NHealthBar is a zero-sized layout root; its visible children
        // contain the actual HP strip, labels and status ornaments.
        foreach (var health in controls.Select(item => item.Control).OfType<NHealthBar>())
        {
            var rectangles = health.FindChildren("*", "", true, false).OfType<Control>()
                .Where(control => control.IsVisibleInTree())
                .Select(ScreenRect).Where(rect => rect.Size.X > 0 && rect.Size.Y > 0).ToArray();
            if (rectangles.Length > 0)
                result.Add(new ComponentRect(node, "health-bar", Enclose(rectangles)));
        }
        return result.ToArray();
    }

    internal static int SizeSignature(NCombatRoom room)
    {
        HashCode hash = new();
        Rect2 visible = room.GetViewport().GetVisibleRect();
        AddSize(ref hash, visible.Position);
        AddSize(ref hash, visible.Size);

        foreach (NCreature node in room.CreatureNodes)
        {
            if (!IsUsableAlly(node))
                continue;

            hash.Add(node.GetInstanceId());
            hash.Add(node.Entity.IsAlive);
            AddSize(ref hash, node.Visuals.Bounds.Size);
            AddSize(ref hash, node.Hitbox.Size);
            AddScale(ref hash, node.Visuals.DefaultScale);
            AddScale(ref hash, node.Visuals.Scale.X);
            AddScale(ref hash, node.Visuals.Scale.Y);
            Transform2D nodeTransform = node.GetGlobalTransformWithCanvas();
            AddScale(ref hash, nodeTransform.X.Length());
            AddScale(ref hash, nodeTransform.Y.Length());

            // Reuse controls discovered during the last real layout pass. This catches native
            // health/status row size changes without running FindChildren from the frame loop.
            if (!measuredControls.TryGetValue(node, out MeasuredControls? cache))
                continue;
            foreach (Control control in cache.Values)
            {
                if (!GodotObject.IsInstanceValid(control)) continue;
                hash.Add(control.IsVisibleInTree());
                if (control is NPowerContainer) hash.Add(control.GetChildCount());
                if (!control.IsVisibleInTree()) continue;
                Rect2 rect = ScreenRect(control);
                AddSize(ref hash, rect.Size);
                AddSize(ref hash, rect.Position - nodeTransform.Origin);
            }
        }

        return hash.ToHashCode();
    }

    internal static void AttachSizeObserver(NCombatRoom room)
    {
        if (!GodotObject.IsInstanceValid(room)
            || !room.IsInsideTree()
            || room.GetNodeOrNull<Node>("FriendlyFormationMonitor") != null)
            return;

        FriendlyFormationMonitor monitor = new() { Room = room };
        monitor.Name = "FriendlyFormationMonitor";
        room.AddChild(monitor);
        monitor.SetProcess(true);
    }

    internal static bool VerifyNoOverlap(IEnumerable<NCreature> creatureNodes, out string detail)
    {
        NCreature[] nodes = creatureNodes.Where(IsUsableAlly).DistinctBy(node => node.Entity).ToArray();
        Viewport viewport = nodes.FirstOrDefault()?.GetViewport() ?? NGame.Instance?.GetViewport()
            ?? throw new InvalidOperationException("Formation verification needs a live viewport.");
        Rect2 visible = viewport.GetVisibleRect();
        float scale = nodes.Select(node => node.GetGlobalTransformWithCanvas().X.Length())
            .Where(value => value > 0f).DefaultIfEmpty(1f).Average();
        float enemyBoundary = visible.Position.X + visible.Size.X * 0.5f - 70f * scale;
        ComponentRect[] components = nodes.SelectMany(MeasureComponents).ToArray();

        foreach (ComponentRect component in components)
        {
            Rect2 rect = component.Bounds;
            if (rect.Position.X < visible.Position.X
                || rect.Position.Y < visible.Position.Y
                || rect.End.X > visible.End.X
                || rect.End.Y > visible.End.Y)
            {
                detail = $"{component.Creature.Entity.Name} {component.Part} is clipped by the viewport: {Format(rect)}";
                return false;
            }

            if (rect.End.X > enemyBoundary)
            {
                detail = $"{component.Creature.Entity.Name} {component.Part} crosses the native player/enemy boundary: {Format(rect)}";
                return false;
            }
        }

        for (int i = 0; i < components.Length; i++)
        {
            for (int j = i + 1; j < components.Length; j++)
            {
                ComponentRect left = components[i];
                ComponentRect right = components[j];
                if (left.Creature.Entity == right.Creature.Entity || !left.Bounds.Intersects(right.Bounds))
                    continue;

                detail = $"{left.Creature.Entity.Name} {left.Part} overlaps {right.Creature.Entity.Name} {right.Part}: "
                    + $"{Format(left.Bounds)} / {Format(right.Bounds)}";
                return false;
            }
        }

        detail = $"no overlap among {nodes.Length} allied nodes and {components.Length} visible body/UI rectangles";
        return true;
    }

    private static PlacedCreature Measure(NCreature node)
    {
        ComponentRect[] components = MeasureComponents(node);
        if (components.Length == 0)
            throw new InvalidOperationException($"No visible native bounds found for {node.Entity.Name}.");

        Rect2 footprint = Enclose(components.Select(component => component.Bounds));
        Vector2 root = node.GetGlobalTransformWithCanvas().Origin;
        Rect2 offset = new(footprint.Position - root, footprint.Size);
        return new PlacedCreature
        {
            Node = node,
            Components = components,
            FootprintOffset = offset,
            RootScale = node.GetGlobalTransformWithCanvas().X.Length()
        };
    }

    private static bool IsUsableAlly(NCreature node)
        => GodotObject.IsInstanceValid(node)
           && node.Entity is not null
           && (node.Entity.IsPlayer || node.Entity.PetOwner != null)
           && node.Entity.IsAlive
           && node.IsVisibleInTree()
           && GodotObject.IsInstanceValid(node.Visuals)
           && GodotObject.IsInstanceValid(node.Hitbox);

    private static Rect2 ScreenRect(Control control)
    {
        Transform2D transform = control.GetGlobalTransformWithCanvas();
        Vector2 a = transform * Vector2.Zero;
        Vector2 b = transform * new Vector2(control.Size.X, 0f);
        Vector2 c = transform * new Vector2(0f, control.Size.Y);
        Vector2 d = transform * control.Size;
        float left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        float top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        float right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        float bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static Rect2 Enclose(IEnumerable<Rect2> rectangles)
    {
        Rect2[] values = rectangles.ToArray();
        if (values.Length == 0)
            return new Rect2();

        float left = values.Min(rect => rect.Position.X);
        float top = values.Min(rect => rect.Position.Y);
        float right = values.Max(rect => rect.End.X);
        float bottom = values.Max(rect => rect.End.Y);
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static string Format(Rect2 rect)
        => $"({rect.Position.X:0.0},{rect.Position.Y:0.0},{rect.Size.X:0.0},{rect.Size.Y:0.0})";

    private static void AddSize(ref HashCode hash, Vector2 size)
    {
        AddScale(ref hash, size.X);
        AddScale(ref hash, size.Y);
    }

    private static void AddScale(ref HashCode hash, float value)
        => hash.Add(MathF.Round(value * 20f) / 20f);

    private static void Observe(Creature creature)
    {
        lock (observedGate)
        {
            if (observed.TryGetValue(creature, out _))
                return;
            observed.Add(creature, new object());
        }

        creature.Died += OnDied;
        creature.Revived += OnRevived;
    }

    private static void OnDied(Creature creature)
    {
        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
        if (node == null)
        {
            QueueReflow();
            return;
        }
        _ = ReflowAfterDeath(node);
    }

    private static async Task ReflowAfterDeath(NCreature node)
    {
        try
        {
            if (node.DeathAnimationTask is { } death)
                await death;
        }
        catch (Exception ex)
        {
            GD.PushError("Friendly formation death wait failed: " + ex);
        }
        await Task.Delay(25);
        QueueReflow();
    }

    private static void OnRevived(Creature _) => QueueReflow();

    internal static void QueueReflow()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return;

        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(room) && NCombatRoom.Instance == room)
                Reflow(room.CreatureNodes);
        }).CallDeferred();
    }
}

internal sealed partial class FriendlyFormationMonitor : Node
{
    internal NCombatRoom? Room { get; set; }

    private int? lastSignature;
    private double stableSeconds;
    private bool sizeChanged;
    private SceneTree? tree;
    internal FriendlyFormationMonitor()
    {
        TreeEntered += () => { tree = GetTree(); tree.ProcessFrame += Frame; };
        TreeExiting += () => { if (GodotObject.IsInstanceValid(tree)) tree!.ProcessFrame -= Frame; tree = null; };
    }
    private void Frame() => Step(GetProcessDeltaTime());
    private void Step(double delta)
    {
        if (!RaceRuntime.Live || Room == null || !GodotObject.IsInstanceValid(Room) || !Room.IsInsideTree())
        {
            QueueFree();
            return;
        }

        int signature = FriendlyFormation.SizeSignature(Room);
        if (lastSignature == null)
        {
            lastSignature = signature;
            return;
        }

        if (lastSignature.Value != signature)
        {
            lastSignature = signature;
            stableSeconds = 0d;
            sizeChanged = true;
            return;
        }

        if (!sizeChanged)
            return;

        stableSeconds += Math.Max(0d, delta);
        if (stableSeconds < 0.15d)
            return;

        sizeChanged = false;
        stableSeconds = 0d;
        FriendlyFormation.QueueReflow();
    }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.PositionPlayersAndPets))]
[HarmonyPriority(Priority.Last)]
internal static class FriendlyFormationNativePositionPatch
{
    static void Postfix(List<NCreature> creatureNodes)
    {
        if (NCombatRoom.Instance is { } room)
            FriendlyFormation.AttachSizeObserver(room);
        FriendlyFormation.Reflow(creatureNodes);
    }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
[HarmonyPriority(Priority.Last)]
internal static class FriendlyFormationAddCreaturePatch
{
    static void Postfix(NCombatRoom __instance, Creature creature)
    {
        if (creature != null)
        {
            // Event hooks make the same arrangement run after companion death and revival.
            var node = __instance.GetCreatureNode(creature);
            if (node != null)
            {
                FriendlyFormation.AttachSizeObserver(__instance);
                FriendlyFormation.Reflow(__instance.CreatureNodes);
            }
        }
    }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.RemoveCreatureNode))]
[HarmonyPriority(Priority.Last)]
internal static class FriendlyFormationRemoveCreaturePatch
{
    static void Postfix(NCombatRoom __instance)
        => FriendlyFormation.QueueReflow();
}


