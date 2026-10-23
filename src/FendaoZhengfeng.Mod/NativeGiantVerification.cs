using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeGiantVerification
{
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!;
        var player = run.Players.Single();
        var hero = player.Creature;
        var state = hero.CombatState!;
        var enemies = state.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        var context = new ThrowingPlayerChoiceContext();
        var attackCard = player.Deck.Cards.First(card => card.Type == CardType.Attack);
        int originalHp = hero.CurrentHp, originalMaxHp = hero.MaxHp;

        session.Me.Effects.Clear();
        session.Engine.State.EnabledEffects.Add(GiantForm.EffectId);
        check(GiantForm.CanExecute(99, 100, 0) && !GiantForm.CanExecute(100, 100, 0)
            && GiantForm.CanExecute(109, 100, 1) && !GiantForm.CanExecute(110, 100, 1),
            "giant keeps strict less-than at base and repeated thresholds");

        if (enemies.Length < 2)
        {
            check(false, "native giant checks need at least two live enemies for boundary and combo cases");
            return;
        }

        async Task Prepare(Creature enemy, int hp)
        {
            enemy.LoseBlockInternal(enemy.Block);
            await CreatureCmd.SetMaxAndCurrentHp(enemy, hp);
        }

        async Task<AttackCommand> Hit(Creature target, int hitCount = 1)
        {
            var command = new AttackCommand(1).FromCard(attackCard, null).Targeting(target).WithNoAttackerAnim();
            if (hitCount > 1) command.WithHitCount(hitCount);
            return await command.Execute(context);
        }

        async Task RestoreHero()
        {
            await CreatureCmd.SetMaxHp(hero, originalMaxHp);
            await CreatureCmd.SetCurrentHp(hero, originalHp);
        }

        try
        {
            await CreatureCmd.SetMaxAndCurrentHp(hero, 100);
            NCreature? nativeHero = hero.GetCreatureNode();
            float baseScale = nativeHero?.Visuals?.DefaultScale ?? 1f;
            await MockTesting.GrantEffect(GiantForm.EffectId);
            check(session.Me.Effects[GiantForm.EffectId].Rank == 0, "first giant grant uses base rank zero");

            bool doubled = await WaitForScale(nativeHero, baseScale * 2f);
            check(doubled, "giant doubles the player's native character visual scale");

            foreach (var enemy in enemies) await Prepare(enemy, 5000);
            var first = enemies[0];
            await Prepare(first, 100);
            var equal = await Hit(first);
            check(first.IsAlive && equal.Results.SelectMany(hit => hit).Single().Receiver == first,
                "an enemy whose maximum HP equals current player HP is not executed");

            await Prepare(first, 99);
            await CreatureCmd.GainBlock(first, 5, ValueProp.Unpowered, null);
            var baseKill = await Hit(first);
            var baseResult = baseKill.Results.SelectMany(hit => hit).Single();
            check(first.IsDead && first.CurrentHp == 0 && first.MaxHp == 99 && baseResult.WasTargetKilled
                && baseResult.UnblockedDamage >= 99,
                "strictly lower maximum HP dies through a blocked native attack with a real lethal DamageResult");

            await MockTesting.GrantEffect(GiantForm.EffectId);
            check(session.Me.Effects[GiantForm.EffectId].Rank == 1, "repeated giant increases rank once");
            float repeatedScale = baseScale * GiantForm.ScaleMultiplier(1);
            check(await WaitForScale(nativeHero, repeatedScale), "a repeated giant raises scale to 2.1 times base");

            var second = enemies[1];
            await Prepare(second, 110);
            var repeatedEqual = await Hit(second);
            check(second.IsAlive && repeatedEqual.Results.SelectMany(hit => hit).Single().Receiver == second,
                "a target exactly at the repeated 1.1x HP threshold still survives");

            await Prepare(second, 109);
            var repeatedKill = await Hit(second);
            var repeatedResult = repeatedKill.Results.SelectMany(hit => hit).Single();
            check(second.IsDead && repeatedResult.WasTargetKilled,
                "one duplicate raises the strict execution threshold to below 1.1 times current HP");

            async Task<Creature> NewTarget(int maxHp)
            {
                var creature = await CreatureCmd.Add(ModelDb.GetById<MonsterModel>(enemies[0].ModelId).ToMutable(), state);
                await Prepare(creature, maxHp); return creature;
            }
            var areaTarget = await NewTarget(109);
            var board = state.Enemies.Where(enemy => enemy.IsAlive).ToArray();
            var area = await new AttackCommand(1).FromCard(attackCard, null)
                .TargetingAllOpponents(state).WithNoAttackerAnim().Execute(context);
            var areaResults = area.Results.Single().ToArray();
            check(areaResults.Length == board.Length
                && areaResults.Select(result => result.Receiver).Distinct().Count() == board.Length
                && areaResults.Any(result => result.Receiver == areaTarget && result.WasTargetKilled)
                && areaTarget.IsDead && board.Where(enemy => enemy != areaTarget).All(enemy => enemy.IsAlive),
                "an area attack executes each qualifying target independently and preserves other targets");

            var comboTarget = await NewTarget(109);
            var combo = await Hit(comboTarget, 2);
            var comboResults = combo.Results.ToArray();
            check(comboResults.Length == 1 && comboResults[0].Single().Receiver == comboTarget && comboTarget.IsDead,
                "a fixed-target native multihit stops after execution instead of hitting a removed enemy");
            var flightTarget = await NewTarget(109);
            foreach (var enemy in state.Enemies.Where(enemy => enemy.IsAlive && enemy != flightTarget))
                await CreatureCmd.SetCurrentHp(enemy, 10);
            session.Me.Effects["flight_wings"] = new Holding { Id = "flight_wings" };
            var retargeted = await Hit(flightTarget, 2);
            var nextHits = retargeted.Results.ToArray();
            check(flightTarget.IsDead && nextHits.Length == 2
                && nextHits[0].Single().Receiver == flightTarget
                && nextHits[1].Single().Receiver != flightTarget,
                "flight retargets the next hit after a giant execution using actual remaining enemies");
            session.Me.Effects.Remove("flight_wings");

            check(GiantForm.ScaleMultiplier(10) == 3f,
                "repeated giant visual scale stops at the documented 3x cap");
        }
        finally
        {
            await RestoreHero();
        }
    }

    private static async Task<bool> WaitForScale(NCreature? node, float expected)
    {
        if (node?.Visuals == null) return false;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (Math.Abs(node.Visuals.DefaultScale - expected) < 0.01f) return true;
            await Task.Delay(20);
        }
        return false;
    }
}

