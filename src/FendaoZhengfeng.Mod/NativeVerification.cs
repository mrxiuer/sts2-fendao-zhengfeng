using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

/// <summary>Opt-in verification in the actual Godot process, using the game's in-memory save backend.</summary>
internal static class NativeVerification
{
    private static bool started;
    public static void MaybeRun(NGame game)
    {
        if (started || System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") != "1") return;
        started = true;
        if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") is "host" or "client") _ = NativeNetworkVerification.Run(game);
        else _ = Verify(game);
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("验证失败：" + name); GD.Print("[FDZF VERIFY PASS] " + name); }
    private static async Task CaptureVisual(NGame game, string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Delay(350);
        Directory.CreateDirectory(directory);
        var result = game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png"));
        if (result != Error.Ok) throw new IOException("实机截图保存失败：" + result);
    }
    private static async Task Verify(NGame game)
    {
        try
        {
            await game.GameStartupComplete;
            GD.Print("[FDZF VERIFY] Starting isolated native checks");
            var mock = new SaveManager(new MockGodotFileIo("user://fdzf-memory-verification"));
            mock.InitProfileId(1); mock.InitSettingsDataForTest(); mock.InitPrefsDataForTest(); mock.InitProgressData();
            mock.SetFtuesEnabled(false);
            SaveManager.MockInstanceForTesting(mock);
            const string seed = "FDZFVERIFY";
            var run = await MockTesting.StartWithCharacter(ModelDb.Character<Ironclad>(), seed);
            var session = RaceSession.Current!;
            Check(session.IsMock && session.Engine.State.Racers.Count == 2, "interactive mock starts locally with one simulated opponent");
            Check(run.Players.Count == 1, "personal run has one player");
            Check(run.Players.Single().Relics.Any(r => r is LizardTail), "starting Lizard Tail granted");
            await RunManager.Instance.EnterRoomDebug(RoomType.Elite);
            var player = run.Players.Single();
            long until = System.Environment.TickCount64 + 30000;
            while ((RaceModifier.Active?.Turn ?? 0) < 1 || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play) { if (System.Environment.TickCount64 > until) throw new TimeoutException("回合启动超时。"); await Task.Delay(30); }
            Check(session.Me.Effects.Count == 0 && PileType.Hand.GetPile(player).Cards.Concat(PileType.Draw.GetPile(player).Cards).Where(card => !card.EnergyCost.CostsX).All(card => card.EnergyCost.GetWithModifiers(CostModifiers.All) == card.EnergyCost.Canonical), "clean mock opening has no forced effects or doubled card costs");
            await CaptureVisual(game, "mock-01-clean-opening");
            await MockTesting.WinBattle();
            while (session.Me.InBattle) await Task.Delay(30);
            foreach (var e in Catalog.All.Where(e => e.Kind == EffectKind.Curse || e.Kind == EffectKind.Contract))
                session.Me.Effects[e.Id] = new() { Id = e.Id, Grants = [new() { EventId = e.Id, Source = "2", ActiveBattle = session.Me.Battle + 1 }] };
            session.Me.Effects["battle_hardened"] = new() { Id = "battle_hardened" }; session.Me.VictoryCount = 20;
            session.Me.Effects["death_defiance"] = new() { Id = "death_defiance" }; session.Me.RevivalCount = 2;
            session.Me.Effects["shared_misery"] = new() { Id = "shared_misery" };
            await RunManager.Instance.EnterRoomDebug(RoomType.Elite);
            until = System.Environment.TickCount64 + 30000;
            while (session.Me.Battle < 2 || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play) { if (System.Environment.TickCount64 > until) throw new TimeoutException("第二场测试战斗启动超时。"); await Task.Delay(30); }
            await Task.Delay(250);
            var active = RaceModifier.Active!;
            Check(session.Me.BurdenSnapshot == 27, "curse burden activated at battle boundary");
            Check(player.Creature.GetPowerAmount<StrengthPower>() == -1, "strength curse applied");
            Check(player.Creature.GetPowerAmount<DexterityPower>() == -1, "dexterity curse applied");
            Check(player.Creature.GetPowerAmount<DoomPower>() == 3, "borrowed time applies Doom");
            Check(active.Dice is >= 80 and <= 110, "damage dice generated within range");
            Check(player.PlayerCombatState!.Energy >= 6, "combined opening energy applied");
            Check(PileType.Hand.GetPile(player).Cards.OfType<ClutterCard>().Count() == 2, "two clutter cards after normal draw");
            Check(player.Creature.MaxHp == 96, "historical revival max HP scaling");
            await CreatureCmd.GainBlock(player.Creature, 20, ValueProp.Unpowered, null);
            Check(player.Creature.Block == 0, "all-in prevents first-turn block");
            var ctx = new ThrowingPlayerChoiceContext();
            await CardPileCmd.AddGeneratedCardToCombat(player.Creature.CombatState!.CreateCard<Slimed>(player), PileType.Discard, null);
            Check(session.Engine.State.Racers["2"].PendingStatuses.Count == 1, "enemy-origin status queues for opponent");
            var source = player.Deck.Cards.First(); var packet = NativeCheckpoint.EncodeCard(source); var copy = NativeCheckpoint.DecodeCard(packet, run, player);
            Check(copy.Id == source.Id && copy.CurrentUpgradeLevel == source.CurrentUpgradeLevel, "native card serialization round trip");
            var enemy = player.Creature.CombatState!.Enemies[0];
            var attack = PileType.Hand.GetPile(player).Cards.First(c => c.Type == CardType.Attack);
            int beforeHp = player.Creature.CurrentHp;
            await CreatureCmd.Damage(ctx, enemy, 10, ValueProp.Unpowered, player.Creature, attack, null);
            int afterFirst = player.Creature.CurrentHp;
            await CreatureCmd.Damage(ctx, enemy, 10, ValueProp.Unpowered, player.Creature, attack, null);
            Check(afterFirst == beforeHp - 1 && player.Creature.CurrentHp == afterFirst, "barbs triggers once per card and enemy across multiple hits");
            int openingHp = enemy.MaxHp;
            await CreatureCmd.Damage(ctx, enemy, 9999, ValueProp.Unpowered, player.Creature, null, null);
            int cappedHp = enemy.CurrentHp;
            Check(cappedHp == openingHp - (int)Math.Ceiling(openingHp * 0.4), "hard shell caps actual native HP loss");
            await CreatureCmd.Damage(ctx, enemy, 9999, ValueProp.Unpowered, player.Creature, null, null);
            Check(enemy.CurrentHp == cappedHp, "hard shell does not reset between hits");
            session.Me.Ghosts = 1;
            await CreatureCmd.Damage(ctx, player.Creature, 9999, ValueProp.Unpowered, player.Creature.CombatState!.Enemies[0], null, null);
            Check(player.Creature.CurrentHp == 1 && session.Me.Ghosts == 0, "scapegoat preserves one HP and consumes one charge");
            Check(session.Me.RevivalCount == 2, "scapegoat does not count as revival");
            Check(!player.Relics.OfType<LizardTail>().Single().WasUsed, "scapegoat precedes native resurrection");
            Check(Catalog.All.All(effect => EffectIcons.Get(effect.Id).GetWidth() == 64), "all 29 effect icons load through native SVG renderer");
            EffectHud.Refresh();
            Check(EffectHud.IsVisible && EffectHud.IconCount == session.Me.Effects.Count, "effect icons stay visible near relics with race panel closed");
            var heldContract = session.Me.Effects["bitter_sweet"];
            session.Me.Effects.Remove("bitter_sweet");
            Check(attack.EnergyCost.GetWithModifiers(CostModifiers.All) == 1, "without contract native card cost stays unchanged");
            session.Me.Effects["bitter_sweet"] = heldContract;
            heldContract.Grants[0].ActiveBattle = session.Me.Battle + 1;
            Check(attack.EnergyCost.GetWithModifiers(CostModifiers.All) == 1, "pending contract does not alter current combat cost");
            heldContract.Grants[0].ActiveBattle = session.Me.Battle;
            Check(attack.EnergyCost.GetWithModifiers(CostModifiers.All) == 2, "active contract doubles only the opening manual card cost");
            async Task<int> PlayProbe(bool automatic)
            {
                var originalSkill = player.Deck.Cards.First(card => card.Type == CardType.Skill);
                var skill = player.Creature.CombatState!.CreateCard(ModelDb.AllCards.First(card => card.Id == originalSkill.Id), player);
                await CardPileCmd.AddGeneratedCardToCombat(skill, PileType.Hand, player);
                int before = CombatManager.Instance.History.CardPlaysFinished.Count();
                await skill.OnPlayWrapper(new ThrowingPlayerChoiceContext(), null, automatic, default, true);
                return CombatManager.Instance.History.CardPlaysFinished.Count() - before;
            }
            int firstExecutions = await PlayProbe(false);
            Check(active.ManualCards == 1 && attack.EnergyCost.GetWithModifiers(CostModifiers.All) == 1, "after first native manual play remaining hand costs return to normal");
            int secondExecutions = await PlayProbe(false);
            int autoExecutions = await PlayProbe(true);
            Check(firstExecutions == 1 && secondExecutions == 1 && autoExecutions == 1 && active.ManualCards == 2, "automatic native card play neither counts nor receives third-card replay");
            int thirdExecutions = await PlayProbe(false);
            Check(thirdExecutions == 2 && active.ManualCards == 3, "third native manual card executes exactly twice");
            Check(attack.EnergyCost.GetWithModifiers(CostModifiers.All) == 1, "later manual cards retain normal cost");
            int activeCurse = session.Me.Count("exhaustion");
            await MockTesting.GrantEffect("exhaustion");
            Check(session.Me.Count("exhaustion") == activeCurse && session.Me.Count("exhaustion", false) == activeCurse + 1, "interactive mock opponent attack queues without changing current fight");
            int boonRank = session.Me.Effects["battle_hardened"].Rank;
            await MockTesting.GrantEffect("battle_hardened");
            Check(session.Me.Effects["battle_hardened"].Rank == boonRank + 1, "interactive mock grants repeated boon through normal rules transaction");
            await CaptureVisual(game, "mock-02-persistent-effect-icons");
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR")))
            {
                _ = MockTesting.SelectEffect(EffectKind.Boon);
                await CaptureVisual(game, "mock-03-effect-test-menu");
            }
            else
            {
                await MockTesting.WinBattle();
                while (session.Me.InBattle) await Task.Delay(30);
                await MockTesting.EnterService(RoomType.Shop);
                await MockTesting.Gold();
                int balance = player.Gold;
                int price = Balance.CleansePrice(Catalog.Get("exhaustion"), session.Me.Count("exhaustion", false))!.Value;
                await session.Submit(session.New("shop_cleanse", RaceRuntime.RoomKey, "exhaustion", balance));
                await active.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
                Check(RaceRuntime.State!.CurrentRoom is MerchantRoom && !session.Me.Effects.ContainsKey("exhaustion") && player.Gold == balance - price, "interactive mock shop deducts native gold and cleanses all stacks");
                await MockTesting.EnterService(RoomType.RestSite);
                Check(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption.Generate(player).OfType<RaceRestOption>().Any(), "interactive mock camp exposes native cleanse option");
                await session.Submit(session.New("camp_cleanse", RaceRuntime.RoomKey, "imbalance"));
                Check(!session.Me.Effects.ContainsKey("imbalance"), "interactive mock camp cleanse uses ordinary-only rules");
            }
            GD.Print("[FDZF VERIFY COMPLETE] Native checks passed; no player save backend was used.");
            game.GetTree().Quit(0);
        }
        catch (Exception e) { GD.PushError("[FDZF VERIFY FAILED] " + e); game.GetTree().Quit(1); }
    }
}
