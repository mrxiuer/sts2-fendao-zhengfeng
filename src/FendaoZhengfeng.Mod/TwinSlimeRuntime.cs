using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// Keeps the split bodies as two native player creatures. The engine has no public API for adding a second
/// Player-backed Creature to a live CombatState, so attachment uses its private AttachCreature method and then
/// delegates normal insertion, nodes, hooks and state tracking to CreatureCmd.Add.
/// </summary>
internal static class TwinSlimeRuntime
{
    internal sealed class Pair
    {
        internal required Player Owner { get; init; }
        internal required CombatState State { get; init; }
        internal required Creature First { get; init; }
        internal required Creature Second { get; init; }
        internal required int SourceMaxHp { get; init; }
        internal required int SplitBodyMaxHp { get; init; }
        internal required int Stacks { get; init; }
        internal Task SetupTask { get; set; } = Task.CompletedTask;
        internal bool SettlingDeath { get; set; }
        internal bool Collapsed { get; set; }
        internal IEnumerable<Creature> Bodies => [First, Second];
        internal bool Contains(Creature creature) => creature == First || creature == Second;
    }

    internal sealed class Scope
    {
        internal required Player Owner { get; init; }
        internal required Creature Body { get; init; }
        internal required Pair? Pair { get; init; }
        internal required CardPlay? Play { get; init; }
        internal required bool IsDeath { get; init; }
        internal required Scope? Previous { get; init; }
    }

    private static readonly ConditionalWeakTable<Player, Pair> byPlayer = new();
    private static readonly Dictionary<CombatState, HashSet<Pair>> byCombat = new();
    private static readonly object pairGate = new();
    private static readonly AsyncLocal<Scope?> activeScope = new();
    // CallDeferred closures can outlive a replaced node; weak marks let the callback guard skip old bars without retaining them.
    private static readonly ConditionalWeakTable<NHealthBar, object> retiredHealthBars = new();
    private static readonly object retiredHealthBarMarker = new();
    private static readonly MethodInfo? AttachCreature = AccessTools.Method(typeof(CombatState), "AttachCreature",
        [typeof(Creature)]);
    private static readonly MethodInfo? KillWithoutCheckingWinCondition = AccessTools.Method(typeof(CreatureCmd),
        "KillWithoutCheckingWinCondition", [typeof(Creature), typeof(bool), typeof(int)]);

    internal static int StackCount(Player player)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session
            || RaceRuntime.State?.Players.Contains(player) != true)
            return 0;
        return Math.Max(0, session.Me.Count(TwinSlimeRules.EffectId));
    }

    internal static bool ShouldLookLikeSlime(Player player) => StackCount(player) > 0 || Find(player) != null;

    internal static Pair? Find(Player player)
    {
        if (byPlayer.TryGetValue(player, out var pair) && !pair.Collapsed)
            return pair;
        return null;
    }

    internal static IEnumerable<Creature> BodiesOf(Creature creature) => Find(creature)?.Bodies ?? [creature];

    internal static Pair? Find(Creature creature)
        => creature.Player is { } player && Find(player) is { } pair && pair.Contains(creature) ? pair : null;

    internal static Pair? Find(CombatState state)
    {
        lock (pairGate)
            return byCombat.TryGetValue(state, out var pairs)
                ? pairs.FirstOrDefault(pair => !pair.Collapsed)
                : null;
    }

    internal static Pair[] FindAll(CombatState state)
    {
        lock (pairGate)
            return byCombat.TryGetValue(state, out var pairs)
                ? pairs.Where(pair => !pair.Collapsed).ToArray()
                : [];
    }

    internal static bool HasPair(ICombatState? state) => state is CombatState combat && Find(combat) != null;

    internal static bool IsResolvingDeath(Player player)
    {
        for (var scope = activeScope.Value; scope != null; scope = scope.Previous)
            if (scope.IsDeath && scope.Owner == player)
                return true;
        return false;
    }

    internal static Creature? CurrentBody(Player player)
    {
        for (var scope = activeScope.Value; scope != null; scope = scope.Previous)
            if (scope.Owner == player && (scope.IsDeath || scope.Pair is { Collapsed: false }))
                return scope.Body;
        return null;
    }

    internal static Scope? PushCardPlay(CardPlay play)
    {
        var pair = Find(play.Player);
        if (pair == null || pair.State != play.Card.CombatState)
            return null;
        var scope = new Scope
        {
            Owner = play.Player,
            Body = play.PlayIndex % 2 == 0 ? pair.First : pair.Second,
            Pair = pair,
            Play = play,
            IsDeath = false,
            Previous = activeScope.Value
        };
        activeScope.Value = scope;
        return scope;
    }

    internal static Scope? PushDeath(Creature creature)
    {
        if (creature.Player is not { } player)
            return null;
        var scope = new Scope
        {
            Owner = player,
            Body = creature,
            Pair = Find(player),
            Play = null,
            IsDeath = true,
            Previous = activeScope.Value
        };
        activeScope.Value = scope;
        return scope;
    }

    internal static Scope? PushBody(Creature creature)
    {
        if (creature.Player is not { } player)
            return null;
        var scope = new Scope
        {
            Owner = player,
            Body = creature,
            Pair = Find(player),
            Play = null,
            IsDeath = false,
            Previous = activeScope.Value
        };
        activeScope.Value = scope;
        return scope;
    }

    internal static void Pop(Scope? scope)
    {
        if (scope == null) return;
        if (ReferenceEquals(activeScope.Value, scope))
        {
            activeScope.Value = scope.Previous;
            return;
        }
        var chain = new Stack<Scope>();
        for (var current = activeScope.Value; current != null && !ReferenceEquals(current, scope); current = current.Previous)
            chain.Push(current);
        if (chain.Count == 0) return;
        var restored = scope.Previous;
        while (chain.Count > 0)
        {
            var frame = chain.Pop();
            restored = new Scope { Owner = frame.Owner, Body = frame.Body, Pair = frame.Pair, Play = frame.Play, IsDeath = frame.IsDeath, Previous = restored };
        }
        activeScope.Value = restored;
    }

    internal static Scope? PushCardPlayCleanup(CardPlay play)
    {
        for (var scope = activeScope.Value; scope != null; scope = scope.Previous)
            if (!scope.IsDeath && ReferenceEquals(scope.Play, play))
                return scope;
        return null;
    }

    internal static Task PopAfter(Task original, Scope? scope) => PopAfterCore(original, scope);

    private static async Task PopAfterCore(Task original, Scope? scope)
    {
        try { await original; }
        finally { Pop(scope); }
    }

    internal static bool ShouldPrioritizeFirstSplit(Creature target, decimal amount, CardModel? cardSource)
    {
        if (target.Player is not { } player
            || cardSource?.Owner == player
            || target.CurrentHp <= 0
            || amount < target.CurrentHp)
            return false;
        return CanBeginSplit(target, out _, out _);
    }

    internal static bool TrySplitOnLethal(Creature target, decimal finalHpLoss)
    {
        if (target.CurrentHp <= 0 || finalHpLoss < target.CurrentHp
            || !CanBeginSplit(target, out _, out int stacks))
            return false;
        return BeginSplit(target, stacks);
    }

    private static bool CanBeginSplit(Creature target, out Player? player, out int stacks)
    {
        player = null;
        stacks = 0;
        if (target.Player is not { } owner
            || IsResolvingDeath(owner)
            || !CombatManager.Instance.IsInProgress
            || CombatManager.Instance.IsEnding
            || Find(owner) != null
            || target != owner.Creature
            || target.CombatState is not CombatState state
            || !state.ContainsCreature(target))
            return false;

        stacks = StackCount(owner);
        if (stacks <= 0)
            return false;
        player = owner;
        return true;
    }

    private static bool BeginSplit(Creature first, int stacks)
    {
        if (first.Player is not { } player || first.CombatState is not CombatState state || stacks <= 0 || Find(player) != null)
            return false;

        int sourceMaxHp = Math.Max(1, first.MaxHp);
        int splitHp = TwinSlimeRules.BodyMaxHp(sourceMaxHp, stacks);
        if (splitHp <= 0)
            return false;

        var second = new Creature(player, splitHp, splitHp);
        var pair = new Pair
        {
            Owner = player,
            State = state,
            First = first,
            Second = second,
            SourceMaxHp = sourceMaxHp,
            SplitBodyMaxHp = splitHp,
            Stacks = stacks
        };
        if (!RegisterPair(pair) || !AttachSecond(pair))
            return false;

        first.SetMaxHpInternal(splitHp);
        first.SetCurrentHpInternal(splitHp);
        pair.SetupTask = AddSecondBodyAndCopyPowers(pair, first.Powers.ToArray());
        return true;
    }

    internal static async Task<bool> RestoreSavedSplit(Player player, TwinSlimeSavedState saved)
    {
        if (Find(player) != null || player.Creature.CombatState is not CombatState state
            || !state.ContainsCreature(player.Creature))
            return false;

        Creature first = player.Creature;
        var second = new Creature(player, saved.SecondMaxHp, saved.SecondCurrentHp);
        var pair = new Pair
        {
            Owner = player,
            State = state,
            First = first,
            Second = second,
            SourceMaxHp = saved.SourceMaxHp,
            SplitBodyMaxHp = saved.SplitBodyMaxHp,
            Stacks = saved.Stacks
        };
        if (!RegisterPair(pair) || !AttachSecond(pair))
            return false;

        first.SetMaxHpInternal(saved.FirstMaxHp);
        first.SetCurrentHpInternal(saved.FirstCurrentHp);
        pair.SetupTask = AddSecondBodyAndCopyPowers(pair, first.Powers.ToArray());
        await pair.SetupTask;
        if (pair.Collapsed || !GodotObject.IsInstanceValid(second.GetCreatureNode()))
        {
            Collapse(pair);
            return false;
        }
        return true;
    }

    private static bool RegisterPair(Pair pair)
    {
        lock (pairGate)
        {
            if (byPlayer.TryGetValue(pair.Owner, out _))
                return false;
            byPlayer.Add(pair.Owner, pair);
            if (!byCombat.TryGetValue(pair.State, out var pairs))
                byCombat[pair.State] = pairs = [];
            pairs.Add(pair);
        }
        return true;
    }

    private static bool AttachSecond(Pair pair)
    {
        if (AttachCreature == null)
        {
            GD.PushError("Twin Slime needs the native CombatState.AttachCreature(Creature) API, which is missing in this game version.");
            return false;
        }
        try
        {
            AttachCreature.Invoke(pair.State, [pair.Second]);
            return true;
        }
        catch (Exception ex)
        {
            RemovePair(pair);
            GD.PushError("Twin Slime could not attach its second native player body: " + ex);
            return false;
        }
    }

    internal static void RefreshVisualNode(Creature creature)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        NCreature? oldNode = room?.GetCreatureNode(creature);
        if (room == null || oldNode == null || !GodotObject.IsInstanceValid(oldNode))
            return;

        MarkRetiredHealthBars(oldNode);
        oldNode.ToggleIsInteractable(false);
        oldNode.Visible = false;
        oldNode.ProcessMode = Node.ProcessModeEnum.Disabled;
        room.RemoveCreatureNode(oldNode);
        room.AddCreature(creature);
        oldNode.QueueFree();
    }

    internal static bool IsRetiredHealthBar(NHealthBar healthBar)
        => retiredHealthBars.TryGetValue(healthBar, out _);

    private static void MarkRetiredHealthBars(NCreature node)
    {
        foreach (NHealthBar healthBar in node.FindChildren("*", "", true, false).OfType<NHealthBar>())
            retiredHealthBars.GetValue(healthBar, _ => retiredHealthBarMarker);
    }

    private static void PositionTwinBodies(Pair pair)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        NCreature? first = room?.GetCreatureNode(pair.First);
        NCreature? second = room?.GetCreatureNode(pair.Second);
        if (room == null || first == null || second == null)
            return;

        if (pair.State.Encounter is { } encounter)
        {
            List<NCreature> playerNodes = room.CreatureNodes
                .Where(node => node.Entity.IsPlayer || node.Entity.PetOwner != null)
                .ToList();
            NCombatRoom.PositionPlayersAndPets(playerNodes, encounter.GetCameraScaling(), encounter.FullyCenterPlayers);
            return;
        }

        float firstHalf = first.Visuals.Bounds.Size.X * 0.5f;
        float secondHalf = second.Visuals.Bounds.Size.X * 0.5f;
        float center = first.Position.X;
        float gap = 24f;
        first.Position = new Vector2(center - firstHalf - gap * 0.5f, first.Position.Y);
        second.Position = new Vector2(center + secondHalf + gap * 0.5f, first.Position.Y);
    }

    private static async Task AddSecondBodyAndCopyPowers(Pair pair, PowerModel[] inheritedPowers)
    {
        try
        {
            await CreatureCmd.Add(pair.Second);
            if (pair.Collapsed)
                return;

            RefreshVisualNode(pair.First);
            RestoreSharedOrbDisplay(pair);
            PositionTwinBodies(pair);
            var choiceContext = new ThrowingPlayerChoiceContext();
            foreach (PowerModel original in inheritedPowers)
            {
                if (pair.Collapsed || original.Amount == 0)
                    continue;
                var copy = (PowerModel)original.ClonePreservingMutability();
                await PowerCmd.Apply(choiceContext, copy, pair.Second, original.Amount, original.Applier, null, silent: true);
                if (pair.Second.Powers.Contains(copy))
                {
                    copy.AmountOnTurnStart = original.AmountOnTurnStart;
                    copy.SkipNextDurationTick = original.SkipNextDurationTick;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushError("Twin Slime body setup failed after native insertion: " + ex);
        }
    }

    private static void RestoreSharedOrbDisplay(Pair pair)
    {
        if (pair.Owner.PlayerCombatState is not { } combat || pair.First.GetCreatureNode()?.OrbManager is not { } manager)
            return;
        // Replacing the first creature node also replaces its manager after the
        // native CombatSetUp event has already run. Hydrate its slots and actual
        // models; do not channel or evoke again. Both bodies share one queue.
        var slots = (List<NOrb>)AccessTools.Field(typeof(NOrbManager), "_orbs").GetValue(manager)!;
        if (slots.Count < combat.OrbQueue.Capacity) manager.AddSlotAnim(combat.OrbQueue.Capacity - slots.Count);
        var models = combat.OrbQueue.Orbs.ToArray();
        for (int index = 0; index < models.Length; index++) slots[index].ReplaceOrb(models[index]);
        if (pair.Second.GetCreatureNode()?.OrbManager is { } secondary) secondary.Visible = false;
    }

    internal static bool BeginVerificationSplit(Player player, int stacks)
    {
        if (!CombatManager.Instance.IsInProgress || player.Creature.CombatState is not CombatState state || Find(player) != null)
            return false;
        return BeginSplit(player.Creature, stacks);
    }

    internal static async Task CollapseVerificationSplit(Player player)
    {
        if (Find(player) is not { } pair) return;
        try { await pair.SetupTask; }
        finally { Collapse(pair); }
    }

    internal static async Task<IEnumerable<DamageResult>> AwaitSplitSetup(
        Task<IEnumerable<DamageResult>> damageTask, Creature[] targets)
    {
        IEnumerable<DamageResult> results = await damageTask;
        foreach (Player player in targets.Select(target => target.Player).OfType<Player>().Distinct())
        {
            Pair? pair = Find(player);
            if (pair != null && pair.State == player.Creature.CombatState)
                await pair.SetupTask;
        }
        return results;
    }

    internal static bool NeedsSplitSetupWait(IEnumerable<Creature>? targets)
        => targets?.Any(target => target.Player is { } player && (Find(player) != null || StackCount(player) > 0)) == true;

    internal static int PairedCardPlayCount(Player player, ICombatState? state, int nativeCount)
    {
        var pair = Find(player);
        return pair != null && pair.State == state ? TwinSlimeRules.PairedPlayCount(nativeCount) : nativeCount;
    }

    internal static Task SettleDeath(CombatManager manager, Pair pair, CombatId? combatId, Player player)
        => SettleDeathCore(manager, pair, combatId, player);

    private static async Task SettleDeathCore(CombatManager manager, Pair pair, CombatId? combatId, Player player)
    {
        try
        {
            Creature firstToDie = pair.Bodies.FirstOrDefault(body => body.IsDead) ?? pair.First;
            foreach (Creature body in pair.Bodies.Where(body => body.IsAlive).ToArray())
                await KillWithoutWinCheck(body);
            Scope? scope = PushDeath(firstToDie);
            try { await manager.HandlePlayerDeath(combatId, player); }
            finally { Pop(scope); }
        }
        finally
        {
            pair.SettlingDeath = false;
        }
    }

    private static async Task KillWithoutWinCheck(Creature creature)
    {
        if (KillWithoutCheckingWinCondition == null)
            throw new MissingMethodException(typeof(CreatureCmd).FullName, "KillWithoutCheckingWinCondition(Creature,bool,int)");
        if (KillWithoutCheckingWinCondition.Invoke(null, [creature, true, 0]) is Task killTask)
            await killTask;
    }

    internal static Task CollapseAfterCombat(Task original, Pair[] pairs) => CollapseAfterCombatCore(original, pairs);

    private static async Task CollapseAfterCombatCore(Task original, Pair[] pairs)
    {
        foreach (Pair pair in pairs)
        {
            try { await pair.SetupTask; }
            catch (Exception ex) { GD.PushError("Twin Slime cleanup waited on a failed body setup: " + ex); }
        }
        try { await original; }
        finally { foreach (Pair pair in pairs) Collapse(pair); }
    }

    internal static void CollapseForReset()
    {
        Pair[] pairs;
        lock (pairGate) pairs = byCombat.Values.SelectMany(combatPairs => combatPairs).Distinct().ToArray();
        foreach (Pair pair in pairs)
            Collapse(pair);
    }

    private static void RemovePair(Pair pair)
    {
        lock (pairGate)
        {
            byPlayer.Remove(pair.Owner);
            if (!byCombat.TryGetValue(pair.State, out var pairs)) return;
            pairs.Remove(pair);
            if (pairs.Count == 0)
                byCombat.Remove(pair.State);
        }
    }

    private static void Collapse(Pair pair)
    {
        if (pair.Collapsed) return;
        pair.Collapsed = true;
        int restoredMaxHp = TwinSlimeRules.RecombinedMaxHp(
            pair.SourceMaxHp, pair.SplitBodyMaxHp, pair.First.MaxHp, pair.Second.MaxHp);
        int restoredHp = TwinSlimeRules.RecombinedCurrentHp(pair.First.CurrentHp, pair.Second.CurrentHp, restoredMaxHp);

        Creature second = pair.Second;
        try
        {
            if (GodotObject.IsInstanceValid(second.GetCreatureNode()))
            {
                NCreature node = second.GetCreatureNode()!;
                NCombatRoom.Instance?.RemoveCreatureNode(node);
                node.ToggleIsInteractable(false);
                node.Visible = false;
            }
        }
        catch (Exception ex)
        {
            GD.PushError("Twin Slime could not remove the second body node cleanly: " + ex);
        }
        try
        {
            second.Reset();
            if (pair.State.ContainsCreature(second))
            {
                CombatManager.Instance.RemoveCreature(second);
                pair.State.RemoveCreature(second);
            }
        }
        catch (Exception ex)
        {
            GD.PushError("Twin Slime could not detach the second combat body cleanly: " + ex);
        }

        pair.First.SetMaxHpInternal(restoredMaxHp);
        pair.First.SetCurrentHpInternal(restoredHp);
        RemovePair(pair);
        TwinSlimePersistence.ClearStored(pair.Owner);
        if (pair.First.IsAlive && CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding)
            RefreshVisualNode(pair.First);
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
internal static class TwinSlimeLethalSplitPatch
{
    static void Prefix(Creature __instance, ref decimal __0)
    {
        if (TwinSlimeRuntime.TrySplitOnLethal(__instance, __0))
            __0 = 0m;
    }
}

[HarmonyPatch]
internal static class TwinSlimeDamageSetupPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
        [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
         typeof(Creature), typeof(CardModel), typeof(CardPlay)])!;

    static void Postfix(IEnumerable<Creature>? __1, ref Task<IEnumerable<DamageResult>> __result)
    {
        if (!TwinSlimeRuntime.NeedsSplitSetupWait(__1)) return;
        __result = TwinSlimeRuntime.AwaitSplitSetup(__result, __1!.ToArray());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class TwinSlimePairedCardCountPatch
{
    static void Postfix(ICombatState __0, CardModel __1, ref int __result)
        => __result = TwinSlimeRuntime.PairedCardPlayCount(__1.Owner!, __0, __result);
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
internal static class TwinSlimeCardScopePatch
{
    static void Prefix(CardPlay __1, out TwinSlimeRuntime.Scope? __state)
        => __state = TwinSlimeRuntime.PushCardPlay(__1);
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class TwinSlimeAfterCardScopePatch
{
    static void Prefix(CardPlay __2, out TwinSlimeRuntime.Scope? __state)
        => __state = TwinSlimeRuntime.PushCardPlayCleanup(__2);

    static void Postfix(ref Task __result, TwinSlimeRuntime.Scope? __state)
    {
        if (__state != null)
            __result = TwinSlimeRuntime.PopAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Creature), MethodType.Getter)]
internal static class TwinSlimeCardOwnerCreaturePatch
{
    static void Postfix(Player __instance, ref Creature __result)
    {
        if (TwinSlimeRuntime.CurrentBody(__instance) is { } body)
            __result = body;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.CreateVisuals))]
internal static class TwinSlimeVisualPatch
{
    static bool Prefix(Creature __instance, ref NCreatureVisuals? __result)
    {
        if (__instance.Player is not { } player || !TwinSlimeRuntime.ShouldLookLikeSlime(player))
            return true;
        MonsterModel model = TwinSlimeRuntime.Find(__instance) != null
            ? ModelDb.Monster<LeafSlimeS>()
            : ModelDb.Monster<LeafSlimeM>();
        __result = EnsureFormVfxHolder(model.CreateVisuals());
        return false;
    }

    private static NCreatureVisuals EnsureFormVfxHolder(NCreatureVisuals visuals)
    {
        if (visuals.GetNodeOrNull<Control>("%FormVfx") != null)
            return visuals;

        Control holder = visuals.GetNodeOrNull<Control>("FormVfx")
            ?? new Control { Name = "FormVfx", MouseFilter = Control.MouseFilterEnum.Ignore };
        if (holder.GetParent() == null)
            visuals.AddChild(holder);
        holder.Owner = visuals;
        holder.UniqueNameInOwner = true;
        return visuals;
    }
}

[HarmonyPatch]
internal static class TwinSlimeCharacterAnimatorPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(CharacterModel), nameof(CharacterModel.GenerateAnimator),
        [typeof(MegaSprite), typeof(Creature)])!;

    static bool Prefix(MegaSprite __0, Creature __1, ref CreatureAnimator __result)
    {
        if (__1.Player is not { } player || !TwinSlimeRuntime.ShouldLookLikeSlime(player))
            return true;
        MonsterModel model = TwinSlimeRuntime.Find(__1) != null
            ? ModelDb.Monster<LeafSlimeS>()
            : ModelDb.Monster<LeafSlimeM>();
        __result = model.GenerateAnimator(__0);
        return false;
    }
}

[HarmonyPatch]
internal static class TwinSlimeRetiredHealthBarPatch
{
    static MethodBase TargetMethod()
        => AccessTools.Method(typeof(NHealthBar), "SetHpBarContainerSizeWithOffsetsImmediately")!;

    static bool Prefix(NHealthBar __instance)
        => !TwinSlimeRuntime.IsRetiredHealthBar(__instance);
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class TwinSlimeFaceRightPatch
{
    static void Postfix(NCreature __instance)
    {
        if (__instance.Entity.Player is not { } player || !TwinSlimeRuntime.ShouldLookLikeSlime(player))
            return;

        Node2D body = __instance.Visuals.Body;
        Vector2 scale = body.Scale;
        body.Scale = new Vector2(-Math.Abs(scale.X), scale.Y);
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.Name), MethodType.Getter)]
internal static class TwinSlimeNamePatch
{
    static void Postfix(Creature __instance, ref string __result)
    {
        if (__instance.Player is not { } player || !TwinSlimeRuntime.ShouldLookLikeSlime(player))
            return;
        if (TwinSlimeRuntime.Find(__instance) is { } pair)
            __result = __instance == pair.First ? L10n.T("双生黏液 1") : L10n.T("双生黏液 2");
        else
            __result = L10n.T("黏液");
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.Players), MethodType.Getter)]
internal static class TwinSlimeDeduplicatePlayersPatch
{
    static void Postfix(CombatState __instance, ref IReadOnlyList<Player> __result)
    {
        if (TwinSlimeRuntime.HasPair(__instance))
            __result = __result.Distinct().ToArray();
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
internal static class TwinSlimeDeduplicateHookListenersPatch
{
    static void Postfix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        if (TwinSlimeRuntime.HasPair(__instance))
            __result = __result.Distinct<AbstractModel>(ReferenceEqualityComparer.Instance).ToArray();
    }
}

[HarmonyPatch]
internal static class TwinSlimeDeathScopePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(typeof(CreatureCmd), "KillWithoutCheckingWinCondition",
        [typeof(Creature), typeof(bool), typeof(int)])!;

    static void Prefix(Creature __0, out TwinSlimeRuntime.Scope? __state)
        => __state = TwinSlimeRuntime.PushDeath(__0);

    static void Postfix(ref Task __result, TwinSlimeRuntime.Scope? __state)
    {
        if (__state != null)
            __result = TwinSlimeRuntime.PopAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeDeath))]
internal static class TwinSlimeBeforeDeathBodyScopePatch
{
    static void Prefix(Creature __2, out TwinSlimeRuntime.Scope? __state)
        => __state = TwinSlimeRuntime.PushBody(__2);

    static void Postfix(ref Task __result, TwinSlimeRuntime.Scope? __state)
    {
        if (__state != null)
            __result = TwinSlimeRuntime.PopAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
internal static class TwinSlimeAfterDeathBodyScopePatch
{
    static void Prefix(Creature __2, out TwinSlimeRuntime.Scope? __state)
        => __state = TwinSlimeRuntime.PushBody(__2);

    static void Postfix(ref Task __result, TwinSlimeRuntime.Scope? __state)
    {
        if (__state != null)
            __result = TwinSlimeRuntime.PopAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.HandlePlayerDeath))]
internal static class TwinSlimeDeathPropagationPatch
{
    static bool Prefix(CombatManager __instance, CombatId? __0, Player __1, ref Task __result)
    {
        var pair = TwinSlimeRuntime.Find(__1);
        if (pair == null || pair.SettlingDeath)
            return true;
        pair.SettlingDeath = true;
        __result = TwinSlimeRuntime.SettleDeath(__instance, pair, __0, __1);
        return false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
internal static class TwinSlimeCollapseAfterCombatPatch
{
    static void Postfix(ICombatState? __1, ref Task __result)
    {
        if (__1 is CombatState state && TwinSlimeRuntime.FindAll(state) is { Length: > 0 } pairs)
            __result = TwinSlimeRuntime.CollapseAfterCombat(__result, pairs);
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.Reset))]
internal static class TwinSlimeResetCleanupPatch
{
    static void Prefix() => TwinSlimeRuntime.CollapseForReset();
}
