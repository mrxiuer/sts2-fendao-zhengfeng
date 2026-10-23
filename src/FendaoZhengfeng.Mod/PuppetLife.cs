using System.Globalization;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal sealed record PuppetVitals(int Generation, int InitialMaxHp, int MaxHp, int CurrentHp);

public sealed partial class RaceModifier
{
    private PuppetVitals? puppetVitals;
    private HashSet<string> puppetReceipts = [];
    private bool applyingPuppet;
    [SavedProperty] public string PuppetVitalsJson
    {
        get { if (!IsCanonical && ReferenceEquals(this, Active) && !applyingPuppet) CapturePuppetVitals(); return StateJson.Serialize(puppetVitals); }
        set => puppetVitals = string.IsNullOrWhiteSpace(value) || value == "null" ? null : StateJson.Deserialize<PuppetVitals>(value);
    }
    [SavedProperty] public string PuppetReceiptsJson
    { get => StateJson.Serialize(puppetReceipts); set => puppetReceipts = StateJson.Deserialize<HashSet<string>>(value); }
    internal string PuppetStatusText
    {
        get
        {
            CapturePuppetVitals();
            return puppetVitals is { } saved
                ? L10n.F($"\n傀儡生命 {saved.CurrentHp}/{saved.MaxHp}；每次强化增加{(int)Math.Ceiling(saved.InitialMaxHp * 0.25m)}最大生命并恢复等量生命。")
                : "";
        }
    }
    internal static bool HasPuppet(Creature creature) => RaceRuntime.Live && Active is { } modifier
        && creature.Player == modifier.Player && modifier.Me.Has(PuppetRules.Id);
    private void CapturePuppetVitals()
    {
        if (puppetVitals is { } stored && Player.Creature.CombatState?.Allies.FirstOrDefault(creature =>
            creature.PetOwner == Player && creature.Monster is SubstitutePuppet puppet && puppet.Generation == stored.Generation) is { } live)
            puppetVitals = stored with { CurrentHp = live.CurrentHp, MaxHp = live.MaxHp };
    }
    internal async Task ApplyPuppetReceipt(Receipt receipt, bool inCombat)
    {
        if (puppetReceipts.Contains(receipt.Id)) return;
        if (!Me.Effects.TryGetValue(PuppetRules.Id, out var holding)
            || !int.TryParse(receipt.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int generation)
            || PuppetRules.Generation(holding) != generation) return;
        CapturePuppetVitals();
        if (receipt.Kind == "puppet_create")
        {
            if (puppetVitals?.Generation != generation)
                puppetVitals = new(generation, receipt.Amount, receipt.Amount, receipt.Amount);
        }
        else
        {
            if (puppetVitals?.Generation != generation) throw new RuleException(L10n.T("傀儡初始状态尚未恢复。"));
            puppetVitals = puppetVitals with { MaxHp = checked(puppetVitals.MaxHp + receipt.Amount),
                CurrentHp = checked(puppetVitals.CurrentHp + receipt.Amount) };
        }
        // The desired saved state and receipt token are committed together
        // before native animations can yield to a save request.
        puppetReceipts.Add(receipt.Id);
        applyingPuppet = true;
        try
        {
            if (inCombat && Player.Creature.CombatState != null && puppetVitals is { } stats)
            {
                var live = PuppetFollower.Find(Player);
                if (live == null) live = await PuppetFollower.Spawn(Player, stats.MaxHp, stats.CurrentHp);
                else
                {
                    await CreatureCmd.SetMaxHp(live, stats.MaxHp);
                    await CreatureCmd.SetCurrentHp(live, stats.CurrentHp);
                }
                int ownerBlock = Player.Creature.Block;
                if (!((SubstitutePuppet)live.Monster!).GuardsOwner && ownerBlock > 0)
                { Player.Creature.LoseBlockInternal(ownerBlock); live.GainBlockInternal(ownerBlock); }
                ((SubstitutePuppet)live.Monster!).Generation = stats.Generation;
                ((SubstitutePuppet)live.Monster!).GuardsOwner = true;
            }
        }
        finally { applyingPuppet = false; }
    }
    internal async Task BeforePuppetCombat()
    {
        await ApplyReceipts(Context(), true, puppetOnly: true);
        if (!Me.Effects.TryGetValue(PuppetRules.Id, out var holding)) { puppetVitals = null; return; }
        if (puppetVitals == null || puppetVitals.Generation != PuppetRules.Generation(holding))
            throw new RuleException(L10n.T("傀儡存档与取得记录不一致。"));
        if (puppetVitals.CurrentHp <= 0)
        { await S.Submit(S.New("puppet_destroyed", puppetVitals.Generation.ToString(CultureInfo.InvariantCulture))); puppetVitals = null; return; }
        var live = PuppetFollower.Find(Player) ?? await PuppetFollower.Spawn(Player, puppetVitals.MaxHp, puppetVitals.CurrentHp);
        ((SubstitutePuppet)live.Monster!).Generation = puppetVitals.Generation;
        ((SubstitutePuppet)live.Monster!).GuardsOwner = true;
    }
    internal async Task AfterPuppetDeath(Creature creature, bool prevented)
    {
        if (prevented || creature.Monster is not SubstitutePuppet { GuardsOwner: true } puppet || creature.PetOwner != Player) return;
        await S.Submit(S.New("puppet_destroyed", puppet.Generation.ToString(CultureInfo.InvariantCulture)));
        if (puppetVitals?.Generation == puppet.Generation) puppetVitals = null;
    }
    public override async Task AfterCombatEnd(CombatRoom room)
    {
        CapturePuppetVitals();
        if (PuppetFollower.Find(Player) is { } live) live.LoseBlockInternal(live.Block);
        await BurrowEffect.AfterCombatEnd();
    }
}

// Healing restrictions depend on ownership, so they also apply between fights.
// Native zero-HP revivals remain allowed, as required by the mode's survival rules.
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
internal static class PuppetNoHealingCommandPatch
{
    static bool Prefix(Creature creature, ref Task __result)
    { if (creature.CurrentHp <= 0 || !RaceModifier.HasPuppet(creature)) return true; __result = Task.CompletedTask; return false; }
}
[HarmonyPatch(typeof(Creature), nameof(Creature.HealInternal))]
internal static class PuppetNoDirectHealingPatch
{
    static bool Prefix(Creature __instance, decimal amount)
        => amount <= 0 || __instance.CurrentHp <= 0 || !RaceModifier.HasPuppet(__instance);
}
