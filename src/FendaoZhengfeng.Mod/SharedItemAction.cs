using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

// Native ActionTypes discovers INetAction implementations in loaded mods.
// Item clicks therefore use the same ordering and player-choice machinery as cards and potions.
public struct NetSharedItemAction : INetAction
{
    public string EffectId;
    public bool InCombat;
    public GameAction ToGameAction(Player player) => new SharedItemAction(player, EffectId, InCombat);
    public void Serialize(PacketWriter writer) { RaceMessage.Write(writer, EffectId, 40); writer.WriteBool(InCombat); }
    public void Deserialize(PacketReader reader) { EffectId = RaceMessage.Read(reader, 40); InCombat = reader.ReadBool(); }
}

internal sealed class SharedItemAction(Player player, string effectId, bool inCombat) : GameAction
{
    public override ulong OwnerId => player.NetId;
    public override GameActionType ActionType => inCombat ? GameActionType.CombatPlayPhaseOnly : GameActionType.NonCombat;
    public override INetAction ToNetAction() => new NetSharedItemAction { EffectId = effectId, InCombat = inCombat };
    internal static void Enqueue(string effect)
    {
        var owner = RaceRuntime.LocalPlayer() ?? throw new RuleException("尚未进入战局。");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new SharedItemAction(owner, effect, CombatManager.Instance.IsInProgress));
    }
    protected override async Task ExecuteAction()
    {
        if (RaceSession.Current is not { } session || !session.Engine.State.Synchronous
            || RaceRuntime.RacerFor(player) is not { } racer || !racer.Has(effectId)) { return; }
        var modifier = RaceModifier.ForPlayer(player)!;
        var context = new GameActionPlayerChoiceContext(this);
        string operation = "active:" + Id;
        try
        {
            switch (effectId)
            {
                case "small_light":
                    await modifier.RemoveWithSmallLight(context: context, operation: operation);
                    break;
                case "good_connections":
                    await AdvancedEffects.BuyCardShared(player, context, operation);
                    break;
                case "tail_refill":
                    if (inCombat || CombatManager.Instance.IsInProgress || !player.Relics.OfType<LizardTail>().Any(tail => tail.WasUsed))
                        throw new RuleException("请在非战斗时补充已消耗的蜥蜴尾巴。");
                    await SharedChoice.Profile(player, operation);
                    await SharedChoice.Commit(player, "tail_refill", operation);
                    await modifier.ApplyReceipts(context, false);
                    break;
                case BurrowRules.EffectId:
                    await BurrowEffect.Activate(true, player);
                    break;
                default: return;
            }
            RaceUi.Refresh();
        }
        catch (RuleException error)
        {
            if (player == RaceRuntime.LocalPlayer()) RaceUi.Error(error.Message);
        }
    }
    public override string ToString() => $"SharedItemAction {OwnerId} {effectId}";
}
