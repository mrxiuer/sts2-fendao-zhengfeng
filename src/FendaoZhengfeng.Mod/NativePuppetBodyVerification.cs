using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class NativePuppetBodyVerification
{
    private static async Task Wait(Func<bool> ready)
    { long until = System.Environment.TickCount64 + 40000; while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException("Puppet body transition"); await Task.Delay(25); } }
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single(); var hero = player.Creature;
        var context = new ThrowingPlayerChoiceContext(); var state = hero.CombatState!;
        var enemy = state.Enemies.First(creature => creature.IsAlive);
        session.Me.Effects.Clear(); hero.LoseBlockInternal(hero.Block);
        foreach (var power in hero.Powers.ToArray()) await PowerCmd.Remove(power);
        check(Catalog.All.Any(effect => effect.Id == "substitute_puppet" && effect.BossPool && effect.FirstKillPool), "puppet item is available through boss sacrifices and first-kill rewards");
        check(EffectIcons.Get("substitute_puppet").GetWidth() == 128 && PuppetPresentation.Texture().GetHeight() == 512,
            "painted puppet icon and actual body artwork load from packaged transparent resources");
        var puppet = await PuppetFollower.Spawn(player, 80, 77);
        check(puppet.IsPet && puppet.PetOwner == player && puppet.Side == CombatSide.Player && state.Allies.Contains(puppet)
            && puppet.MaxHp == 80 && puppet.CurrentHp == 77 && puppet.GetPowerAmount<BarricadePower>() == 1,
            "actual native follower owns separate health and a real barricade power");
        check(puppet.Monster!.Title.GetFormattedText() == "替身傀儡", "native follower title resolves through the mod localization table");
        bool rejected = false; try { await PuppetFollower.Spawn(player, 80, 80); } catch (RuleException) { rejected = true; }
        check(rejected && state.Allies.Count(creature => creature.Monster is SubstitutePuppet && creature.IsAlive) == 1,
            "a second living substitute cannot be spawned for the same owner");
        await CreatureCmd.GainBlock(hero, 7, ValueProp.Unpowered, null);
        await CreatureCmd.GainBlock(puppet, 25, ValueProp.Unpowered, null);
        int hp = hero.CurrentHp;
        var first = (await CreatureCmd.Damage(context, puppet, 20, ValueProp.Unpowered, enemy, null, null)).ToArray();
        check(puppet.Block == 5 && puppet.CurrentHp == 77 && hero.Block == 7 && hero.CurrentHp == hp
            && first.Single().Receiver == puppet && first.Single().BlockedDamage == 20 && first.Single().WasFullyBlocked,
            "native damage to the follower consumes its own block and preserves the owner's block and HP");
        var directBreak = (await CreatureCmd.Damage(context, puppet, 15, ValueProp.Unpowered, enemy, null, null)).Single();
        check(directBreak.WasBlockBroken && puppet.Block == 0 && puppet.CurrentHp == 67 && hero.Block == 7 && hero.CurrentHp == hp,
            "follower block breaks into its own HP without touching unrelated owner block");
        await CreatureCmd.Damage(context, hero, 20, ValueProp.Unpowered, enemy, null, null);
        check(hero.CurrentHp == hp - 13 && hero.Block == 0 && puppet.CurrentHp == 67,
            "follower damage scope does not leak into a subsequent native owner damage call");
        await CreatureCmd.GainBlock(hero, 3, ValueProp.Unpowered, null);
        await CreatureCmd.GainBlock(puppet, 4, ValueProp.Unpowered, null); hp = hero.CurrentHp;
        await CreatureCmd.Damage(context, new[] { hero, puppet }, 10, ValueProp.Unpowered, enemy, null, null);
        check(hero.CurrentHp == hp - 7 && puppet.CurrentHp == 61 && hero.Block == 0 && puppet.Block == 0,
            "multi-target native damage resolves the two recipients against their own block independently");
        await CreatureCmd.GainBlock(puppet, 30, ValueProp.Unpowered, null);
        int round = state.RoundNumber;
        CombatManager.Instance.SetReadyToEndTurn(player, false);
        await Wait(() => state.RoundNumber > round && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play);
        check(puppet.Block == 30 && puppet.CurrentHp == 61, "real native round transition preserves follower block through barricade");
        var poison = (await PowerCmd.Apply<PoisonPower>(context, puppet, 10, enemy, null))!;
        await poison.Trigger();
        check(puppet.CurrentHp == 51 && puppet.Block == 30, "native poison hurts the follower's own HP and bypasses its retained block");
        await PowerCmd.Remove(poison);
        await PowerCmd.Apply<VulnerablePower>(context, puppet, 2, enemy, null); hp = hero.CurrentHp;
        await CreatureCmd.Damage(context, puppet, 20, ValueProp.Move, enemy, null, null);
        check(puppet.CurrentHp == 51 && puppet.Block == 0 && hero.CurrentHp == hp,
            "native follower vulnerability modifies its incoming attack before its own block");
        await PowerCmd.Apply<FrailPower>(context, puppet, 2, enemy, null);
        await CreatureCmd.GainBlock(puppet, 20, ValueProp.Move, null);
        check(puppet.Block == 15, "native follower frail reduces its own gained block");
        await PowerCmd.Apply<StrengthPower>(context, puppet, -2, enemy, null);
        check(puppet.GetPowerAmount<StrengthPower>() == -2 && hero.GetPowerAmount<StrengthPower>() == 0,
            "native negative attributes remain on the follower and do not overwrite the owner's attributes");
        var node = puppet.GetCreatureNode()!;
        check(node.IsVisibleInTree() && node.FindChildren("*", "", true, false).OfType<NHealthBar>().Any(bar => bar.IsVisibleInTree())
            && node.FindChildren("*", "", true, false).OfType<NPower>().Any(power => power.Model is BarricadePower),
            "follower renders a native health bar and its own power row");
        check(node.Hitbox.GetGlobalRect().Position.X > hero.GetCreatureNode()!.Hitbox.GetGlobalRect().End.X,
            "follower body and native health row have their own space ahead of the owner");
        var sprite = node.Visuals.Body.GetChildren().OfType<Sprite2D>().Single(); float before = sprite.Rotation;
        await Task.Delay(200);
        check(Math.Abs(sprite.Rotation - before) > 0.00001f, "actual follower artwork moves during combat idle");
        hp = hero.CurrentHp; int puppetHp = puppet.CurrentHp;
        var nativeEnemyAttack = await new AttackCommand(0).FromMonster(enemy.Monster!).WithNoAttackerAnim().Execute(context);
        check(nativeEnemyAttack.Results.SelectMany(hit => hit).All(hit => hit.Receiver == hero) && puppet.CurrentHp == puppetHp,
            "native enemy target enumeration excludes the pet rather than adding an extra area-attack recipient");
        await Capture("puppet-01-native-body");
        await CreatureCmd.Kill(puppet, true);
        check(puppet.IsDead && hero.IsAlive && hero.CurrentHp == hp && !session.Me.Eliminated,
            "native follower death removes the pet without killing or damaging its owner");
        await Task.Delay(800);
        var replacement = await PuppetFollower.Spawn(player, 101, 33);
        check(replacement != puppet && PuppetFollower.Find(player) == replacement && replacement.CurrentHp == 33 && replacement.MaxHp == 101,
            "a dead follower can be replaced by a separate native instance with precise restored vitals");
        await CreatureCmd.Heal(replacement, 10);
        check(replacement.CurrentHp == 43 && hero.CurrentHp == hp, "follower healing modifies its own actual health only");
        await Capture("puppet-02-native-replacement");
        ((SubstitutePuppet)replacement.Monster!).GuardsOwner = true;
        hero.LoseBlockInternal(hero.Block); hp = hero.CurrentHp;
        await CreatureCmd.GainBlock(replacement, 25, ValueProp.Unpowered, null);
        var guarded = (await CreatureCmd.Damage(context, hero, 20, ValueProp.Move, enemy, null, null)).ToArray();
        check(hero.CurrentHp == hp && replacement.CurrentHp == 43 && replacement.Block == 5
            && guarded.Single(result => result.Receiver == replacement).BlockedDamage == 20
            && guarded.Single(result => result.Receiver == replacement).WasFullyBlocked
            && guarded.Single(result => result.Receiver == hero).BlockedDamage == 0,
            "guarded native attack consumes actual follower block and assigns block history to the follower");
        guarded = (await CreatureCmd.Damage(context, hero, 15, ValueProp.Move, enemy, null, null)).ToArray();
        check(hero.CurrentHp == hp && replacement.CurrentHp == 33 && replacement.Block == 0
            && guarded.Single(result => result.Receiver == replacement).WasBlockBroken
            && !guarded.Single(result => result.Receiver == hero).WasBlockBroken,
            "breaking follower block damages the follower and does not send a false owner block-break event");
        await PowerCmd.Apply<VulnerablePower>(context, replacement, 2, enemy, null);
        await CreatureCmd.Damage(context, hero, 10, ValueProp.Move, enemy, null, null);
        check(replacement.CurrentHp == 18 && hero.CurrentHp == hp,
            "guarded attacks use the receiving follower's native vulnerability");
        await PowerCmd.Remove(replacement.GetPower<VulnerablePower>()!);
        await CreatureCmd.Damage(context, hero, 4, ValueProp.Unpowered | ValueProp.Unblockable, enemy, null, null);
        check(hero.CurrentHp == hp - 4 && replacement.CurrentHp == 18,
            "unpowered direct HP loss is not silently converted into an intercepted attack");
        hp = hero.CurrentHp;
        guarded = (await CreatureCmd.Damage(context, hero, 30, ValueProp.Move, enemy, null, null)).ToArray();
        check(replacement.IsDead && hero.CurrentHp == hp - 12
            && guarded.Single(result => result.Receiver == replacement).UnblockedDamage == 18
            && guarded.Single(result => result.Receiver == hero).UnblockedDamage == 12,
            "native overkill consumes remaining follower HP and spills only the excess to its owner");
        hp = hero.CurrentHp;
        await CreatureCmd.Damage(context, hero, 10, ValueProp.Move, enemy, null, null);
        check(hero.CurrentHp == hp - 10 && PuppetFollower.Guard(hero) == null,
            "subsequent attack hits return to the owner immediately after follower death");
        await Task.Delay(800);
        var final = await PuppetFollower.Spawn(player, 20, 5);
        ((SubstitutePuppet)final.Monster!).GuardsOwner = true;
        hp = hero.CurrentHp;
        var multi = await new AttackCommand(10).FromMonster(enemy.Monster!).WithHitCount(2).WithNoAttackerAnim().Execute(context);
        check(final.IsDead && hero.CurrentHp == hp - 15 && multi.Results.Count() == 2,
            "one real native multihit attack spills its first hit and targets the owner normally on its second hit");
    }
    private static async Task Capture(string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(500); Directory.CreateDirectory(directory);
        NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png"));
    }
}
