using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace FendaoZhengfeng.Mod;

internal static class PuppetPresentation
{
    private static Texture2D? texture;
    internal static Texture2D Texture()
    {
        if (texture != null) return texture;
        using var source = typeof(PuppetPresentation).Assembly.GetManifestResourceStream("FendaoZhengfeng.Followers.puppet.png")
            ?? throw new IOException("缺少傀儡立绘。");
        using var bytes = new MemoryStream(); source.CopyTo(bytes);
        var image = new Image();
        if (image.LoadPngFromBuffer(bytes.ToArray()) != Error.Ok) throw new IOException("傀儡立绘损坏。");
        image.Resize(512, 512, Image.Interpolation.Lanczos);
        return texture = ImageTexture.CreateFromImage(image);
    }
    internal static NCreatureVisuals CreateVisuals()
    {
        var root = new NCreatureVisuals { Name = "SubstitutePuppetVisuals" };
        void Add(Node node) { root.AddChild(node); node.Owner = root; node.UniqueNameInOwner = true; }
        var body = new Node2D { Name = "Visuals" }; Add(body);
        var sprite = new Sprite2D { Name = "PuppetSprite", Texture = Texture(), Position = new Vector2(0, -128), Scale = Vector2.One * 0.5f };
        body.AddChild(sprite); body.AddChild(new PuppetMotion { Sprite = sprite });
        Add(new Control { Name = "Bounds", Position = new Vector2(-98, -244), Size = new Vector2(196, 244), MouseFilter = Control.MouseFilterEnum.Ignore });
        Add(new Marker2D { Name = "IntentPos", Position = new Vector2(0, -260) });
        Add(new Marker2D { Name = "CenterPos", Position = new Vector2(0, -130) });
        Add(new Marker2D { Name = "TalkPos", Position = new Vector2(75, -210) });
        return root;
    }
    internal static void PositionFollowers(IEnumerable<NCreature> nodes)
    {
        if (!nodes.Any(node => node.Entity.Monster is SubstitutePuppet)) return;
        var list = nodes.ToArray();
        foreach (var follower in list.Where(node => node.Entity.Monster is SubstitutePuppet && node.Entity.IsAlive))
        {
            var owner = list.FirstOrDefault(node => node.Entity == follower.Entity.PetOwner?.Creature);
            if (owner != null) follower.Position = owner.Position + new Vector2(230, 12);
        }
    }
}

internal partial class PuppetMotion : Node2D
{
    internal Sprite2D Sprite { get; set; } = null!;
    private SceneTree? tree;
    private double elapsed;
    internal PuppetMotion()
    {
        TreeEntered += () => { tree = GetTree(); tree.ProcessFrame += Frame; };
        TreeExiting += () => { if (GodotObject.IsInstanceValid(tree)) tree!.ProcessFrame -= Frame; tree = null; };
    }
    private void Frame()
    {
        if (!GodotObject.IsInstanceValid(Sprite)) return;
        elapsed += GetProcessDeltaTime();
        Sprite.Rotation = (float)Math.Sin(elapsed * 1.7) * 0.012f;
        Sprite.Scale = new Vector2(0.5f, 0.5f + (float)Math.Sin(elapsed * 1.7) * 0.004f);
    }
}

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.CreateVisuals))]
internal static class PuppetVisualsPatch
{
    static bool Prefix(MonsterModel __instance, ref NCreatureVisuals __result)
    { if (__instance is not SubstitutePuppet) return true; __result = PuppetPresentation.CreateVisuals(); return false; }
}

// Native pet positioning disables interaction and hides the whole status row.
// This follower needs inspectable HP, block and debuffs throughout combat.
[HarmonyPatch(typeof(NCreature), nameof(NCreature.ToggleIsInteractable))]
internal static class PuppetStatusVisibilityPatch
{
    static void Prefix(NCreature __instance, ref bool on)
    { if (__instance.Entity.Monster is SubstitutePuppet && __instance.Entity.IsAlive) on = true; }
}

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
internal static class PuppetAddedPositionPatch
{
    static void Postfix(NCombatRoom __instance)
        => PuppetPresentation.PositionFollowers(__instance.CreatureNodes);
}
[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.PositionPlayersAndPets))]
internal static class PuppetLayoutPositionPatch
{
    static void Postfix(List<NCreature> creatureNodes) => PuppetPresentation.PositionFollowers(creatureNodes);
}
