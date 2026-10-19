using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

/// <summary>Explicit local simulation. It uses native gameplay and the same rules reducer, without a network peer.</summary>
internal static class MockTesting
{
    private static RaceSession Session => RaceSession.Current is { IsMock: true } session ? session : throw new InvalidOperationException("这些工具仅可在本地模拟模式使用。");
    public static async Task Start()
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("游戏尚未就绪。");
        if (RaceRuntime.State != null) throw new InvalidOperationException("请先返回主菜单，再开启本地模拟。");
        var characters = ModelDb.AllCharacters.ToList();
        int selected = await RaceUi.Choose("本地模拟 · 选择角色（不需要好友）", characters.Select(c => c.Title.GetFormattedText()).ToList());
        if (selected < 0) return;
        await StartWithCharacter(characters[selected], "FDZFMOCK" + DateTime.UtcNow.Ticks);
        RaceUi.Error("本地模拟已开始：默认没有诅咒。测试工具位于“分道争锋”面板，效果图标在遗物区常驻。");
        RaceUi.Refresh();
    }
    internal static async Task<RunState> StartWithCharacter(CharacterModel character, string seed)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("游戏尚未就绪。");
        var session = RaceSession.CreateMock(seed);
        RaceRuntime.ResumeRequested = false;
        try
        {
            return await game.StartNewSingleplayerRun(character, false, ActModel.GetDefaultList(), [ModelDb.Modifier<RaceModifier>().ToMutable()], seed, GameMode.Standard);
        }
        catch { session.Dispose(); throw; }
    }
    public static async Task SelectEffect(EffectKind kind)
    {
        _ = Session;
        var effects = Catalog.All.Where(e => e.Kind == kind && Session.Engine.State.EnabledEffects.Contains(e.Id)).ToList();
        int selected = await RaceUi.Choose(kind == EffectKind.Curse ? "模拟对手献祭 · 给自己施加诅咒" : "本地测试 · 取得自身效果", effects.Select(e => e.Name + " · " + RaceUi.TierName(e.Tier) + "\n" + e.Description).ToList(), effects.Select(e => EffectIcons.Get(e.Id)).ToList());
        if (selected >= 0) await GrantEffect(effects[selected].Id);
    }
    internal static async Task GrantEffect(string id)
    {
        var session = Session;
        var effect = Catalog.Get(id);
        string actor = effect.Kind == EffectKind.Curse ? "2" : session.LocalId;
        string reward = "mock-" + Guid.NewGuid().ToString("N") + "|0";
        string offer = actor + ":" + reward;
        // Debug fixture supplies a reward; resolution still uses the ordinary atomic transaction.
        session.Engine.State.Racers[actor].WonBattles.Add(reward.Split('|')[0]);
        session.Engine.State.Offers[offer] = new Offer
        {
            Id = offer, Player = actor, RewardKey = reward, Candidates = [effect.Id],
            Route = effect.Kind switch { EffectKind.Curse => Route.Disrupt, EffectKind.Contract => Route.Contract, _ => Route.Grow }
        };
        await session.Submit(session.New("choose", offer, effect.Id) with { Actor = actor });
        if (RaceModifier.Active is { } modifier)
        {
            await modifier.ApplyReceipts(new ThrowingPlayerChoiceContext(), session.Me.InBattle);
            await modifier.UpdateMaxHp();
        }
        RaceUi.Refresh();
    }
    public static async Task EnterBattle()
    {
        _ = Session;
        if (CombatManager.Instance.IsInProgress) throw new InvalidOperationException("请先结算当前测试战斗。");
        int selected = await RaceUi.Choose("进入原生测试战斗", ["普通怪物", "精英", "Boss"]);
        if (selected < 0) return;
        await RunManager.Instance.EnterRoomDebug(selected switch { 0 => RoomType.Monster, 1 => RoomType.Elite, _ => RoomType.Boss });
    }
    public static async Task WinBattle()
    {
        _ = Session;
        if (!CombatManager.Instance.IsInProgress || RaceRuntime.State?.Players.Single().Creature.CombatState is not { } combat) throw new InvalidOperationException("当前没有正在进行的战斗。");
        // Some elites spawn another creature when killed; settle those waves too.
        for (int wave = 0; wave < 24; wave++)
        {
            foreach (var enemy in combat.Enemies.Where(enemy => enemy.IsAlive).ToList()) await CreatureCmd.Kill(enemy, true);
            await CombatManager.Instance.CheckWinCondition();
            if (!Session.Me.InBattle || CombatManager.Instance.IsOverOrEnding) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("特殊敌人的阶段尚未结束，请手动完成战斗后继续测试。");
    }
    public static async Task SelectService()
    {
        _ = Session;
        int selected = await RaceUi.Choose("进入净化测试节点", ["商店（可使用测试金币）", "营地"]);
        if (selected >= 0) await EnterService(selected == 0 ? RoomType.Shop : RoomType.RestSite);
    }
    internal static async Task EnterService(RoomType type)
    {
        _ = Session;
        if (CombatManager.Instance.IsInProgress) throw new InvalidOperationException("请先结算当前测试战斗。");
        if (type is not (RoomType.Shop or RoomType.RestSite)) throw new InvalidOperationException("不是可用的测试服务节点。");
        await RunManager.Instance.EnterRoomDebug(type);
    }
    public static async Task OpponentBoss()
    {
        var session = Session;
        int act = RaceRuntime.State!.CurrentActIndex;
        var peer = session.Engine.State.Racers["2"];
        if (peer.Act > act) throw new InvalidOperationException("模拟对手已经领先当前幕。");
        while (peer.Act < act)
        {
            await session.Submit(session.New("progress", amount: 80, value: "Boss") with { Actor = "2", Act = peer.Act + 1 });
            peer = session.Engine.State.Racers["2"];
        }
        string key = "mock-boss-" + Guid.NewGuid().ToString("N");
        await session.Submit(session.New("battle_start", key) with { Actor = "2" });
        await session.Submit(session.New("victory", key, boss: true) with { Actor = "2" });
        RaceUi.Refresh();
    }
    public static async Task Gold()
    {
        _ = Session;
        await PlayerCmd.GainGold(500, RaceRuntime.State!.Players.Single());
    }
    public static async Task OpponentGold()
    {
        var session = Session;
        await session.Submit(session.New("gold", amount: 100) with { Actor = "2" });
    }
}
