using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace FendaoZhengfeng.Mod;

/// <summary>Visual-only motion layered onto the existing native creature model.</summary>
internal static class BurrowVisuals
{
    private static BurrowMotion? Find(Creature creature, bool create)
    {
        var native = NCombatRoom.Instance?.GetCreatureNode(creature);
        if (native == null || !GodotObject.IsInstanceValid(native)) return null;
        var motion = native.GetNodeOrNull<BurrowMotion>("BurrowMotion");
        if (motion != null || !create) return motion;
        motion = new BurrowMotion { Name = "BurrowMotion", Creature = native };
        native.AddChild(motion);
        return motion;
    }

    private static void Each(Creature creature, bool create, Action<BurrowMotion> action)
    {
        foreach (var body in TwinSlimeRuntime.BodiesOf(creature))
            if (Find(body, create) is { } motion) action(motion);
    }
    internal static void Bury(Creature creature) => Each(creature, true, motion => motion.Bury());
    internal static void Emerge(Creature creature, bool stunned) => Each(creature, true, motion => motion.Emerge(stunned));
    internal static void Restore(Creature? creature)
    {
        if (creature != null) Each(creature, false, motion => motion.Restore());
    }
    internal static void SnapBuried(Creature creature) => Each(creature, true, motion => motion.SnapBuried());
    internal static void EnsureDazed(Creature creature) => Each(creature, true, motion => motion.EnsureDazed());
    internal static void ClearDaze(Creature creature) => Each(creature, false, motion => motion.ClearDaze());
    internal static void ShieldBroken(Creature creature) => Each(creature, true, motion => motion.ShieldBroken());

    internal static bool HasMotion(Creature creature) => Find(creature, create: false) != null;
    internal static bool IsBuried(Creature creature) => Find(creature, create: false)?.IsBuried == true;
    internal static bool IsDazed(Creature creature) => Find(creature, create: false)?.IsDazed == true;
    internal static float BodyOffset(Creature creature) => Find(creature, create: false)?.BodyOffset ?? 0;
    internal static float BodyOpacity(Creature creature) => Find(creature, create: false)?.BodyOpacity ?? 1;
}

internal partial class BurrowMotion : Node
{
    private const float SinkDistance = 132f;
    private const float BuriedOpacity = 0.16f;
    private Node2D? body;
    private Vector2 basePosition;
    private Color baseModulate = Colors.White;
    private Tween? tween;
    private BurrowDaze? daze;
    private bool cached;

    internal NCreature Creature { get; set; } = null!;
    internal bool IsBuried { get; private set; }
    internal bool IsDazed => GodotObject.IsInstanceValid(daze);
    internal float BodyOffset => GodotObject.IsInstanceValid(body) ? body!.Position.Y - basePosition.Y : 0;
    internal float BodyOpacity => GodotObject.IsInstanceValid(body) ? body!.Modulate.A : 1;

    internal void Bury()
    {
        if (!CacheBody()) return;
        ClearDaze();
        KillTween();
        IsBuried = true;
        EmitDust(16, 31);
        var target = basePosition + new Vector2(0, SinkDistance);
        tween = CreateTween().SetParallel();
        tween.TweenProperty(body, "position", target, 0.42)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.TweenProperty(body, "modulate:a", baseModulate.A * BuriedOpacity, 0.34)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.Finished += () => { if (IsBuried) EmitDust(13, 43); };
    }

    internal void SnapBuried()
    {
        if (!CacheBody()) return;
        KillTween();
        IsBuried = true;
        body!.Position = basePosition + new Vector2(0, SinkDistance);
        body.Modulate = WithAlpha(baseModulate, baseModulate.A * BuriedOpacity);
    }

    internal void Emerge(bool stunned)
    {
        if (!CacheBody()) return;
        KillTween();
        if (!IsBuried) SnapBuried();
        IsBuried = false;
        EmitDust(20, stunned ? 73 : 59);
        tween = CreateTween().SetParallel();
        tween.TweenProperty(body, "position", basePosition, stunned ? 0.39 : 0.34)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(body, "modulate", baseModulate, stunned ? 0.33 : 0.29)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        if (stunned) tween.Finished += EnsureDazed;
        else ClearDaze();
    }

    internal void ShieldBroken()
    {
        if (!IsBuried) return;
        EmitDust(12, 97);
    }

    internal void EnsureDazed()
    {
        if (!GodotObject.IsInstanceValid(Creature) || IsDazed) return;
        daze = new BurrowDaze { Name = "BurrowDaze" };
        Creature.AddChild(daze);
    }

    internal void ClearDaze()
    {
        if (GodotObject.IsInstanceValid(daze)) daze!.QueueFree();
        daze = null;
    }

    internal void Restore()
    {
        KillTween();
        ClearDaze();
        IsBuried = false;
        if (CacheBody())
        {
            body!.Position = basePosition;
            body.Modulate = baseModulate;
        }
    }

    private bool CacheBody()
    {
        if (!GodotObject.IsInstanceValid(Creature) || Creature.Visuals == null
            || !GodotObject.IsInstanceValid(Creature.Visuals)) return false;
        var current = Creature.Visuals.Body;
        if (!GodotObject.IsInstanceValid(current)) return false;
        if (cached && body == current) return true;
        body = current;
        basePosition = body.Position;
        baseModulate = body.Modulate;
        cached = true;
        return true;
    }

    private void EmitDust(int count, int seed)
    {
        if (!GodotObject.IsInstanceValid(Creature)) return;
        Creature.AddChild(new BurrowDust { Name = "BurrowDust", Count = count, Seed = seed });
    }

    private void KillTween()
    {
        if (GodotObject.IsInstanceValid(tween)) tween!.Kill();
        tween = null;
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.R, color.G, color.B, alpha);
}

internal partial class BurrowDust : Node2D
{
    private readonly record struct Speck(Vector2 Velocity, float Radius, Color Color);
    private Speck[] specks = [];
    private SceneTree? tree;
    private float age;
    private bool initialized;
    internal int Count { get; set; } = 16;
    internal int Seed { get; set; } = 31;

    public BurrowDust()
    {
        Draw += Render;
        TreeEntered += OnTreeEntered;
        TreeExiting += OnTreeExiting;
    }

    private void OnTreeEntered()
    {
        tree = GetTree();
        tree.ProcessFrame += Frame;
        Initialize();
        Frame();
    }

    private void OnTreeExiting()
    {
        if (GodotObject.IsInstanceValid(tree))
            tree!.ProcessFrame -= Frame;
        tree = null;
    }

    private void Initialize()
    {
        if (initialized) return;
        initialized = true;
        ZIndex = 12;
        var random = new Random(Seed);
        specks = Enumerable.Range(0, Math.Clamp(Count, 1, 40)).Select(_ =>
        {
            float x = (float)(random.NextDouble() * 2 - 1);
            float y = (float)random.NextDouble();
            float radius = 2.2f + (float)random.NextDouble() * 4.2f;
            var color = random.Next(3) switch
            {
                0 => new Color("b78a59"),
                1 => new Color("8e6848"),
                _ => new Color("d2aa71")
            };
            return new Speck(new Vector2(x * (70 + (float)random.NextDouble() * 65), -35 - y * 105), radius, color);
        }).ToArray();
    }

    private void Frame()
    {
        age += (float)GetProcessDeltaTime();
        if (age >= 0.82f) { QueueFree(); return; }
        QueueRedraw();
    }

    private void Render()
    {
        float progress = Mathf.Clamp(age / 0.82f, 0, 1);
        float alpha = 1 - progress;
        for (int i = 0; i < specks.Length; i++)
        {
            var speck = specks[i];
            float lag = i % 5 * 0.022f;
            float time = Math.Max(0, age - lag);
            var position = new Vector2(0, -8) + speck.Velocity * time + new Vector2(0, 145 * time * time);
            DrawCircle(position, speck.Radius * (1 - progress * 0.28f), WithAlpha(speck.Color, alpha));
        }
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.R, color.G, color.B, alpha * color.A);
}

internal partial class BurrowDaze : Node2D
{
    private SceneTree? tree;
    private float age;

    public BurrowDaze()
    {
        Draw += Render;
        TreeEntered += OnTreeEntered;
        TreeExiting += OnTreeExiting;
    }

    private void OnTreeEntered()
    {
        ZIndex = 14;
        tree = GetTree();
        tree.ProcessFrame += Frame;
        Frame();
    }

    private void OnTreeExiting()
    {
        if (GodotObject.IsInstanceValid(tree))
            tree!.ProcessFrame -= Frame;
        tree = null;
    }

    private void Frame()
    {
        age += (float)GetProcessDeltaTime();
        QueueRedraw();
    }

    private void Render()
    {
        var gold = new Color("f6db89");
        var pale = new Color("fff1be");
        for (int i = 0; i < 3; i++)
        {
            float phase = age * 2.5f + i * Mathf.Tau / 3;
            var center = new Vector2(Mathf.Cos(phase) * 31, -142 + Mathf.Sin(phase * 1.35f) * 7);
            DrawLine(center + new Vector2(-5, -5), center + new Vector2(5, 5), gold, 3, true);
            DrawLine(center + new Vector2(-5, 5), center + new Vector2(5, -5), pale, 3, true);
            DrawCircle(center, 3.2f, gold);
        }
    }
}