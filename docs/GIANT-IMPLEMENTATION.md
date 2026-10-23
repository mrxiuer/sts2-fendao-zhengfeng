# 巨人化实现说明

效果 ID 为 giant_form。新代码可由项目默认源码通配规则编译；正式开放前仍需由主代理接入 Catalog、展示文本、效果图标和原生验证入口。

## 规则

- 首次取得时 Holding.Rank 为 0：角色原生战斗立绘缩放为原来的 2 倍；玩家攻击可斩杀最大生命严格小于玩家当前生命的敌人。
- 每次重复使斩杀阈值增加玩家当前生命的 10%。判断保持严格小于：Rank 1 时，当前生命 100 的玩家可斩杀最大生命 109 的敌人，最大生命恰为 110 的敌人仍不符合。
- 每次重复也使体型增加原始尺寸的 0.1 倍；上限 3 倍。该上限避免重复堆叠让战斗站位和点击区域无限放大。
- 斩杀仅响应玩家自身带有 ValueProp.Move 的攻击，要求敌人与玩家处于同一场战斗。当前生命与敌人最大生命在最终 HP 损失结算时读取。
- 不加金币、奖励或其他经济收益。

## 原生结算

GiantExecutionPatch 在原生 Hook.ModifyHpLost 的 AfterOsty 阶段调整最终未格挡生命损失，让 CreatureCmd.Damage 继续生成正常 DamageResult。随后游戏原代码继续触发伤害钩子、攻击记录、连击处理，并将真正死亡的目标交给原生 CreatureCmd.Kill。因此不直接写 HP，不手工调用强制击杀，也不绕过死亡预防、Boss 阶段与战斗胜利判定。

多目标伤害在每个目标的伤害结算中分别判断；多段攻击在首个目标死亡后仍由原生 AttackCommand 选取后续目标。NativeGiantVerification 覆盖严格边界、挡格下斩杀、重复阈值、群体攻击目标独立性、多段重选目标及体型缩放。

## 接线

1. 在 RaceConfig.Effects.cs / Catalog 中登记 giant_form、简短中文名称与规则文案；不要把首层 Rank=0 当作没有持有。
2. 为 giant_form 提供正式效果图标并加入图标资源打包规则。
3. 从现有原生验证入口调用 NativeGiantVerification.Run(run, check)。本验证使用 MockTesting.GrantEffect("giant_form")，需先使待测 ID 可由测试 Catalog 查找。

