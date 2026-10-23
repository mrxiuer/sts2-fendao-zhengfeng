# 牵心丝（heartstrings_charm）实现说明

## 效果与接入

获得 `heartstrings_charm` 时，每个独立获取收据永久加入一张《魅惑》：2 费、技能、指定敌人、消耗。重复获取仍保留，每次再加入一张。请在父级收据处理器中使用 `Kind = "charm_card"`、`Id = choiceId + ":charm"`，调用：

```csharp
await CharmCardGrant.AddToDeck(run, player, receipt.Id);
```

该方法在卡牌的 `[SavedProperty] GrantReceiptId` 中保存收据 ID。同一收据重放不会重复加牌；不同收据各自增加一张。效果注册、配置/奖励入口和卡面由父级接线；本实现未登记 `Catalog`，也未添加占位美术。

《魅惑》通过原生 `PowerCmd.Apply<CharmRedirectPower>` 施加两层持续时间。每当被魅惑敌人完成一次敌方行动回合，原生 `AfterSideTurnEnd` 钩子减少一层；其他敌人的行动和玩家回合不消耗时长。目标重选、伤害、状态创建和 Artifact 抵挡均继续经过游戏原生命令。

## 原生目标路由

- `AttackCommand.GetPossibleTargets` 根据原生命令的单体或群体标志选一个或全部其他存活敌人；没有其他存活敌人时回到魅惑来源。
- `MoveState.PerformMove` 将真实怪物行动委托收到的敌方目标候选转给其他存活敌人；`MonsterModel.RollMove` 同步改写真实 AI 选招目标。出牌后通过 `NCreature.UpdateIntent` 刷新游戏原生意图节点，验证器读取的是 `NIntent` 内实际保存的目标。
- `CreatureCmd.Damage(IEnumerable<Creature>, ...)` 覆盖不经 `AttackCommand` 的原生移动伤害，只处理 `ValueProp.Move`，并按原目标集合判断单体或群体。
- `PowerCmd.Apply<T>` 根据 `ModelDb.Power<T>().GetTypeForAmount(amount)` 判断实际正负类型。敌方负面状态转给其他敌人；群体状态转给所有其他存活敌人；无其他敌人时施加者接收状态。敌人给自己施加的增益转给施放《魅惑》的玩家。
- Debuff 如果最终仍指向玩家，再交给 `PuppetFollower.Guard` 处理。因此牵心丝与替身傀儡组合时，状态和真实返回的 `PowerModel.Owner` 都属于最终接收者；力量正负变化使用同一实例路由。
- 原生 Artifact 仍由 `PowerCmd` 消耗并抵挡状态，不绕过其规则。诅咒、模式状态牌等非敌方行动状态不会被全局改写。

`CharmApplyCallsiteScanner` 只扫描游戏程序集和本 Mod，解析实际 IL 中对已闭合 `PowerCmd.Apply<T>` 单体/群体重载的 `call` / `callvirt`，再把操作数替换为同签名 `CharmPowerCmd.Apply<T>`。这让路由在原生堆叠查找之前发生，并保留真实泛型类型、既有 Power 实例和返回实例的 Owner。`Native*Verification` 类型及其嵌套异步状态机排除在扫描之外，验证代码显式调用 wrapper。启动日志会输出补丁命中的调用方法数和扫描耗时。

调用覆盖边界：外部 Mod 程序集不在扫描范围内；开放泛型方法中的 `PowerCmd.Apply<T>` 调用因无法安全确定闭合 `T` 而跳过。本 Mod 的开放泛型入口（例如 `RaceModifier.Power<T>`）应直接调用 `CharmPowerCmd.Apply<T>`。不通过 `PowerCmd`、`AttackCommand`、`MoveState` 或上述 `CreatureCmd.Damage` 重载实现的第三方自定义行动无法由这些入口自动路由。

## 原生验证

验证器为 `NativeCharmVerification.Run(RunState run, Action<bool, string> check)`，应在一个新鲜的原生 `SlimesNormal` 战斗、玩家已进入出牌阶段后调用。该 encounter 固定生成 Twig/Leaf 大型史莱姆各一只和小型史莱姆各一只；测试会核对四只怪物类型，避免随机战斗导致用例漂移。示例入口：

```csharp
await RunManager.Instance.EnterRoomDebug(
    RoomType.Monster,
    model: ModelDb.Encounter<SlimesNormal>().ToMutable());
// 等待玩家进入出牌阶段后：
await NativeCharmVerification.Run(run, check);
```

验证覆盖永久卡收据的重复回放/不同获取、卡牌存档往返、原生意图目标、真实怪物攻击委托、两次原生敌方回合后的到期、已有力量实例的增益叠加、负力量的 Debuff 分类、群体 Weak、Artifact 抵挡、返回 Power 所有者，以及用原生死亡结算清空其他敌人后回落到魅惑来源。测试只使用 `CharmPowerCmd` 显式验证状态路由，避免自身验证 FSM 被生产 IL 扫描器补丁。

该验证源已落盘，但本 Agent 未构建或启动游戏。父级统一构建后还需将它接入原生验收入口并实测；脚本/共享验证入口不属于本文件改动范围。
## 2026-10-22 集成验收

Core授予、永久卡收据与原生展示已接线，卡面/图标随DLL打包。`Verify-Native.ps1 -Suite Charm` 原生通过全部19项检查，覆盖真实怪物动作、两次敌方回合、单体/群体转向、增益已有实例堆叠、负力量、人工制品以及仅剩自身的回退。修复原生Apply在人工制品阻挡后仍返回未安装零量模型的问题，wrapper只返回实际目标Powers中的实例。回退测试伤害从1改为20，避免已有虚弱将1点向下取整为0而不能观测伤害。外部模组开放泛型调用覆盖边界仍见上文；实际图形验收已通过原生手牌完整悬停卡面与敌人状态展示；专项联机尚未完成，正常池未开放。
