using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativeGamblerVerification
{
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single(); var modifier = RaceModifier.Active!;
        var state = player.Creature.CombatState!; var enemies = state.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        var context = new ThrowingPlayerChoiceContext(); var card = player.Deck.Cards.First(card => card.Type == CardType.Attack);
        session.Me.Effects.Clear();
        foreach (var creature in enemies.Append(player.Creature))
        {
            creature.LoseBlockInternal(creature.Block);
            foreach (var power in creature.Powers.ToArray()) await PowerCmd.Remove(power);
            await CreatureCmd.SetMaxAndCurrentHp(creature, 20000);
        }
        await MockTesting.GrantEffect("gamblers_heart");
        check(EffectIcons.Get("gamblers_heart").GetWidth() == 128, "gambler has a packaged painted icon");
        int rolls = modifier.CriticalRolls;
        for (int i = 0; i < 100; i++)
            checkPreview(modifier.ModifyDamageMultiplicative(enemies[0], 100, ValueProp.Move, player.Creature, card, null) == 1.1m);
        check(modifier.CriticalRolls == rolls && modifier.ActiveCriticalAttacks == 0, "repeated damage previews neither roll criticals nor create attack state");
        static void checkPreview(bool valid) { if (!valid) throw new InvalidOperationException("Preview must include only the fixed ten-percent damage bonus."); }
        int criticals = 0, normal = 0; bool hitValues = true, perAttack = true, checkedPopup = false;
        async Task ObserveCritical()
        {
            if (checkedPopup || !modifier.LastAttackCritical) return;
            check(player.Creature.GetCreatureNode()!.FindChildren("*", "", true, false).OfType<Label>()
                .Any(label => label.Text == "暴击！" && label.IsVisibleInTree()), "actual critical attack shows its visible combat popup");
            checkedPopup = true;
            string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            await Task.Delay(100); Directory.CreateDirectory(directory);
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, "mechanics-05a-critical-popup.png"));
        }
        for (int i = 0; i < 20; i++)
        {
            int before = modifier.CriticalRolls;
            var attack = await new AttackCommand(100).FromCard(card, null).Targeting(enemies[0]).WithHitCount(2).WithNoAttackerAnim().BeforeDamage(ObserveCritical).Execute(context);
            bool critical = modifier.LastAttackCritical; if (critical) criticals++; else normal++;
            hitValues &= attack.Results.SelectMany(hit => hit).All(hit => hit.UnblockedDamage == (critical ? 165 : 110));
            perAttack &= modifier.CriticalRolls == before + 1 && modifier.ActiveCriticalAttacks == 0;
        }
        check(criticals > 0 && normal > 0 && hitValues, "actual native attacks produce both normal 110 and critical 165 damage from base 100");
        check(perAttack, "multi-hit attacks roll once and clear their critical state after completion");
        rolls = modifier.CriticalRolls;
        var area = await new AttackCommand(100).FromCard(card, null).TargetingAllOpponents(state).WithNoAttackerAnim().Execute(context);
        check(modifier.CriticalRolls == rolls + 1 && area.Results.Single().Count == enemies.Length
            && area.Results.Single().All(hit => hit.UnblockedDamage == (modifier.LastAttackCritical ? 165 : 110)),
            "area attacks use one shared critical result for all native targets");
        int hp = player.Creature.CurrentHp; rolls = modifier.CriticalRolls;
        await CreatureCmd.Damage(context, player.Creature, 100, ValueProp.Unpowered, enemies[0], null, null);
        check(player.Creature.CurrentHp == hp - 110 && modifier.CriticalRolls == rolls, "incoming non-attack damage gains ten percent without rolling a critical");
        hp = enemies[0].CurrentHp;
        await CreatureCmd.Damage(context, enemies[0], 100, ValueProp.Unpowered, player.Creature, null, null);
        check(enemies[0].CurrentHp == hp - 110 && modifier.CriticalRolls == rolls, "outgoing non-attack damage gains ten percent without rolling a critical");
        var poison = (await PowerCmd.Apply<PoisonPower>(context, enemies[0], 100, player.Creature, card))!;
        hp = enemies[0].CurrentHp; rolls = modifier.CriticalRolls;
        check(poison.CalculateTotalDamageNextTurn() == 110, "player-applied poison preview includes the fixed damage bonus without rolling criticals");
        await poison.Trigger();
        check(enemies[0].CurrentHp == hp - 110 && modifier.CriticalRolls == rolls, "actual player-applied native poison receives the same bonus as its preview");
        await PowerCmd.Remove(poison);
        poison = (await PowerCmd.Apply<PoisonPower>(context, enemies[0], 100, enemies[1], null))!;
        hp = enemies[0].CurrentHp; await poison.Trigger();
        check(enemies[0].CurrentHp == hp - 100, "enemy-applied poison does not inherit the player's outgoing damage bonus");
        await PowerCmd.Remove(poison);
        hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 100, ValueProp.Unpowered, player.Creature, null, null);
        check(player.Creature.CurrentHp == hp - 110, "self damage receives the ten-percent multiplier once");
        var play = new CardPlay { Card = card, Player = player, Target = enemies[0], ResultPile = PileType.Discard,
            Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 }, IsAutoPlay = false, PlayIndex = 0, PlayCount = 1 };
        rolls = modifier.CriticalRolls; bool groupedValues;
        await using (var grouped = await AttackCommand.CreateContextAsync(state, context, play))
        {
            var first = (await CreatureCmd.Damage(context, enemies[0], 100, ValueProp.Move, player.Creature, card, play)).ToArray(); grouped.AddHit(first);
            var second = (await CreatureCmd.Damage(context, enemies[1], 100, ValueProp.Move, player.Creature, card, play)).ToArray(); grouped.AddHit(second);
            groupedValues = first.Concat(second).All(hit => hit.UnblockedDamage == (modifier.LastAttackCritical ? 165 : 110));
        }
        check(groupedValues && modifier.CriticalRolls == rolls + 1 && modifier.ActiveCriticalAttacks == 0, "native grouped-damage attack context shares a single critical and cleans up on disposal");
        rolls = modifier.CriticalRolls; bool outerCritical = false;
        var nested = await new AttackCommand(100).FromCard(card, null).Targeting(enemies[0]).WithNoAttackerAnim().BeforeDamage(async () =>
        {
            outerCritical = modifier.LastAttackCritical;
            await new AttackCommand(100).FromCard(card, null).Targeting(enemies[1]).WithNoAttackerAnim().Execute(context);
        }).Execute(context);
        check(modifier.CriticalRolls == rolls + 2 && modifier.ActiveCriticalAttacks == 0
            && nested.Results.Single().Single().UnblockedDamage == (outerCritical ? 165 : 110),
            "nested native attacks preserve the outer critical result and clean up both frames");
        bool failed = false;
        try { await new AttackCommand(1).FromCard(card, null).Targeting(enemies[0]).WithNoAttackerAnim().BeforeDamage(() => throw new InvalidOperationException("fixture attack fault")).Execute(context); }
        catch (InvalidOperationException error) when (error.Message == "fixture attack fault") { failed = true; }
        check(failed && modifier.ActiveCriticalAttacks == 0, "failed native attack cannot leak critical state into later damage");
        hp = enemies[0].CurrentHp;
        await CreatureCmd.GainBlock(enemies[0], 100, ValueProp.Unpowered, null);
        await CreatureCmd.Damage(context, enemies[0], 100, ValueProp.Unpowered, player.Creature, null, null);
        check(enemies[0].CurrentHp == hp - 10 && enemies[0].Block == 0, "fixed damage bonus is calculated before native block");
        await MockTesting.GrantEffect("flight_wings"); hp = player.Creature.CurrentHp;
        await CreatureCmd.Damage(context, player.Creature, 100, ValueProp.Move | ValueProp.Unpowered, enemies[0], null, null);
        check(player.Creature.CurrentHp == hp - 55, "flight and gambler incoming multipliers compose once");
        for (int i = 0; i < Balance.CriticalMaxRank; i++) await MockTesting.GrantEffect("gamblers_heart");
        check(Balance.CriticalChance(session.Me.Effects["gamblers_heart"].Rank) == 0.5m, "repeated acquisition reaches the fifty-percent critical cap");
        var saved = StateJson.Deserialize<Match>(StateJson.Serialize(session.Engine.State));
        check(saved.Racers[session.LocalId].Effects["gamblers_heart"].Rank == Balance.CriticalMaxRank, "critical reinforcement survives shared-state serialization");
        await OstyCmd.Summon(context, player, 100, null);
        if (player.Osty!.GetPower<DieForYouPower>() is { } protector) await PowerCmd.Remove(protector);
        rolls = modifier.CriticalRolls;
        var petAttack = await new AttackCommand(100).FromOsty(player.Osty, card, null).Targeting(enemies[0]).WithNoAttackerAnim().Execute(context);
        check(modifier.CriticalRolls == rolls + 1 && petAttack.Results.Single().Single().UnblockedDamage == (modifier.LastAttackCritical ? 165 : 110),
            "owned native Osty attacks share the owner's fixed bonus and critical mechanic");
        CombatEffectDisplay.Tick(); await Task.Delay(100);
        check(NGame.Instance!.GetTree().Root.FindChildren("*", "", true, false).OfType<NPower>()
            .Any(node => node.Model is RaceDisplayPower { EffectId: "gamblers_heart", Amount: 50 } && node.IsVisibleInTree()),
            "native character status row displays the current critical chance");
        session.Me.Effects.Remove("flight_wings"); var holding = session.Me.Effects["gamblers_heart"]; session.Me.Effects.Remove("gamblers_heart");
        hp = player.Creature.CurrentHp; rolls = modifier.CriticalRolls;
        await CreatureCmd.Damage(context, player.Creature, 100, ValueProp.Unpowered, enemies[0], null, null);
        check(player.Creature.CurrentHp == hp - 100 && modifier.CriticalRolls == rolls, "removing gambler restores native damage without further critical rolls");
        session.Me.Effects["gamblers_heart"] = holding; CombatEffectDisplay.Tick();
    }
}
