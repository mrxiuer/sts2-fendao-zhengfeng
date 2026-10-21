using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeEvilEchoVerification
{
    internal static async Task Run(RunState run, Action<bool, string> check)
    {
        var session = RaceSession.Current!; var player = run.Players.Single();
        var enemies = player.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive).ToArray();
        var target = enemies[0]; var other = enemies[1]; var context = new ThrowingPlayerChoiceContext();
        var source = player.Deck.Cards.First();
        await MockTesting.GrantEffect("evil_echo");
        check(EffectIcons.Get("evil_echo").GetWidth() == 128, "evil echo has a packaged painted icon");
        int weak = target.GetPowerAmount<WeakPower>();
        await PowerCmd.Apply<WeakPower>(context, target, 2, player.Creature, source);
        check(target.GetPowerAmount<WeakPower>() == weak + 4, "evil echo doubles native debuff duration");
        await PowerCmd.ModifyAmount(context, target.GetPower<WeakPower>()!, -1, player.Creature, source);
        check(target.GetPowerAmount<WeakPower>() == weak + 3, "evil echo never amplifies negative debuff removal");
        int poison = target.GetPowerAmount<PoisonPower>();
        await PowerCmd.Apply<PoisonPower>(context, target, 3, player.Creature, null);
        check(target.GetPowerAmount<PoisonPower>() == poison + 6, "attributed relic-style poison doubles without requiring a card source");
        int strength = target.GetPowerAmount<StrengthPower>();
        await PowerCmd.Apply<StrengthPower>(context, target, -2, player.Creature, source);
        check(target.GetPowerAmount<StrengthPower>() == strength - 4, "negative native strength is treated as an enemy debuff");
        await PowerCmd.Apply<StrengthPower>(context, target, 3, player.Creature, source);
        check(target.GetPowerAmount<StrengthPower>() == strength - 1, "positive enemy buffs are not amplified");
        int selfWeak = player.Creature.GetPowerAmount<WeakPower>();
        await PowerCmd.Apply<WeakPower>(context, player.Creature, 2, player.Creature, source);
        check(player.Creature.GetPowerAmount<WeakPower>() == selfWeak + 2, "self debuffs remain unchanged");
        int otherWeak = other.GetPowerAmount<WeakPower>();
        await PowerCmd.Apply<ArtifactPower>(context, other, 1, null, null);
        await PowerCmd.Apply<WeakPower>(context, other, 2, player.Creature, source);
        check(other.GetPowerAmount<ArtifactPower>() == 0 && other.GetPowerAmount<WeakPower>() == otherWeak,
            "one native artifact blocks one doubled application without a second application");
        await PowerCmd.Apply<WeakPower>(context, other, 2, null, null);
        check(other.GetPowerAmount<WeakPower>() == otherWeak + 2, "unattributed mode and system powers are not amplified");
        int baseStrength = target.GetPowerAmount<StrengthPower>();
        await PowerCmd.Apply<PiercingWailPower>(context, target, 2, player.Creature, source);
        check(target.GetPowerAmount<PiercingWailPower>() == 4 && target.GetPowerAmount<StrengthPower>() == baseStrength - 4,
            "temporary strength loss doubles once rather than quadrupling its internal strength");
        await PowerCmd.Apply<PiercingWailPower>(context, target, 1, player.Creature, source);
        check(target.GetPowerAmount<PiercingWailPower>() == 6 && target.GetPowerAmount<StrengthPower>() == baseStrength - 6,
            "stacking an existing temporary debuff scales its internal delta only once");
        await target.GetPower<PiercingWailPower>()!.AfterSideTurnEnd(context, CombatSide.Enemy, [target]);
        check(target.GetPowerAmount<PiercingWailPower>() == 0 && target.GetPowerAmount<StrengthPower>() == baseStrength,
            "temporary debuff expiration restores exactly the scaled strength amount");
        await PowerCmd.Apply<StrengthPower>(context, target, -1, player.Creature, source);
        check(target.GetPowerAmount<StrengthPower>() == baseStrength - 2, "temporary application scope does not leak into later permanent stat changes");
        CombatEffectDisplay.Tick(); await Task.Delay(100);
        check(NGame.Instance!.GetTree().Root.FindChildren("*", "", true, false).OfType<NPower>()
                .Any(node => node.Model is RaceDisplayPower { EffectId: "evil_echo", Amount: 2 } && node.IsVisibleInTree()),
            "native character status row displays the current evil echo multiplier");
        await MockTesting.GrantEffect("evil_echo"); weak = target.GetPowerAmount<WeakPower>();
        await PowerCmd.Apply<WeakPower>(context, target, 1, player.Creature, source);
        check(target.GetPowerAmount<WeakPower>() == weak + 3, "repeated acquisition increases debuff multiplier to three");
        var saved = StateJson.Deserialize<Match>(StateJson.Serialize(session.Engine.State));
        check(Balance.EvilEchoMultiplier(saved.Racers[session.LocalId].Effects["evil_echo"].Rank) == 3,
            "evil echo rank survives shared-state save and restore");
    }
}
