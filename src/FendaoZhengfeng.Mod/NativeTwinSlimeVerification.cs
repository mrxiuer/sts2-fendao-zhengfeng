using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeTwinSlimeVerification
{
    private static async Task Wait(Func<bool> condition, string reason)
    {
        long deadline = Environment.TickCount64 + 30000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(reason);
            await Task.Delay(25);
        }
    }

    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("Twin Slime verification needs a live race session.");
        var player = run.Players.Single();
        Creature original = player.Creature;
        var state = original.CombatState ?? throw new InvalidOperationException("Twin Slime verification needs an active combat.");
        var context = new ThrowingPlayerChoiceContext();
        Creature enemy = state.Enemies.First(creature => creature.IsAlive);
        int originalMaxHp = original.MaxHp;
        int originalCurrentHp = original.CurrentHp;
        int originalStrength = original.GetPowerAmount<StrengthPower>();
        int originalGhosts = session.Me.Ghosts;
        var tail = player.Relics.OfType<LizardTail>().Single();
        bool originalTailUsed = tail.WasUsed;
        RelicStatus originalTailStatus = tail.Status;
        bool hadFairy = player.Potions.Any(potion => potion is FairyInABottle);
        CardModel? testCard = null;
        Creature? verificationPuppet = null;

        session.Engine.State.EnabledEffects.Add(TwinSlimeRules.EffectId);
        await MockTesting.GrantEffect(TwinSlimeRules.EffectId);
        NCreature? nodeBeforeGrant = original.GetCreatureNode();
        TwinSlimeRuntime.RefreshVisualNode(original);
        NHealthBar[] retiredHealthBars = nodeBeforeGrant?.FindChildren("*", "", true, false).OfType<NHealthBar>().ToArray() ?? [];
        NCreature? unsplitNode = original.GetCreatureNode();
        NHealthBar? unsplitHealthBar = unsplitNode?.FindChildren("*", "", true, false).OfType<NHealthBar>().FirstOrDefault();
        check(retiredHealthBars.Length > 0 && retiredHealthBars.All(TwinSlimeRuntime.IsRetiredHealthBar)
              && unsplitHealthBar != null && !TwinSlimeRuntime.IsRetiredHealthBar(unsplitHealthBar),
            "visual replacement marks deferred callbacks from old native health bars while leaving the live bar untouched");
        check(unsplitNode != null && unsplitHealthBar != null
              && unsplitNode.Visuals.Body.Scale.X < 0 && unsplitNode.Scale.X > 0
              && unsplitHealthBar.Scale.X > 0 && unsplitNode.Visuals.FormVfxHolder != null,
            "the unsplit slime faces right while its creature root and native health bar remain unmirrored");
        await NativeAcceptanceVisuals.Capture("twin-slime-00-unsplit");
        check(TwinSlimeRules.SplitPercent(1) == 50
              && TwinSlimeRules.SplitPercent(2) == 55
              && TwinSlimeRules.SplitPercent(20) == 75
              && TwinSlimeRules.BodyMaxHp(101, 1) == 51,
            "twin split uses 50 percent per body, +5 points per repeat up to 75 percent, with halves rounded away from zero");

        try
        {
            await CreatureCmd.SetMaxAndCurrentHp(original, 5000);
            await CreatureCmd.SetCurrentHp(original, 100);
            var lowHpSave = RunManager.Instance.ToSave(null);
            var lowHpRoundTrip = RunState.FromSerializable(lowHpSave);
            string lowHpState = lowHpRoundTrip.Modifiers.OfType<RaceModifier>().Single().TwinSlimeStateJson;
            await TwinSlimePersistence.RestorePendingRoom(RaceModifier.Active!);
            check(string.IsNullOrEmpty(lowHpState) && TwinSlimeRuntime.Find(player) == null,
                "saving low HP before a real split stores no paired state and cannot reconstruct a free pair");

            await CreatureCmd.SetMaxAndCurrentHp(original, 5000);
            await PowerCmd.Apply<StrengthPower>(context, original, 4, enemy, null);
            tail.WasUsed = false;
            tail.Status = RelicStatus.Normal;
            session.Me.Ghosts = 1;
            if (!hadFairy)
                await PotionCmd.TryToProcure<FairyInABottle>(player);
            check(player.Potions.Any(potion => potion is FairyInABottle),
                "native Fairy in a Bottle is present for the ordered revival fixture");

            verificationPuppet = await PuppetFollower.Spawn(player, 1, 1);
            ((SubstitutePuppet)verificationPuppet.Monster!).GuardsOwner = true;
            var overkill = (await CreatureCmd.Damage(context, original, 5101,
                ValueProp.Move | ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null)).ToArray();

            var pair = TwinSlimeRuntime.Find(player);
            check(verificationPuppet.IsDead
                  && overkill.Any(hit => hit.Receiver == verificationPuppet)
                  && overkill.Any(hit => hit.Receiver == original)
                  && pair != null
                  && pair.First == original
                  && pair.First.IsAlive
                  && pair.Second.IsAlive
                  && state.PlayerCreatures.Count(creature => creature.Player == player) == 2,
                "native attack kills the guarded puppet first, then overkill reaches the hero and causes a true two-body split");
            if (pair == null) return;

            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR")))
                await Task.Delay(2000);
            await NativeAcceptanceVisuals.Capture("twin-slime-01-split");

            int stacks = TwinSlimeRuntime.StackCount(player);
            int expectedBodyHp = TwinSlimeRules.BodyMaxHp(5000, stacks);
            check(pair.First.MaxHp == expectedBodyHp && pair.First.CurrentHp == expectedBodyHp
                  && pair.Second.MaxHp == expectedBodyHp && pair.Second.CurrentHp == expectedBodyHp,
                "both real bodies start at the stack-adjusted share of the pre-split maximum HP");
            check(session.Me.Ghosts == 1 && player.Potions.Any(potion => potion is FairyInABottle)
                  && !tail.WasUsed,
                "the first lethal hit prioritizes splitting without spending Ghost, Fairy or the native Tail");
            check(pair.Second.GetPowerAmount<StrengthPower>() == 4 && pair.First.GetPowerAmount<StrengthPower>() == 4,
                "the new body receives an independent native copy of the powers present at the split");
            check(state.Players.Count(candidate => candidate == player) == 1,
                "a second body does not duplicate the multiplayer Player entry or its shared hooks");
            check(pair.First.Name == "双生黏液 1" && pair.Second.Name == "双生黏液 2",
                "both combat entities expose separate twin-slime names");

            NCreature? firstNode = pair.First.GetCreatureNode();
            NCreature? secondNode = pair.Second.GetCreatureNode();
            check(firstNode != null && secondNode != null && firstNode != secondNode
                  && firstNode.IsVisibleInTree() && secondNode.IsVisibleInTree()
                  && firstNode.Position != secondNode.Position
                  && firstNode.HasSpineAnimation && secondNode.HasSpineAnimation
                  && firstNode.FindChildren("*", "", true, false).OfType<NHealthBar>().Any()
                  && secondNode.FindChildren("*", "", true, false).OfType<NHealthBar>().Any()
                  && firstNode.Visuals.Body.Scale.X < 0 && secondNode.Visuals.Body.Scale.X < 0
                  && firstNode.Visuals.FormVfxHolder != null && secondNode.Visuals.FormVfxHolder != null
                  && firstNode.Scale.X > 0 && secondNode.Scale.X > 0
                  && firstNode.FindChildren("*", "", true, false).OfType<NHealthBar>().All(bar => bar.Scale.X > 0)
                  && secondNode.FindChildren("*", "", true, false).OfType<NHealthBar>().All(bar => bar.Scale.X > 0),
                "both native small-slime bodies have separate visible hitboxes, health bars and native animation controllers");

            int secondHp = pair.Second.CurrentHp;
            await CreatureCmd.Damage(context, pair.First, 13, ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
            check(pair.First.CurrentHp == expectedBodyHp - 13 && pair.Second.CurrentHp == secondHp,
                "direct native damage changes only the selected body's current HP");

            var enemyAreaAttack = await new AttackCommand(0).FromMonster(enemy.Monster!).WithNoAttackerAnim().Execute(context);
            var areaReceivers = enemyAreaAttack.Results.SelectMany(hit => hit).Select(hit => hit.Receiver).ToHashSet();
            check(areaReceivers.Contains(pair.First) && areaReceivers.Contains(pair.Second),
                "native enemy area targeting enumerates both true player creatures as independent recipients");
            var selectedAttack = await FromMonsterForExplicitTargeting(enemy.Monster!)
                .Targeting(pair.Second).WithNoAttackerAnim().Execute(context);
            check(selectedAttack.Results.SelectMany(hit => hit).Any(hit => hit.Receiver == pair.Second),
                "native single-target attack can select and resolve against the second body directly");
            Creature[] randomCandidates = state.PlayerCreatures.Where(creature => creature.IsAlive).ToArray();
            check(randomCandidates.Length == 2 && randomCandidates.Contains(pair.First) && randomCandidates.Contains(pair.Second),
                "native combat-target RNG candidate list contains both live twin bodies and no pet/fake target");
            Creature randomTarget = player.RunState.Rng.CombatTargets.NextItem(randomCandidates)
                ?? throw new InvalidOperationException("没有可用的原生随机目标。");
            var randomAttack = await FromMonsterForExplicitTargeting(enemy.Monster!)
                .Targeting(randomTarget).WithNoAttackerAnim().Execute(context);
            check(randomCandidates.Contains(randomTarget)
                  && randomAttack.Results.SelectMany(hit => hit).Any(hit => hit.Receiver == randomTarget),
                "native CombatTargets RNG selects a live twin from PlayerCreatures and a real single-target AttackCommand hits it");

            Creature[] livingEnemies = state.Enemies.Where(creature => creature.IsAlive).ToArray();
            CardModel attackCard = player.Deck.Cards.First(card => card.Type == CardType.Attack);
            var allEnemyAttack = await new AttackCommand(1).FromCard(attackCard, null)
                .TargetingAllOpponents(state).WithNoAttackerAnim().Execute(context);
            CheckEnemyTargeting(allEnemyAttack.Results, livingEnemies, check);

            var sourceSkill = player.Deck.Cards.First(card => card.Type == CardType.Skill
                && card.DynamicVars.Values.OfType<BlockVar>().Any());
            testCard = state.CreateCard(ModelDb.AllCards.First(card => card.Id == sourceSkill.Id), player);
            await CardPileCmd.AddGeneratedCardToCombat(testCard, PileType.Hand, player);
            pair.First.LoseBlockInternal(pair.First.Block);
            pair.Second.LoseBlockInternal(pair.Second.Block);
            int historyBefore = CombatManager.Instance.History.CardPlaysFinished.Count();
            await testCard.OnPlayWrapper(context, null, false, default, true);
            int nativePlayCount = CombatManager.Instance.History.CardPlaysFinished.Count() - historyBefore;
            check(nativePlayCount == 2 && pair.First.Block > 0 && pair.Second.Block > 0,
                "one manual skill produces two native card resolutions and grants block to each body");
            check(testCard.Pile?.Type != PileType.Hand,
                "the doubled resolution still moves and retains only one physical card instance");

            var nativeSave = RunManager.Instance.ToSave(null);
            var serializedPlayer = nativeSave.Players.Single();
            int combinedMaxHp = TwinSlimeRules.RecombinedMaxHp(
                pair.SourceMaxHp, pair.SplitBodyMaxHp, pair.First.MaxHp, pair.Second.MaxHp);
            int combinedCurrentHp = TwinSlimeRules.RecombinedCurrentHp(
                pair.First.CurrentHp, pair.Second.CurrentHp, combinedMaxHp);
            var restoredRun = RunState.FromSerializable(nativeSave);
            string savedJson = restoredRun.Modifiers.OfType<RaceModifier>().Single().TwinSlimeStateJson;
            bool savedPairValid = TwinSlimePersistence.TryRead(savedJson, out var savedPair)
                && savedPair!.RoomKey == RaceRuntime.RoomKey
                && savedPair.FirstCurrentHp == pair.First.CurrentHp
                && savedPair.FirstMaxHp == pair.First.MaxHp
                && savedPair.SecondCurrentHp == pair.Second.CurrentHp
                && savedPair.SecondMaxHp == pair.Second.MaxHp;
            check(serializedPlayer.MaxHp == combinedMaxHp && serializedPlayer.CurrentHp == combinedCurrentHp
                  && restoredRun.Players.Single().Creature.MaxHp == combinedMaxHp
                  && restoredRun.Players.Single().Creature.CurrentHp == combinedCurrentHp
                  && savedPairValid,
                "native save stores recombined hero HP while its modifier payload preserves both exact body pools and the room key");

            await TwinSlimeRuntime.CollapseVerificationSplit(player);
            check(TwinSlimeRuntime.Find(player) == null
                  && original.MaxHp == serializedPlayer.MaxHp
                  && original.CurrentHp == serializedPlayer.CurrentHp,
                "collapsing after save restores the exact combined HP without permanently halving the hero");
            var modifier = RaceModifier.Active!;
            modifier.TwinSlimeStateJson = savedJson;
            await TwinSlimePersistence.RestorePendingRoom(modifier);
            pair = TwinSlimeRuntime.Find(player);
            check(pair != null
                  && pair.First.CurrentHp == savedPair!.FirstCurrentHp
                  && pair.First.MaxHp == savedPair.FirstMaxHp
                  && pair.Second.CurrentHp == savedPair.SecondCurrentHp
                  && pair.Second.MaxHp == savedPair.SecondMaxHp,
                "room replay rebuilds both exact saved bodies without re-halving the merged native HP or granting healing");
            if (pair == null) return;

            await NativeAcceptanceVisuals.Capture("twin-slime-02-restored");

            session.Me.Ghosts = 1;
            await CreatureCmd.Damage(context, pair.Second, pair.Second.CurrentHp + 100,
                ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
            await Wait(() => session.Me.Ghosts == 0, "Ghost consumption did not finish.");
            check(pair.Second.IsAlive && pair.Second.CurrentHp == 1
                  && player.Potions.Any(potion => potion is FairyInABottle)
                  && !tail.WasUsed,
                "after splitting, Ghost prevents death on either body before the native revival hooks");
            
            await CreatureCmd.Damage(context, pair.Second, pair.Second.CurrentHp + 100,
                ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
            check(pair.Second.IsAlive && pair.Second.CurrentHp > 0
                  && player.Potions.All(potion => potion is not FairyInABottle)
                  && !tail.WasUsed,
                "native ShouldDie consumes Fairy in a Bottle before the later ShouldDieLate Tail hook");

            await CreatureCmd.Damage(context, pair.Second, pair.Second.CurrentHp + 100,
                ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
            check(pair.Second.IsAlive && pair.Second.CurrentHp > 0 && tail.WasUsed,
                "native ShouldDieLate Lizard Tail revives the selected second body after Fairy is spent");

            await CreatureCmd.Damage(context, pair.Second, pair.Second.CurrentHp + 100,
                ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
            check(pair.First.IsDead && pair.Second.IsDead && session.Me.Eliminated,
                "once Ghost, Fairy and Tail are spent, death of either twin kills both native bodies and the player");
        }
        finally
        {
            await TwinSlimeRuntime.CollapseVerificationSplit(player);
            if (verificationPuppet is { IsAlive: true })
                await CreatureCmd.Kill(verificationPuppet, true);
            if (testCard != null)
            {
                if (testCard.Pile?.Type.IsCombatPile() == true)
                    await CardPileCmd.RemoveFromCombat(testCard, skipVisuals: true);
                testCard.RemoveFromState();
            }

            if (original.GetPower<StrengthPower>() is { } strength)
            {
                int excess = strength.Amount - originalStrength;
                if (excess > 0)
                    await PowerCmd.Apply<StrengthPower>(context, original, -excess, enemy, null);
            }
            session.Me.Ghosts = originalGhosts;
            tail.WasUsed = originalTailUsed;
            tail.Status = originalTailStatus;
            if (hadFairy && player.Potions.All(potion => potion is not FairyInABottle))
                await PotionCmd.TryToProcure<FairyInABottle>(player);
            await CreatureCmd.SetMaxAndCurrentHp(original, originalMaxHp);
            await CreatureCmd.SetCurrentHp(original, Math.Clamp(originalCurrentHp, 1, originalMaxHp));
        }
    }

    private static AttackCommand FromMonsterForExplicitTargeting(MonsterModel monster)
    {
        // FromMonster sets native TargetingAllOpponents. Clear only that target-mode state so the
        // real AttackCommand.Targeting/TargetingRandomOpponents methods can select a specific body.
        var attack = new AttackCommand(0).FromMonster(monster);
        var combatStateField = typeof(AttackCommand).GetField("_combatState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AttackCommand).FullName, "_combatState");
        combatStateField.SetValue(attack, null);
        return attack;
    }

    private static void CheckEnemyTargeting(
        IEnumerable<IEnumerable<DamageResult>> hits,
        IReadOnlyCollection<Creature> expectedEnemies,
        Action<bool, string> check)
    {
        var receivers = hits.SelectMany(hit => hit).Select(hit => hit.Receiver).ToHashSet();
        check(expectedEnemies.All(receivers.Contains),
            "native card area targeting remains directed at all enemy creatures after player bodies are added");
    }
}