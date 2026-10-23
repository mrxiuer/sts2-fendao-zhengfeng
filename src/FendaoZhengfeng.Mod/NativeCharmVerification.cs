using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>Native combat checks for real redirected attacks, powers, intent targets and turn expiry.</summary>
internal static class NativeCharmVerification
{
    private static readonly FieldInfo? IntentTargetsField = typeof(NIntent).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic);

    private static async Task Wait(Func<bool> ready, string reason)
    {
        long until = Environment.TickCount64 + 40000;
        while (!ready())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(reason);
            await Task.Delay(30);
        }
    }

    private static async Task RemovePower<T>(Creature creature) where T : PowerModel
    {
        foreach (T power in creature.Powers.OfType<T>().ToArray())
            await PowerCmd.Remove(power);
    }

    private static Creature[] ReadIntentTargets(NCreature creatureNode)
    {
        if (IntentTargetsField == null) return [];
        return creatureNode.IntentContainer.GetChildren().OfType<NIntent>()
            .SelectMany(intent => (IntentTargetsField.GetValue(intent) as IEnumerable<Creature>) ?? [])
            .Distinct()
            .ToArray();
    }

    private static async Task AdvanceEnemyTurn(Player player, ICombatState combat, string label)
    {
        int oldRound = combat.RoundNumber;
        CombatManager.Instance.SetReadyToEndTurn(player, false);
        await Wait(() => combat.RoundNumber > oldRound && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, label);
    }

    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var player = run.Players.Single();
        Creature hero = player.Creature;
        ICombatState combat = hero.CombatState ?? throw new InvalidOperationException("牵心丝原生验证需要处于战斗中。");
        var context = new ThrowingPlayerChoiceContext();
        Creature[] enemies = combat.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        string[] actualMonsterTypes = enemies.Select(enemy => enemy.Monster?.GetType().Name ?? "").OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] expectedMonsterTypes = ["LeafSlimeM", "LeafSlimeS", "TwigSlimeM", "TwigSlimeS"];
        check(actualMonsterTypes.SequenceEqual(expectedMonsterTypes),
            "native test uses the fixed four-slime SlimesNormal encounter");
        if (enemies.Length < 2)
        {
            check(false, "native charm tests require at least two living enemies");
            return;
        }

        foreach (Creature creature in enemies.Append(hero))
        {
            await CreatureCmd.SetMaxAndCurrentHp(creature, 5000);
            creature.LoseBlockInternal(creature.Block);
            await RemovePower<CharmRedirectPower>(creature);
            await RemovePower<WeakPower>(creature);
            await RemovePower<StrengthPower>(creature);
            await RemovePower<ArtifactPower>(creature);
        }

        var granted = new List<CharmCard>();
        string firstReceipt = "charm-verification-" + Guid.NewGuid().ToString("N");
        string secondReceipt = "charm-verification-" + Guid.NewGuid().ToString("N");
        try
        {
            int initialCount = player.Deck.Cards.OfType<CharmCard>().Count();
            bool firstAdded = await CharmCardGrant.AddToDeck(run, player, firstReceipt);
            CharmCard firstCard = player.Deck.Cards.OfType<CharmCard>().Single(card => card.GrantReceiptId == firstReceipt);
            granted.Add(firstCard);
            bool replayAdded = await CharmCardGrant.AddToDeck(run, player, firstReceipt);
            bool secondAdded = await CharmCardGrant.AddToDeck(run, player, secondReceipt);
            CharmCard secondCard = player.Deck.Cards.OfType<CharmCard>().Single(card => card.GrantReceiptId == secondReceipt);
            granted.Add(secondCard);
            check(firstAdded && !replayAdded && secondAdded
                && player.Deck.Cards.OfType<CharmCard>().Count() == initialCount + 2,
                "each distinct permanent grant receipt adds one card and replaying the same receipt adds none");
            check(firstCard.EnergyCost.Canonical == 2 && firstCard.Type == CardType.Skill
                && firstCard.TargetType == TargetType.AnyEnemy && firstCard.CanonicalKeywords.Contains(CardKeyword.Exhaust)
                && firstCard.MaxUpgradeLevel == 0,
                "魅惑 is a permanent 2-cost enemy-targeting Exhaust skill with no upgrade variant");
            CardModel decoded = NativeCheckpoint.DecodeCard(NativeCheckpoint.EncodeCard(firstCard), run, player);
            try
            {
                check(decoded is CharmCard decodedCharm && decodedCharm.GrantReceiptId == firstReceipt,
                    "the permanent grant receipt survives native card save serialization");
            }
            finally { decoded.RemoveFromState(); }

            Creature source = enemies.FirstOrDefault(enemy => enemy.Monster!.MoveStateMachine!.States.Values
                .OfType<MoveState>().Any(move => move.Intents.Any(intent => intent is AttackIntent))) ?? enemies[0];
            Creature[] others = enemies.Where(enemy => enemy != source).ToArray();
            var charm = await firstCard.ApplyTo(context, source);
            check(charm is { Amount: 2 } && charm.Owner == source && charm.Applier == hero,
                "playing 魅惑 installs a native two-enemy-turn debuff on the selected enemy");

            var displayed = combat.CreateCard<CharmCard>(player);
            await CardPileCmd.AddGeneratedCardToCombat(displayed, PileType.Hand, player);
            await NativeAcceptanceVisuals.CaptureCard("charm-01-native-card-status", displayed);

            if (source.GetCreatureNode() is { } sourceNode && source.Monster is { } sourceMonster)
            {
                Creature[] intentTargets = ReadIntentTargets(sourceNode);
                check(IntentTargetsField != null && sourceNode.IntentContainer.GetChildren().OfType<NIntent>().Any()
                    && intentTargets.Length == others.Length && others.All(intentTargets.Contains),
                    "the native rendered intent receives the real other-enemy target list");

                int singleTargetHeroHp = hero.CurrentHp;
                var singleTargetHp = others.ToDictionary(enemy => enemy, enemy => enemy.CurrentHp);
                await CreatureCmd.Damage(context, hero, 1, ValueProp.Move, source, null, null);
                check(hero.CurrentHp == singleTargetHeroHp && others[0].CurrentHp < singleTargetHp[others[0]]
                    && others.Skip(1).All(enemy => enemy.CurrentHp == singleTargetHp[enemy]),
                    "a real single-target native damage command hits one other enemy only");

                int groupTargetHeroHp = hero.CurrentHp;
                var groupTargetHp = others.ToDictionary(enemy => enemy, enemy => enemy.CurrentHp);
                var nativeGroupAttack = await new AttackCommand(1).FromMonster(sourceMonster).WithNoAttackerAnim().Execute(context);
                Creature[] groupReceivers = nativeGroupAttack.Results.SelectMany(result => result)
                    .Select(result => result.Receiver).Distinct().ToArray();
                check(hero.CurrentHp == groupTargetHeroHp && others.All(enemy => enemy.CurrentHp < groupTargetHp[enemy])
                    && groupReceivers.Length == others.Length && others.All(groupReceivers.Contains),
                    "the native all-opponents attack redirects to every other enemy without including its source or the player");

                MoveState? attackMove = sourceMonster.MoveStateMachine?.States.Values.OfType<MoveState>()
                    .FirstOrDefault(move => move.Intents.Any(intent => intent is AttackIntent));
                check(attackMove != null, "the fixed native slime has a real attack-intent move");
                if (attackMove != null)
                {
                    sourceMonster.SetMoveImmediate(attackMove, forceTransition: true);
                    await sourceNode.UpdateIntent(CharmTargeting.Recipients(source, group: true));
                    int heroHp = hero.CurrentHp;
                    var hpBefore = others.ToDictionary(enemy => enemy, enemy => enemy.CurrentHp);
                    await sourceMonster.PerformMove();
                    check(hero.CurrentHp == heroHp && others.Any(enemy => enemy.CurrentHp < hpBefore[enemy]),
                        "the monster's real native attack action hits another enemy and leaves the player untouched");
                }
            }
            else
            {
                check(false, "native source has a live combat node and monster model for intent verification");
            }

            await AdvanceEnemyTurn(player, combat, "牵心丝第一轮敌方行动未结束。");
            check(source.GetPowerAmount<CharmRedirectPower>() == 1,
                "the first real enemy side turn consumes exactly one duration");
            await AdvanceEnemyTurn(player, combat, "牵心丝第二轮敌方行动未结束。");
            check(!source.HasPower<CharmRedirectPower>(),
                "the native redirect ends after the charmed enemy completes its second real turn");

            foreach (Creature creature in enemies.Append(hero))
            {
                await RemovePower<WeakPower>(creature);
                await RemovePower<StrengthPower>(creature);
                await RemovePower<ArtifactPower>(creature);
            }
            charm = await firstCard.ApplyTo(context, source);
            check(charm is { Amount: 2 }, "a later play starts a fresh two-turn charm after the prior charm expired");
            Creature existingBuffOwner = hero;
            var existingStrength = await PowerCmd.Apply<StrengthPower>(context, existingBuffOwner, 1, hero, null);
            var routedBuff = await CharmPowerCmd.Apply<StrengthPower>(context, source, 2, source, null);
            check(existingStrength != null && ReferenceEquals(existingStrength, routedBuff)
                && routedBuff?.Owner == hero && hero.GetPowerAmount<StrengthPower>() == 3
                && source.GetPowerAmount<StrengthPower>() == 0,
                "a self-buff stacks on the player's existing native instance and returns that real player-owned instance");

            var redirectedNegativeStrength = await CharmPowerCmd.Apply<StrengthPower>(context, hero, -2, source, null);
            check(redirectedNegativeStrength?.Owner == others[0]
                && redirectedNegativeStrength.Amount == -2
                && hero.GetPowerAmount<StrengthPower>() == 3,
                "negative Strength is classified by its signed native power type and returns the redirected enemy-owned instance");

            Creature artifactTarget = others[^1];
            await PowerCmd.Apply<ArtifactPower>(context, artifactTarget, 1, artifactTarget, null);
            var existingWeak = await PowerCmd.Apply<WeakPower>(context, others[0], 1, null, null);
            IReadOnlyList<WeakPower> routedWeak = await CharmPowerCmd.Apply<WeakPower>(context, [hero], 2, source, null);
            Godot.GD.Print("[FDZF CHARM GROUP] " + string.Join("; ", others.Select(enemy => $"{enemy.Monster?.GetType().Name}:weak={enemy.GetPowerAmount<WeakPower>()},artifact={enemy.GetPowerAmount<ArtifactPower>()}")) + $"; returned={routedWeak.Count};same={ReferenceEquals(existingWeak, routedWeak.FirstOrDefault(power => power.Owner == others[0]))};hero={hero.GetPowerAmount<WeakPower>()}");
            check(existingWeak != null && ReferenceEquals(existingWeak, routedWeak.FirstOrDefault(power => power.Owner == others[0]))
                && others[0].GetPowerAmount<WeakPower>() == 3
                && others.Skip(1).Take(others.Length - 2).All(enemy => enemy.GetPowerAmount<WeakPower>() == 2)
                && artifactTarget.GetPowerAmount<ArtifactPower>() == 0 && artifactTarget.GetPowerAmount<WeakPower>() == 0
                && routedWeak.All(power => others.Contains(power.Owner)),
                "group-debuff routing stacks on existing native powers, keeps their real owners, and lets Artifact block one recipient");

            CharmRedirectPower? fallbackCharm = source.GetPower<CharmRedirectPower>();
            if (fallbackCharm is null)
            {
                fallbackCharm = await firstCard.ApplyTo(context, source);
            }
            for (int pass = 0; pass < 8 && combat.Enemies.Any(enemy => enemy != source && enemy.IsAlive); pass++)
            {
                foreach (Creature other in combat.Enemies.Where(enemy => enemy != source && enemy.IsAlive).ToArray())
                    await CreatureCmd.Kill(other, true);
                await Task.Delay(60);
            }
            Creature[] remaining = combat.Enemies.Where(enemy => enemy.IsAlive).ToArray();
            check(remaining.Length == 1 && remaining[0] == source,
                "native deaths leave the charmed source as the only living enemy for the fallback case");
            if (remaining.Length == 1)
            {
                int heroHp = hero.CurrentHp;
                int sourceHp = source.CurrentHp;
                var fallbackWeak = await CharmPowerCmd.Apply<WeakPower>(context, [hero], 1, source, null);
                check(fallbackWeak.Count == 1 && fallbackWeak[0].Owner == source
                    && source.GetPowerAmount<WeakPower>() > 0 && hero.GetPowerAmount<WeakPower>() == 0,
                    "when no other enemy lives, a negative status returns to the charmed enemy itself");
                await CreatureCmd.Damage(context, hero, 20, ValueProp.Move, source, null, null);
                check(hero.CurrentHp == heroHp && source.CurrentHp < sourceHp,
                    "a real native attack with no other enemy damages the charmed source instead of the player");
            }
        }
        finally
        {
            foreach (Creature creature in combat.Enemies.Append(hero).Distinct())
            {
                await RemovePower<CharmRedirectPower>(creature);
                await RemovePower<WeakPower>(creature);
                await RemovePower<StrengthPower>(creature);
                await RemovePower<ArtifactPower>(creature);
            }
            foreach (CharmCard card in granted)
            {
                if (player.Deck.Cards.Contains(card))
                    await CardPileCmd.RemoveFromDeck(card, false);
                else if (!card.HasBeenRemovedFromState)
                    card.RemoveFromState();
            }
        }
    }
}