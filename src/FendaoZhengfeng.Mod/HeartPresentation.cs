using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace FendaoZhengfeng.Mod;

internal static class HeartPresentation
{
    private static readonly Dictionary<string, Texture2D> textures = [];
    internal static readonly Dictionary<string, string> Texts = new()
    {
        ["heart.name"] = "高塔之心", ["heart.ward.title"] = "高塔不灭", ["heart.beat.title"] = "死亡律动", ["heart.copy.title"] = "众生回响", ["heart.stabs.title"] = "痛苦回响",
        ["heart.copy.intent"] = "复制另一名仍在比赛玩家的永久牌组快照（保留升级），生成你自己的临时战斗副本。第5、6回合使用，第7回合抽牌前恢复原牌堆；对方不受影响。人工制品可以抵挡。"
    };
    internal static Texture2D Texture(string name)
    {
        if (textures.TryGetValue(name, out var texture)) return texture;
        using var resource = typeof(HeartPresentation).Assembly.GetManifestResourceStream("FendaoZhengfeng.Heart." + name + ".png") ?? throw new IOException("缺少心脏资源：" + name);
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        var image = new Image(); if (image.LoadPngFromBuffer(bytes.ToArray()) != Error.Ok) throw new IOException("心脏图片损坏。");
        textures[name] = texture = ImageTexture.CreateFromImage(image); return texture;
    }
    internal static NCreatureVisuals CreateVisuals()
    {
        var root = new NCreatureVisuals { Name = "TowerHeartVisuals" };
        void Add(Node child) { root.AddChild(child); child.Owner = root; child.UniqueNameInOwner = true; }
        var body = new Node2D { Name = "Visuals" }; Add(body);
        var sprite = new Sprite2D { Name = "HeartSprite", Texture = Texture("heart"), Position = new Vector2(0, -290), Scale = Vector2.One * 0.43f };
        body.AddChild(sprite);
        body.AddChild(new HeartMotion { Sprite = sprite });
        Add(new Control { Name = "Bounds", Position = new Vector2(-225, -520), Size = new Vector2(450, 500), MouseFilter = Control.MouseFilterEnum.Ignore });
        Add(new Marker2D { Name = "IntentPos", Position = new Vector2(0, -545) });
        Add(new Marker2D { Name = "CenterPos", Position = new Vector2(0, -285) });
        Add(new Marker2D { Name = "TalkPos", Position = new Vector2(180, -400) });
        return root;
    }
    internal static Texture2D PowerTexture(HeartPower power) => power.HeartKey switch { "ward" => EffectIcons.Get("hard_shell"), "copy" => EffectIcons.Get("burn_bridges"), "stabs" => EffectIcons.Get("barbs"), _ => Texture("heart") };
    internal static HeartMotion? Motion(Creature creature) => NCombatRoom.Instance?.GetCreatureNode(creature)?.Visuals.Body.GetChildren().OfType<HeartMotion>().FirstOrDefault();
    internal static void Install(TowerHeart heart) { var motion = Motion(heart.Creature); if (motion != null) motion.Heart = heart; }
    internal static Task Intro(TowerHeart heart)
    {
        Install(heart);
        Motion(heart.Creature)?.Intro();
        // Visual-only: combat is immediately playable and no intro wait affects race ranking.
        return Task.CompletedTask;
    }
    internal static Task Attack(TowerHeart heart, bool heavy) { var motion = Motion(heart.Creature); motion?.CancelIntro(); motion?.Pulse(heavy ? 1 : 0.6f); return Task.CompletedTask; }
    internal static void Beat(Creature heart) => Motion(heart)?.Pulse(0.18f);
    internal static Task Overload(TowerHeart heart, int number) { var motion = Motion(heart.Creature); if (motion != null) { motion.Overloaded = number >= 2; motion.Pulse(1); } return Task.CompletedTask; }
    internal static void CopyNotice(string donor) => RaceUi.Notify("众生回响：复制“" + donor + "”的牌组，第5、6回合使用，第7回合恢复。");
    internal static HeartDeathVisual? CaptureDeath(TowerHeart heart)
        => Motion(heart.Creature) is { } motion ? new(motion.GlobalTransform, motion.Sprite.Transform, motion) : null;
    internal static void PlayDeath(HeartDeathVisual? snapshot)
    {
        if (snapshot == null || NCombatRoom.Instance is not { } room) return;
        if (GodotObject.IsInstanceValid(snapshot.Source)) snapshot.Source.HideForDeath();
        // Native death removes the creature node. Keep the visual-only outro in its own stage.
        var stage = new Node2D { Name = "TowerHeartOutro" }; room.AddChild(stage); stage.GlobalTransform = snapshot.GlobalTransform;
        var sprite = new Sprite2D { Texture = Texture("heart"), Transform = snapshot.SpriteTransform };
        var motion = new HeartMotion { Sprite = sprite }; stage.AddChild(motion); motion.AddChild(sprite); motion.Die();
        room.GetTree().CreateTimer(5).Timeout += () => { if (GodotObject.IsInstanceValid(stage)) stage.QueueFree(); };
    }
}

internal sealed record HeartDeathVisual(Transform2D GlobalTransform, Transform2D SpriteTransform, HeartMotion Source);

// Native descriptions may call SmartFormat directly (including nested monster titles).
[HarmonyPatch(typeof(LocManager), nameof(LocManager.GetTable))]
internal static class HeartDirectLocalizationPatch
{
    private static readonly LocTable table = new(ModEntry.Id, HeartPresentation.Texts);
    static bool Prefix(string __0, ref LocTable __result)
    {
        if (__0 != ModEntry.Id) return true;
        __result = table;
        return false;
    }
}

internal sealed class HeartCopyIntent : DebuffIntent { }

[HarmonyPatch(typeof(AbstractIntent), nameof(AbstractIntent.GetHoverTip))]
internal static class HeartCopyIntentTipPatch
{
    static bool Prefix(AbstractIntent __instance, ref HoverTip __result)
    {
        if (__instance is not HeartCopyIntent) return true;
        __result = new HoverTip(new LocString(ModEntry.Id, "heart.copy.title"), new LocString(ModEntry.Id, "heart.copy.intent"), HeartPresentation.Texture("heart"));
        return false;
    }
}

internal partial class HeartMotion : Node2D
{
    private SceneTree? frameTree;
    public HeartMotion()
    {
        Draw += Render;
        TreeEntered += () => { frameTree = GetTree(); frameTree.ProcessFrame += Frame; };
        TreeExiting += () => { if (GodotObject.IsInstanceValid(frameTree)) frameTree!.ProcessFrame -= Frame; frameTree = null; };
    }
    private void Frame() => Animate(GetProcessDeltaTime());
    internal Sprite2D Sprite { get; set; } = null!;
    internal TowerHeart? Heart { get; set; }
    internal bool Overloaded { get; set; }
    private double elapsed;
    private float pulse;
    private bool dead;
    private Label? banner;
    private float intro;
    private float beatWave;
    private int beatNumber = -1;
    private Tween? introTween;
    private Button? introSkip;
    private void Animate(double delta)
    {
        elapsed += delta;
        if (dead || !GodotObject.IsInstanceValid(Sprite)) return;
        double period = Overloaded ? 0.85 : 1.3;
        int cycle = (int)(elapsed / period);
        float phase = (float)(elapsed / period - cycle);
        beatWave = Mathf.Exp(-Mathf.Pow((phase - 0.12f) / 0.10f, 2)) + 0.65f * Mathf.Exp(-Mathf.Pow((phase - 0.34f) / 0.10f, 2));
        if (cycle != beatNumber) { beatNumber = cycle; HeartAudio.Play(this, 0); }
        pulse = Math.Max(0, pulse - (float)delta * 2);
        Sprite.Scale = new Vector2(0.43f + beatWave * 0.036f + pulse * 0.022f, 0.43f - beatWave * 0.020f + pulse * 0.012f);
        Sprite.Position = new Vector2(Mathf.Sin((float)elapsed * 0.8f) * 3, -290 + beatWave * 7 + Mathf.Sin((float)elapsed * 1.3f) * 3);
        Sprite.SelfModulate = new Color(1 + beatWave * 0.18f + pulse * 0.14f, 1 - beatWave * 0.10f - (Overloaded ? 0.12f : 0), 1 - beatWave * 0.16f - (Overloaded ? 0.1f : 0));
        if (intro > 0) { intro -= (float)delta; if (banner != null) banner.Modulate = new Color(1, 1, 1, Mathf.Clamp(intro, 0, 1)); }
        QueueRedraw();
    }
    internal void Intro()
    {
        intro = 8;
        Flash(new Color(1, 0.55f, 0.22f, 0.24f));
        Sprite.Modulate = new Color(0.3f, 0.3f, 0.3f, 0.15f);
        introTween = CreateTween(); introTween.TweenProperty(Sprite, "modulate", Colors.White, 2.5);
        banner = new Label { Text = "高塔之心\n众生的回响，尽归于此", Position = new Vector2(-275, -640), MouseFilter = Control.MouseFilterEnum.Ignore };
        banner.AddThemeFontSizeOverride("font_size", 30); banner.AddThemeColorOverride("font_color", new Color("efd8a2"));
        banner.AddThemeConstantOverride("outline_size", 7); AddChild(banner);
        // A local skip affects the artwork only; turn input never waits on the cinematic.
        var skip = new Button { Text = "跳过演出", Position = new Vector2(160, -620) }; AddChild(skip); introSkip = skip;
        skip.Pressed += CancelIntro;
        var timeout = GetTree().CreateTimer(8); timeout.Timeout += () => { if (GodotObject.IsInstanceValid(skip)) skip.QueueFree(); };
    }
    internal void CancelIntro()
    {
        introTween?.Kill(); intro = 0; Sprite.Modulate = Colors.White;
        if (GodotObject.IsInstanceValid(banner)) banner!.QueueFree(); banner = null;
        if (GodotObject.IsInstanceValid(introSkip)) introSkip!.QueueFree(); introSkip = null;
    }
    internal void HideForDeath() { CancelIntro(); dead = true; Sprite.Visible = false; QueueRedraw(); }
    internal void Pulse(float strength)
    {
        pulse = Math.Max(pulse, strength);
        if (strength >= 0.6f)
        {
            HeartAudio.Play(this, 1);
            var wave = new HeartShockwave { Position = new Vector2(0, -230), Heavy = strength >= 1 }; GetParent().AddChild(wave);
            if (strength >= 1) Flash(new Color(1, 0.2f, 0.08f, 0.14f));
        }
        QueueRedraw();
    }
    private void Flash(Color color)
    {
        var layer = new CanvasLayer { Layer = 3 }; GetTree().Root.AddChild(layer);
        var veil = new ColorRect { Color = color, MouseFilter = Control.MouseFilterEnum.Ignore }; layer.AddChild(veil); veil.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        veil.Size = GetViewportRect().Size;
        var tween = veil.CreateTween(); tween.TweenProperty(veil, "modulate:a", 0, 0.65); tween.Finished += layer.QueueFree;
    }
    private void Render()
    {
        if (dead) return;
        var center = new Vector2(0, -230);
        for (int i = 0; i < 3; i++)
            DrawArc(center, 90 + beatWave * 45 + i * 13, 0, Mathf.Tau, 96, new Color(0.95f, 0.19f, 0.08f, beatWave * (0.18f - i * 0.04f)), 7 - i * 2, true);
        if (pulse > 0) DrawArc(center, 110 + (1 - pulse) * 200, 0, Mathf.Tau, 96, new Color(1, 0.35f, 0.12f, pulse * 0.7f), 7, true);
    }
    internal void Die()
    {
        if (dead) return; dead = true; introTween?.Kill(); HeartAudio.Play(this, 2); Flash(new Color(1, 0.78f, 0.35f, 0.4f)); Sprite.Visible = false; if (banner != null) banner.Visible = false;
        var wave = new HeartShockwave { Position = new Vector2(0, -230), Heavy = true }; GetParent().AddChild(wave);
        var size = Sprite.Texture.GetSize();
        Vector2[] edge = [new(-size.X/2, -size.Y/2), new(0, -size.Y/2), new(size.X/2, -size.Y/2), new(size.X/2, 0), new(size.X/2, size.Y/2), new(0, size.Y/2), new(-size.X/2, size.Y/2), new(-size.X/2, 0)];
        var center = new Vector2(0, size.Y * 0.13f);
        var fragments = new List<Polygon2D>();
        for (int i = 0; i < edge.Length; i++)
        {
            Vector2[] polygon = [center, edge[i], edge[(i + 1) % edge.Length]];
            var piece = new Polygon2D { Texture = Sprite.Texture, Polygon = polygon, UV = polygon.Select(point => point + size / 2).ToArray(), Scale = Sprite.Scale, Position = Sprite.Position };
            GetParent().AddChild(piece); fragments.Add(piece);
            var direction = ((polygon[0] + polygon[1] + polygon[2]) / 3).Normalized();
            var tween = piece.CreateTween().SetParallel();
            tween.TweenProperty(piece, "position", piece.Position + direction * 160 + new Vector2(0, 180 + i * 12), 3.2).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tween.TweenProperty(piece, "rotation", direction.X * 0.55f, 3.2);
            tween.TweenProperty(piece, "modulate:a", 0, 2.4).SetDelay(0.8);
            tween.Finished += piece.QueueFree;
        }
        var conclusion = new Label { Text = "高塔之心 · 终结", Position = new Vector2(-180, -370), ZIndex = 20, MouseFilter = Control.MouseFilterEnum.Ignore };
        conclusion.AddThemeFontSizeOverride("font_size", 34); conclusion.AddThemeColorOverride("font_color", new Color("f1d7a5")); conclusion.AddThemeConstantOverride("outline_size", 6); AddChild(conclusion);
        var finishTween = conclusion.CreateTween(); finishTween.TweenProperty(conclusion, "modulate:a", 0, 2).SetDelay(3); finishTween.Finished += conclusion.QueueFree;
        var skip = new Button { Text = "跳过演出", Position = new Vector2(160, -620) }; AddChild(skip);
        skip.Pressed += () => { foreach (var piece in fragments.Where(GodotObject.IsInstanceValid)) piece.QueueFree(); if (GodotObject.IsInstanceValid(conclusion)) conclusion.QueueFree(); skip.QueueFree(); };
        GetTree().CreateTimer(5).Timeout += () => { if (GodotObject.IsInstanceValid(skip)) skip.QueueFree(); };
        QueueRedraw();
    }
}

internal partial class HeartShockwave : Node2D
{
    private SceneTree? frameTree;
    public HeartShockwave()
    {
        Draw += Render;
        TreeEntered += () => { frameTree = GetTree(); frameTree.ProcessFrame += Frame; };
        TreeExiting += () => { if (GodotObject.IsInstanceValid(frameTree)) frameTree!.ProcessFrame -= Frame; frameTree = null; };
    }
    internal bool Heavy { get; set; }
    private float age;
    private void Frame() { age += (float)GetProcessDeltaTime(); if (age >= 0.95f) QueueFree(); else QueueRedraw(); }
    private void Render()
    {
        float progress = age / 0.95f;
        int count = Heavy ? 3 : 2;
        for (int i = 0; i < count; i++)
        {
            float radius = 65 + progress * (Heavy ? 430 : 290) - i * 24;
            DrawArc(Vector2.Zero, Math.Max(1, radius), 0, Mathf.Tau, 96, new Color(1, 0.25f + i * 0.16f, 0.08f, (1 - progress) * 0.65f), 8 - i * 2, true);
        }
    }
}

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.CreateVisuals))]
internal static class HeartVisualsPatch
{
    static bool Prefix(MonsterModel __instance, ref NCreatureVisuals __result)
    { if (__instance is not TowerHeart) return true; __result = HeartPresentation.CreateVisuals(); return false; }
}
[HarmonyPatch(typeof(EncounterModel), nameof(EncounterModel.CreateBackground))]
internal static class HeartBackgroundPatch
{
    static void Postfix(EncounterModel __instance, ref NCombatBackground __result)
    {
        if (__instance is not TowerHeartEncounter) return;
        var image = new Sprite2D { Texture = HeartPresentation.Texture("arena"), Position = Vector2.Zero };
        image.Scale = new Vector2(1920f / image.Texture.GetWidth(), 1080f / image.Texture.GetHeight());
        __result.AddChild(image);
    }
}
[HarmonyPatch]
internal static class HeartPowerIconPatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods() => [AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Icon)), AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.BigIcon))];
    static bool Prefix(PowerModel __instance, ref Texture2D __result)
    { if (__instance is not HeartPower power) return true; __result = HeartPresentation.PowerTexture(power); return false; }
}
[HarmonyPatch(typeof(PowerModel), "get_HoverTips")]
internal static class HeartPowerHoverPatch
{
    static bool Prefix(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
    { if (__instance is not HeartPower heart) return true; __result = [new HoverTip(heart.Title, heart.Detail, HeartPresentation.PowerTexture(heart))]; return false; }
}

[HarmonyPatch]
internal static class HeartTextureCachePatch
{
    static System.Reflection.MethodBase TargetMethod() => typeof(MegaCrit.Sts2.Core.Assets.AssetCache).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Single(method => method.Name == "GetAsset" && !method.IsGenericMethod && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string));
    static bool Prefix(string __0, ref Resource __result)
    {
        if (!__0.StartsWith("res://images/ui/run_history/tower_heart_encounter", StringComparison.Ordinal)) return true;
        __result = HeartPresentation.Texture("heart"); return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon), "UpdateIcon")]
internal static class HeartCurrentRoomIconPatch
{
    static bool Prefix(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon __instance)
    {
        if (RaceRuntime.State?.CurrentRoom is not MegaCrit.Sts2.Core.Rooms.CombatRoom { Encounter: TowerHeartEncounter }) return true;
        __instance.GetNode<TextureRect>("Icon").Texture = HeartPresentation.Texture("heart");
        __instance.GetNode<TextureRect>("Icon/Outline").Texture = HeartPresentation.Texture("heart");
        return false;
    }
}
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint), nameof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint._Ready))]
internal static class HeartMapIconPatch
{
    static void Postfix(MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint __instance)
    {
        var run = RaceRuntime.State;
        if (run == null || run.Act.SecondBossEncounter is not TowerHeartEncounter || __instance.Point != run.Map.SecondBossMapPoint) return;
        __instance.GetNode<Node2D>("%SpineSprite").Visible = false;
        var sprite = new Sprite2D { Texture = HeartPresentation.Texture("heart"), Scale = Vector2.One * 0.12f, Position = new Vector2(0, -12) };
        __instance.GetNode<Node2D>("%SpriteContainer").AddChild(sprite);
    }
}
