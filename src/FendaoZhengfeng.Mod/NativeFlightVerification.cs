using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeFlightVerification
{
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single();
        var state = player.Creature.CombatState!; var enemies = state.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        var context = new ThrowingPlayerChoiceContext();
        check(enemies.Length >= 3, "flight tests use several actual native enemies");
        session.Me.Effects.Clear();
        foreach (var creature in enemies.Append(player.Creature))
        {
            creature.LoseBlockInternal(creature.Block);
            foreach (var power in creature.Powers.ToArray()) await PowerCmd.Remove(power);
        }
        // Native current-HP setters clamp to max HP. Give every fixture enemy
        // enough capacity for the deliberately ordered 100/95/80 HP board.
        foreach (var enemy in enemies) await CreatureCmd.SetMaxAndCurrentHp(enemy, 5000);
        async Task Board(int first, int second, int remaining)
        {
            for (int i = 0; i < enemies.Length; i++)
            { enemies[i].LoseBlockInternal(enemies[i].Block); await CreatureCmd.SetCurrentHp(enemies[i], i == 0 ? first : i == 1 ? second : Math.Max(1, remaining - i)); }
        }
        var attack = player.Deck.Cards.First(card => card.Type == CardType.Attack);
        await MockTesting.GrantEffect("flight_wings");
        check(EffectIcons.Get("flight_wings").GetWidth() == 128, "flight has a packaged painted icon");
        await Board(100, 95, 80);
        var hits = await new AttackCommand(10).FromCard(attack, null).Targeting(enemies.Last()).WithHitCount(2).WithNoAttackerAnim().Execute(context);
        var hitResults = hits.Results.ToArray();
        check(hitResults.Length == 2 && hitResults[0].Single().Receiver == enemies[0] && hitResults[1].Single().Receiver == enemies[1]
                && enemies[0].CurrentHp == 90 && enemies[1].CurrentHp == 85,
            "each hit recomputes highest current HP rather than keeping the initially selected target");
        int untouched = enemies[2].CurrentHp;
        var area = await new AttackCommand(10).FromCard(attack, null).TargetingAllOpponents(state).WithNoAttackerAnim().Execute(context);
        check(area.Results.Single().Count == 1 && area.Results.Single()[0].Receiver == enemies[0] && enemies[2].CurrentHp == untouched,
            "area attack concentrates into one actual hit instead of duplicating all enemy damage");
        var random = await new AttackCommand(1).FromCard(attack, null).TargetingRandomOpponents(state, false).WithHitCount(2).WithNoAttackerAnim().Execute(context);
        check(!random.IsRandomlyTargeted && random.Results.SelectMany(result => result).All(result => result.Receiver == enemies[1]),
            "random attacks obey the lock without invalid no-duplicate targeting or extra random selection");
        await Board(100, 95, 80);
        var bash = state.CreateCard<Bash>(player);
        check(bash.IsValidTarget(enemies[0]) && !bash.IsValidTarget(enemies.Last()), "native attack-card targeting only accepts the locked enemy");
        await bash.OnPlayWrapper(context, enemies.Last(), true, new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 }, true);
        check(enemies[0].CurrentHp < 100 && enemies[0].GetPowerAmount<VulnerablePower>() == 2 && enemies.Last().GetPowerAmount<VulnerablePower>() == 0,
            "real Bash redirects both its damage and attached debuff to the locked target");
        await Board(70, 70, 50);
        var tied = await new AttackCommand(1).FromCard(attack, null).Targeting(enemies[1]).WithNoAttackerAnim().Execute(context);
        check(tied.Results.Single().Single().Receiver == enemies[0], "equal-HP lock uses stable native enemy order");
        await Board(10, 9, 8);
        var kills = await new AttackCommand(100).FromCard(attack, null).Targeting(enemies[0]).WithHitCount(2).WithNoAttackerAnim().Execute(context);
        check(enemies[0].IsDead && enemies[1].IsDead && kills.Results.Count() == 2,
            "multi-hit attack continues onto the next highest live enemy after its original target dies");
        var dealer = enemies.First(enemy => enemy.IsAlive);
        int hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 10, "flight halves incoming native attack damage");
        hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move | ValueProp.Unpowered, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 10, "unpowered attacks retain the flight reduction");
        hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Unpowered, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 20, "non-attack damage is not reduced by flight");
        await CreatureCmd.GainBlock(player.Creature, 5, ValueProp.Unpowered, null); hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 5 && player.Creature.Block == 0, "flight reduces attack damage before native block is consumed");
        await MockTesting.GrantEffect("flight_wings"); hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 9, "first reinforcement raises attack reduction to fifty-five percent");
        for (int rank = 2; rank <= Balance.FlightMaxRank; rank++) await MockTesting.GrantEffect("flight_wings");
        hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 4 && session.Me.Effects["flight_wings"].Rank == Balance.FlightMaxRank,
            "fully reinforced flight stops at eighty percent attack reduction");
        var holding = session.Me.Effects["flight_wings"]; session.Me.Effects.Remove("flight_wings"); hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 20, ValueProp.Move, dealer, null, null);
        check(player.Creature.CurrentHp == hp - 20, "removing flight restores native incoming attack damage");
        session.Me.Effects["flight_wings"] = holding; CombatEffectDisplay.Tick(); await Task.Delay(100);
        check(NGame.Instance!.GetTree().Root.FindChildren("*", "", true, false).OfType<NPower>()
                .Any(node => node.Model is RaceDisplayPower { EffectId: "flight_wings", Amount: 80 } && node.IsVisibleInTree()),
            "native character status row displays the current flight reduction");
    }
}
