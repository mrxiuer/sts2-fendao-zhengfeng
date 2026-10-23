namespace FendaoZhengfeng.Core;

/// <summary>Persisted, deterministic state transitions for the Burrow Sanctuary boon.</summary>
public static class BurrowRules
{
    public const string EffectId = "burrow_sanctuary";
    public const int MaxCharges = 2;
    public const int BaseWinsPerCharge = 3;

    private const string ChargesKey = "burrow_sanctuary.charges";
    private const string ProgressKey = "burrow_sanctuary.progress";
    private const string PhaseKey = "burrow_sanctuary.phase";
    private const string PhaseBattleKey = "burrow_sanctuary.phase_battle";
    private const string UseTurnKey = "burrow_sanctuary.use_turn";
    private const string PhaseTurnKey = "burrow_sanctuary.phase_turn";
    private const string BlockBrokenKey = "burrow_sanctuary.block_broken";
    private const string VictoryMarkerPrefix = "burrow_sanctuary:victory:";

    public enum Phase { Ready, Burrowed, Stunned }

    public readonly record struct Snapshot(int Charges, int Progress, int WinsPerCharge, Phase Phase, int UseTurn, int PhaseTurn, bool BlockBroken);

    public static int WinsPerChargeForRank(int rank) => Math.Max(1, BaseWinsPerCharge - Math.Max(0, rank));

    public static Snapshot Read(Racer racer)
    {
        int charges = Math.Clamp(racer.History.GetValueOrDefault(ChargesKey), 0, MaxCharges);
        int rank = racer.Effects.TryGetValue(EffectId, out var holding) ? Math.Max(0, holding.Rank) : 0;
        int goal = WinsPerChargeForRank(rank);
        int progress = Math.Clamp(racer.History.GetValueOrDefault(ProgressKey), 0, goal - 1);
        bool phaseBelongsToCurrentBattle = racer.History.GetValueOrDefault(PhaseBattleKey) == BattleNumber(racer);
        var phase = phaseBelongsToCurrentBattle
            ? Enum.IsDefined(typeof(Phase), racer.History.GetValueOrDefault(PhaseKey))
                ? (Phase)racer.History.GetValueOrDefault(PhaseKey) : Phase.Ready
            : Phase.Ready;
        int useTurn = phaseBelongsToCurrentBattle ? Math.Max(0, racer.History.GetValueOrDefault(UseTurnKey)) : 0;
        int phaseTurn = phaseBelongsToCurrentBattle ? Math.Max(0, racer.History.GetValueOrDefault(PhaseTurnKey)) : 0;
        bool blockBroken = phaseBelongsToCurrentBattle && racer.History.GetValueOrDefault(BlockBrokenKey) != 0;
        return new(charges, progress, goal, phase, useTurn, phaseTurn, blockBroken);
    }

    /// <summary>
    /// Call only after the victory reducer has added battleKey to WonBattles.
    /// The per-battle marker also protects callers from re-applying a victory.
    /// </summary>
    public static Snapshot RecordVictory(Racer racer, string battleKey)
    {
        if (!racer.Has(EffectId) || !racer.WonBattles.Contains(battleKey)) return Read(racer);
        if (!racer.UsedServices.Add(VictoryMarkerPrefix + battleKey)) return Read(racer);

        int charges = Read(racer).Charges;
        if (charges >= MaxCharges)
        {
            racer.History[ProgressKey] = 0;
            return Read(racer);
        }

        int goal = WinsPerChargeForRank(racer.Effects[EffectId].Rank);
        int progress = Math.Max(0, racer.History.GetValueOrDefault(ProgressKey));
        // A reinforcement may lower the goal below already accumulated progress.
        // Award at most one charge for this victory; never bank progress at the cap.
        if (progress >= goal)
        {
            racer.History[ChargesKey] = charges + 1;
            racer.History[ProgressKey] = 0;
        }
        else
        {
            progress++;
            if (progress >= goal)
            {
                racer.History[ChargesKey] = charges + 1;
                racer.History[ProgressKey] = 0;
            }
            else racer.History[ProgressKey] = progress;
        }
        return Read(racer);
    }

    /// <summary>Record a native AfterBlockBroken event while the player is underground.</summary>
    public static Snapshot MarkBlockBroken(Racer racer, string battleKey)
    {
        EnsureBattle(racer, battleKey);
        var state = Read(racer);
        if (state.Phase == Phase.Burrowed && !state.BlockBroken)
            racer.History[BlockBrokenKey] = 1;
        return Read(racer);
    }

    /// <summary>Reducer for the authoritative, idempotent "burrow_use" command.</summary>
    public static Snapshot Use(Racer racer, string battleKey, int turn)
    {
        EnsureBattle(racer, battleKey);
        if (!racer.Has(EffectId)) throw new RuleException("尚未取得地底庇护。");
        if (turn is < 1 or > 10000) throw new RuleException("当前回合无效，不能遁地。");
        var state = Read(racer);
        if (state.Charges <= 0) throw new RuleException("地底庇护没有储存次数。");
        if (state.Phase != Phase.Ready) throw new RuleException("地底庇护仍在冷却中。");
        racer.History[ChargesKey] = state.Charges - 1;
        SetPhase(racer, Phase.Burrowed, turn, turn, blockBroken: false);
        return Read(racer);
    }

    /// <summary>Reducer for "burrow_emerge" when the first turn after burrowing starts.</summary>
    public static Snapshot Emerge(Racer racer, string battleKey, int turn)
    {
        EnsureBattle(racer, battleKey);
        var state = Read(racer);
        if (state.Phase != Phase.Burrowed || turn <= state.UseTurn)
            throw new RuleException("地底庇护当前不能出土。");
        if (state.BlockBroken) SetPhase(racer, Phase.Stunned, state.UseTurn, turn, blockBroken: true);
        else SetPhase(racer, Phase.Ready, 0, 0, blockBroken: false);
        return Read(racer);
    }

    /// <summary>Reducer for "burrow_ready" after the stunned turn has actually been skipped.</summary>
    public static Snapshot FinishStun(Racer racer, string battleKey, int turn)
    {
        EnsureBattle(racer, battleKey);
        var state = Read(racer);
        if (state.Phase != Phase.Stunned || turn <= state.PhaseTurn)
            throw new RuleException("眩晕回合尚未结束。");
        SetPhase(racer, Phase.Ready, 0, 0, blockBroken: false);
        return Read(racer);
    }

    /// <summary>
    /// Undo only the in-flight use when native combat is reconstructed from its
    /// opening turn. The old command stays committed; restoring its reserved
    /// charge lets the player choose a new action on the replayed timeline.
    /// </summary>
    public static Snapshot ResetForNativeReplay(Racer racer, string battleKey)
    {
        EnsureBattle(racer, battleKey);
        var state = Read(racer);
        if (state.Phase == Phase.Ready) return state;
        racer.History[ChargesKey] = Math.Min(MaxCharges, state.Charges + 1);
        SetPhase(racer, Phase.Ready, 0, 0, blockBroken: false);
        return Read(racer);
    }

    /// <summary>Clear only temporary combat status; stored charges and victory progress persist.</summary>
    public static Snapshot CleanupBattle(Racer racer, string battleKey)
    {
        if (battleKey == racer.BattleKey) SetPhase(racer, Phase.Ready, 0, 0, blockBroken: false);
        return Read(racer);
    }

    /// <summary>Set a clean phase boundary when a new native battle is committed.</summary>
    public static Snapshot BeginBattle(Racer racer)
    {
        if (racer.History.GetValueOrDefault(PhaseBattleKey) != BattleNumber(racer)) SetPhase(racer, Phase.Ready, 0, 0, blockBroken: false);
        return Read(racer);
    }

    private static void EnsureBattle(Racer racer, string battleKey)
    {
        if (!racer.InBattle || racer.BattleKey != battleKey || racer.Finished || racer.ResetPending)
            throw new RuleException("地底庇护的战斗记录不匹配。");
    }

    private static void SetPhase(Racer racer, Phase phase, int useTurn, int phaseTurn, bool blockBroken)
    {
        racer.History[PhaseKey] = (int)phase;
        racer.History[PhaseBattleKey] = BattleNumber(racer);
        racer.History[UseTurnKey] = useTurn;
        racer.History[PhaseTurnKey] = phaseTurn;
        racer.History[BlockBrokenKey] = blockBroken ? 1 : 0;
    }

    private static int BattleNumber(Racer racer) => (int)Math.Clamp(racer.Battle, 0, int.MaxValue);
}
