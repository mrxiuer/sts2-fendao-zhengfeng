using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace FendaoZhengfeng.Mod;

// The follower is an actual native pet with its own HP, block and powers.
// It is not exposed in the reward pool until the full item lifecycle is ready.
public sealed class SubstitutePuppet : MonsterModel
{
    // Enabled only by the unfinished item lifecycle or the dedicated native fixture.
    internal bool GuardsOwner { get; set; }
    internal int Generation { get; set; }
    public override LocString Title => new(ModEntry.Id, "puppet.name");
    public override int MinInitialHp => 1;
    public override int MaxInitialHp => 1;
    public override bool HasDeathSfx => false;
    public override float DeathAnimLengthOverride => 0.65f;
    public override bool ShouldShowInCompendium => false;
    public override IEnumerable<string> AssetPaths => [];
    protected override MonsterMoveStateMachine GenerateMoveStateMachine()
    {
        var wait = new MoveState("PUPPET_GUARD", _ => Task.CompletedTask);
        wait.FollowUpState = wait;
        return new MonsterMoveStateMachine([wait], wait);
    }
}

internal static class PuppetFollower
{
    internal static Creature? Find(Player player) => player.Creature.CombatState?.Allies
        .FirstOrDefault(creature => creature.Monster is SubstitutePuppet && creature.PetOwner == player && creature.IsAlive);
    internal static Creature? Guard(Creature owner) => owner.Player is { } player && Find(player) is { Monster: SubstitutePuppet { GuardsOwner: true } } puppet ? puppet : null;
    internal static Creature? AttackGuard(Creature owner, MegaCrit.Sts2.Core.ValueProps.ValueProp props)
        => props.HasFlag(MegaCrit.Sts2.Core.ValueProps.ValueProp.Move) ? Guard(owner) : null;
    internal static async Task<Creature> Spawn(Player player, int maxHp, int currentHp)
    {
        if (maxHp < 1 || currentHp < 1 || currentHp > maxHp) throw new RuleException(L10n.T("傀儡生命无效。"));
        if (Find(player) != null) throw new RuleException(L10n.T("已有存活的替身傀儡。"));
        var creature = await PlayerCmd.AddPet<SubstitutePuppet>(player);
        await CreatureCmd.SetMaxAndCurrentHp(creature, maxHp);
        await CreatureCmd.SetCurrentHp(creature, currentHp);
        await PowerCmd.Apply<BarricadePower>(new ThrowingPlayerChoiceContext(), creature, 1, player.Creature, null);
        return creature;
    }
}
