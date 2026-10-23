using Environment = System.Environment;
using MegaCrit.Sts2.Core.Combat;
using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// Native acceptance fixture for a Necrobinder with a Giant twin-slime pair, Osty and the substitute puppet.
/// Invoke from a live Necrobinder combat in the local native verification session.
/// </summary>
internal static class NativeFormationVerification
{
    private static async Task WaitUntil(Func<bool> condition, string reason)
    {
        long deadline = Environment.TickCount64 + 40000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(reason);
            await Task.Delay(25);
        }
    }

    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("Formation verification needs a live race session.");
        Player player = run.Players.Single();
        Creature hero = player.Creature;
        if (player.Character is not Necrobinder)
        {
            check(false, "formation fixture must run on Necrobinder so the native local Osty placement path is active");
            return;
        }

        if (!CombatManager.Instance.IsInProgress || hero.CombatState == null || NCombatRoom.Instance == null)
            throw new InvalidOperationException("Formation verification needs an active Necrobinder combat room.");
        if (TwinSlimeRuntime.Find(player) != null || PuppetFollower.Find(player) != null)
        {
            check(false, "formation fixture needs an unsplit Necrobinder without an existing puppet");
            return;
        }

        var context = new ThrowingPlayerChoiceContext();
        NCombatRoom room = NCombatRoom.Instance ?? throw new InvalidOperationException("Formation verification needs a live combat room.");
        NCreature heroNode = room.GetCreatureNode(hero)
            ?? throw new InvalidOperationException("Necrobinder visual node is missing.");
        int originalMaxHp = hero.MaxHp;
        int originalCurrentHp = hero.CurrentHp;
        int originalStrength = hero.GetPowerAmount<StrengthPower>();
        bool hadGiant = session.Me.Effects.TryGetValue(GiantForm.EffectId, out var originalGiantHolding);
        int originalGiantRank = originalGiantHolding?.Rank ?? -1;
        float baseScale = heroNode.Visuals.DefaultScale
            / (hadGiant ? GiantForm.ScaleMultiplier(originalGiantRank) : 1f);
        bool hadTwinSlime = session.Me.Effects.ContainsKey(TwinSlimeRules.EffectId);
        bool hadGiantEnabled = session.Engine.State.EnabledEffects.Contains(GiantForm.EffectId);
        bool hadTwinSlimeEnabled = session.Engine.State.EnabledEffects.Contains(TwinSlimeRules.EffectId);
        Creature? puppet = null;
        Creature? osty = null;
        bool splitStarted = false;

        try
        {
            // Exercise a real Giant transition even when the current run already has the effect.
            // Keep its original Holding so the fixture can restore the exact run state afterward.
            if (hadGiant)
            {
                session.Me.Effects.Remove(GiantForm.EffectId);
                await WaitUntil(() => Math.Abs(heroNode.Visuals.DefaultScale - baseScale) < 0.05f
                                      && Math.Abs(heroNode.Visuals.Scale.X - baseScale) < 0.1f,
                    "the existing Giant effect did not restore the Necrobinder's native scale");
            }

            session.Engine.State.EnabledEffects.Add(GiantForm.EffectId);
            session.Engine.State.EnabledEffects.Add(TwinSlimeRules.EffectId);
            await MockTesting.GrantEffect(GiantForm.EffectId);
            if (!hadTwinSlime)
                await MockTesting.GrantEffect(TwinSlimeRules.EffectId);

            if (hero.GetPowerAmount<StrengthPower>() <= 0)
                await PowerCmd.Apply<StrengthPower>(context, hero, 1, null, null);

            await OstyCmd.Summon(context, player, 100, null);
            osty = player.Osty ?? throw new InvalidOperationException("Native Osty summon did not attach a pet.");
            puppet = await PuppetFollower.Spawn(player, 80, 80);
            // DieForYou is hidden; add a real visible combat status to measure Osty's status row.
            await PowerCmd.Apply<StrengthPower>(context, osty, 1, null, null);

            check(GiantForm.Rank >= 0, "native Giant effect is active before the formation is measured");

            splitStarted = TwinSlimeRuntime.BeginVerificationSplit(player, 1);
            check(splitStarted, "the native split command creates a second player-backed slime body");
            if (!splitStarted)
                return;

            TwinSlimeRuntime.Pair pair = TwinSlimeRuntime.Find(player)
                ?? throw new InvalidOperationException("Twin split did not register its paired bodies.");
            await pair.SetupTask;
            await WaitUntil(() =>
            {
                NCreature? first = room.GetCreatureNode(pair.First);
                NCreature? second = room.GetCreatureNode(pair.Second);
                float targetScale = (first?.GetNodeOrNull<GiantScaleFollower>("GiantScaleFollower")?.NaturalScale ?? 1f) * GiantForm.ScaleMultiplier(GiantForm.Rank);
                return first != null && second != null
                       && first.IsVisibleInTree() && second.IsVisibleInTree()
                       && Math.Abs(first.Visuals.DefaultScale - targetScale) < 0.05f
                       && Math.Abs(second.Visuals.DefaultScale - targetScale) < 0.05f
                       && Math.Abs(first.Visuals.Scale.X - targetScale) < 0.1f
                       && Math.Abs(second.Visuals.Scale.X - targetScale) < 0.1f
                       && room.GetCreatureNode(osty)?.IsVisibleInTree() == true
                       && room.GetCreatureNode(puppet)?.IsVisibleInTree() == true;
            }, "the real Osty, puppet and giant twin-slime nodes did not finish entering the room");

            // Osty's native size tween and the scene scale both affect its actual visible bounds.
            await Task.Delay(850);
            await Task.Delay(50);

            Creature[] expectedEntities = [pair.First, pair.Second, osty, puppet];
            NCreature[] expectedNodes = expectedEntities
                .Select(creature => room.GetCreatureNode(creature))
                .OfType<NCreature>()
                .ToArray();
            check(expectedNodes.Length == 4 && expectedNodes.All(node => node.IsVisibleInTree()),
                "both slime models, the native Osty and the separate puppet are visible as four allied creatures");

            bool allNativeParts = expectedNodes.Length == 4 && expectedNodes.All(node =>
            {
                string[] parts = FriendlyFormation.MeasureComponents(node).Select(component => component.Part).ToArray();
                return parts.Contains("visual-bounds")
                       && parts.Contains("hitbox")
                       && parts.Contains("health-bar")
                       && parts.Contains("status-row");
            });
            foreach (var node in expectedNodes)
                Godot.GD.Print("[FDZF FORMATION PARTS] " + node.Entity.Name + ": " + string.Join(",", FriendlyFormation.MeasureComponents(node).Select(part => part.Part)));
            check(allNativeParts,
                "each ally exposes its own native model bounds, real hitbox, health bar and visible power/status row");

            bool noOverlap = FriendlyFormation.VerifyNoOverlap(room.CreatureNodes, out string layout);
            check(noOverlap, "four-ally Giant formation stays visible, keeps every body and UI rectangle separate, and stays out of the enemy lane: " + layout);
            Vector2[] stablePositions = expectedNodes.Select(node => node.GlobalPosition).ToArray();
            for (int pass = 0; pass < 5; pass++) FriendlyFormation.Reflow(room.CreatureNodes);
            float repeatedDrift = expectedNodes.Select((node, index) => node.GlobalPosition.DistanceTo(stablePositions[index])).Max();
            check(repeatedDrift < 0.5f, $"repeating formation layout does not drift: {repeatedDrift:0.000} units");
            int previousSignature = FriendlyFormation.SizeSignature(room);
            await PowerCmd.Apply<ArtifactPower>(context, pair.Second, 1, null, null);
            await WaitUntil(() => FriendlyFormation.SizeSignature(room) != previousSignature,
                "a newly added native status was not detected by the layout observer");
            await Task.Delay(400);
            check(FriendlyFormation.VerifyNoOverlap(room.CreatureNodes, out var newStatusLayout),
                "adding a native power updates the observed footprint without overlap: " + newStatusLayout);
            await NativeAcceptanceVisuals.Capture("formation-01-giant-necrobinder-four-allies");

            NCreature puppetNode = room.GetCreatureNode(puppet)
                ?? throw new InvalidOperationException("Puppet visual node disappeared before the death check.");
            await CreatureCmd.Kill(puppet, true);
            await WaitUntil(() => !GodotObject.IsInstanceValid(puppetNode) || !puppetNode.IsInsideTree(),
                "dead puppet node was not removed after its native death animation");
            await Task.Delay(200);
            bool afterDeath = FriendlyFormation.VerifyNoOverlap(room.CreatureNodes, out string afterDeathLayout);
            check(afterDeath, "native creature death reflows the remaining giant hero bodies and Osty without overlap: " + afterDeathLayout);

            puppet = await PuppetFollower.Spawn(player, 91, 91);
            await WaitUntil(() =>
            {
                NCreature? node = room.GetCreatureNode(puppet);
                return node != null && node.IsVisibleInTree()
                       && FriendlyFormation.MeasureComponents(node).Any(component => component.Part == "health-bar")
                       && FriendlyFormation.MeasureComponents(node).Any(component => component.Part == "status-row");
            }, "replacement puppet did not receive its native health and power rows");
            await Task.Delay(50);
            noOverlap = FriendlyFormation.VerifyNoOverlap(room.CreatureNodes, out layout);
            check(noOverlap, "summoning the replacement puppet reflows the full four-ally formation: " + layout);
            await NativeAcceptanceVisuals.Capture("formation-02-puppet-resummoned");

            NCreature[] giantNodes = new[] { pair.First, pair.Second, osty, puppet }
                .Select(creature => room.GetCreatureNode(creature))
                .OfType<NCreature>()
                .ToArray();
            Vector2[] giantPositions = giantNodes.Select(node => node.GlobalPosition).ToArray();

            session.Me.Effects.Remove(GiantForm.EffectId);
            var currentHeroNode = room.GetCreatureNode(pair.First)!;
            float slimeBaseScale = currentHeroNode.GetNode<GiantScaleFollower>("GiantScaleFollower").NaturalScale;
            await WaitUntil(() => Math.Abs(currentHeroNode.Visuals.DefaultScale - slimeBaseScale) < 0.05f
                                  && Math.Abs(currentHeroNode.Visuals.Scale.X - slimeBaseScale) < 0.1f,
                "removing Giant did not restore the Necrobinder's native visual scale");
            // Let the size-signature observer see the end of the scale tween and reflow without
            // directly calling Reflow here; this verifies the runtime path used by real play.
            await Task.Delay(300);
            bool restoredNoOverlap = FriendlyFormation.VerifyNoOverlap(room.CreatureNodes, out string restoredLayout);
            float maxPositionShift = 0f;
            for (int i = 0; i < Math.Min(giantNodes.Length, giantPositions.Length); i++)
                maxPositionShift = Math.Max(maxPositionShift, giantNodes[i].GlobalPosition.DistanceTo(giantPositions[i]));
            check(restoredNoOverlap && giantNodes.Length == 4 && maxPositionShift > 1f,
                $"Giant removal restores native scale and the automatic size observer reflows the allies by {maxPositionShift:0.0} units: {restoredLayout}");
        }
        finally
        {
            if (splitStarted)
                await TwinSlimeRuntime.CollapseVerificationSplit(player);

            if (puppet?.IsAlive == true)
                await CreatureCmd.Kill(puppet, true);
            if (osty?.IsAlive == true)
                await CreatureCmd.Kill(osty, true);

            int strengthDifference = originalStrength - hero.GetPowerAmount<StrengthPower>();
            if (strengthDifference != 0)
                await PowerCmd.Apply<StrengthPower>(context, hero, strengthDifference, null, null);

            await CreatureCmd.SetMaxAndCurrentHp(hero, originalMaxHp);
            await CreatureCmd.SetCurrentHp(hero, Math.Clamp(originalCurrentHp, 1, originalMaxHp));

            session.Me.Effects.Remove(GiantForm.EffectId);
            if (originalGiantHolding != null)
                session.Me.Effects[GiantForm.EffectId] = originalGiantHolding;
            if (!hadTwinSlime)
                session.Me.Effects.Remove(TwinSlimeRules.EffectId);
            if (!hadGiantEnabled)
                session.Engine.State.EnabledEffects.Remove(GiantForm.EffectId);
            if (!hadTwinSlimeEnabled)
                session.Engine.State.EnabledEffects.Remove(TwinSlimeRules.EffectId);
        }
    }
}

