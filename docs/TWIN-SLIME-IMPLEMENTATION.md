# 双生黏液实现说明

道具 ID 为 `twin_slime`。首次符合条件的致命伤害会优先触发分裂；每个身体的最大生命为分裂前原始最大生命的 50%，每层重复取得提高 5 个百分点，最高 75%。半点远离零取整，最低 1 点。两个身体以各自最大生命满血开始。重复取得只调整本场分裂比例，不改写角色的永久最大生命。

## 原生实体与外观

分裂会创建第二个 `Creature(Player, hp, hp)`，两个身体都保持原生 `IsPlayer` 身份、玩家阵营、独立生命、格挡、能力、`NCreature` 节点、命中区域和血条。随后通过 `CreatureCmd.Add` 走常规战斗添加流程。游戏没有公开 API 可把第二个 Player-backed `Creature` 附加到已开始的 `CombatState`，因此只在这一处反射调用私有 `CombatState.AttachCreature(Creature)`，再交给公开命令完成常规插入；引擎签名不匹配时会记录错误，不伪造目标实体。

未分裂时复用游戏内绿色中型史莱姆 `LeafSlimeM` 的原生视觉资源；分裂后两个身体各复用绿色小型史莱姆 `LeafSlimeS`。每阶段用对应模型的原生 Spine animator，不将同一份大史莱姆资源缩放成两个小史莱姆。两个身体显示为“**双生黏液 1**”和“**双生黏液 2**”，并由原生节点继续处理待机、受击、死亡动画及点击命中区域。玩家史莱姆只翻转 `NCreatureVisuals.Body` 的 X 轴；`NCreature` 根节点、血条和命中框保持原向。史莱姆视觉在进入树前补齐唯一 `%FormVfx` 容器，让原生 `NCreatureVisuals._Ready` 能绑定 `FormVfxHolder`，死亡动画可按原生流程清理形态特效。

视觉切换仍通过 `NCombatRoom.RemoveCreatureNode` / `AddCreature` 重建原生节点。释放旧节点前，运行时将其子树中的 `NHealthBar` 实例放入 `ConditionalWeakTable` 标记，随后隐藏并禁用旧节点，再按原生流程移除、添加并 `QueueFree`。`NHealthBar.SetHpBarContainerSizeWithOffsets` 用 `CallDeferred` 排入的回调会进入私有 `SetHpBarContainerSizeWithOffsetsImmediately`；Harmony 前置仅对已标记的旧血条跳过该调用，避免回调在控件释放后读取 `HpBarContainer`。活动血条仍执行原方法，弱键表也不会永久保留旧节点。原生 `RemoveCreatureNode` 只将节点移出活动列表并追踪待移除节点，不负责释放节点。

原生 `CombatState.PlayerCreatures` 会列出两个真实实体，因此敌人的单体选敌、随机目标和范围攻击继续走游戏自己的候选和 `AttackContext`。两个实体共用一个 `Player`、牌组、遗物及战斗资源；身体生命、格挡与能力仍按各自 `Creature` 保存。为避免共用玩家钩子因双身体被调用两次，玩家列表和共享 Hook listener 按 `Player` / 模型引用去重；分别复制到两个身体上的能力仍独立生效。

## 卡牌与费用

实现使用原生 `Hook.ModifyCardPlayCount` 和 `CardModel.OnPlayWrapper` 的重复播放循环。已有播放次数 `n` 变成 `2n`，每个 `CardPlay.PlayIndex` 交替解析到 1 号和 2 号身体。每次播放都有原生 `CardPlay` 和 `AttackContext`，卡牌攻击者、自身效果、自动目标和伤害等在对应身体上下文中执行；目标由打牌时的原生选择确定，重复播放复用该目标。卡牌只支付一次原生费用，只走一次外层牌堆移动或消耗流程；两次结算各自执行牌面效果。能量、星星和卡牌特殊资源仍由原生播放命令管理。

`Player.Creature` 的切换限制在单次卡牌播放、死亡前后处理或强制死亡的异步作用域内，并在 `finally` 中恢复，不替换目标选择及其它玩家上下文。原生 `BeforeDeath` / `AfterDeath` 在目标身体作用域中执行。战斗结束时会合并两份当前生命和最大生命改变量、恢复角色原始最大生命基数并收回第二实体；战斗重置也会在清理战斗状态前收回第二实体。若一个身体最终死亡，运行时会结束尚存身体，再只调用一次原生 `CombatManager.HandlePlayerDeath`；若原生复活钩子先救回受击身体，则保留另一身体。直接处决 `Kill` 不属于“承受致命伤害”，仍使用原生语义。

## 致命伤害与复活顺序

首次致命伤害只在真实分裂可执行时跳过 Ghost 预留：目标必须是当前未分裂玩家身体、位于本场战斗、存活、伤害达到当前生命，且持有有效层数、战斗未结束。父代理应在 `RaceModifier.ModifyHpLostAfterOstyLate` 调用 Ghost 预留前执行 `TwinSlimeRuntime.ShouldPrioritizeFirstSplit(target, amount, cardSource)`；返回 `true` 时原样返回伤害，不消耗 Ghost。其它伤害不绕过 Ghost。分裂后可通过 `TwinSlimeRuntime.Find(target)` 识别任一身体，再按常规复活路径处理。

傀儡的受击重定向发生在生命损失前。傀儡先承受攻击；若致死伤害溢出，溢出会作为原玩家的真实生命损失继续处理，首次满足上述条件时才分裂。源码路径由 `CreatureCmd.Damage` 的 `ModifyUnblockedDamageTarget` 重定向和傀儡死亡后的 `LoseHpInternal` 溢出调用组成。

之后任一身体受到致命伤害时，顺序为 Ghost → 原生 Fairy in a Bottle → 原生 Lizard Tail：Ghost 由生命损失钩子先行预留；原生 `Hook.ShouldDie` 阶段运行 Fairy；只有此前无人阻止死亡才进入 `Hook.ShouldDieLate`，在那里运行 Tail。这个次序来自游戏的两阶段 `ShouldDie` / `ShouldDieLate` 调用，以及 Fairy 和 Tail 各自声明的钩子阶段。两份复活资源耗尽后，任一身体死亡都会结束另一身体并淘汰玩家。

## 战斗存档与读档

原生 `Player.ToSerializable` 直接从 `Creature.CurrentHp` / `Creature.MaxHp` 写入玩家快照；战斗房间快照不保存当前回合中两个身体的完整实时状态，普通继续会重新打开该房间。因此保存时需要两层互补数据：

- `Player.ToSerializable` 的 Postfix 只改写返回的 `SerializablePlayer.CurrentHp` 与 `MaxHp`，填入两份身体合并后的生命值。它不修改仍在战斗中的实体，也不会把单个半最大生命身体留在角色永久资料里。
- `RaceModifier.TwinSlimeStateJson` 是 `SavedProperty`，保存当前房间键、分裂标记、层数、分裂前最大生命基数、分裂身体最大生命，以及两个身体各自的当前与最大生命。没有真实已分裂 Pair 时不写入分裂状态；较低的玩家生命不会被误判为已分裂。

战斗开始原生流程完成后，恢复钩子只接受版本有效、房间键相同且仍有对应效果层数的分裂记录，再以记录中的两份精确生命值和最大生命重建第二实体。它不会从合并后的玩家最大生命再次对半计算，也不会免费恢复生命。记录恢复后立即清空；后续保存再从活体 Pair 捕获最新状态。无效记录、错房间记录或没有活体 Pair 时均丢弃，不重建身体。

战斗结束时仍按常规合并收尾；战斗中保存后重新打开同一房间时，玩家快照负责提供合并基数，`SavedProperty` 负责恢复两个身体。开始分裂前即使生命很低也不会生成 Pair，因为恢复必须依赖显式的有效分裂记录。

## 接线与验收边界

`TwinSlimeRuntime.cs` 中的 Harmony patch 由常规 `PatchAll` 自动发现。父代理负责注册目录与持有道具 `twin_slime`、接入上述 `ModifyHpLostAfterOstyLate` 条件，并在统一原生验证分支调用 `NativeTwinSlimeVerification.Run(run, Check)`。`NativeTwinSlimeVerification` 已加入低生命未分裂存档、傀儡致死溢出、双真实身体、原生节点与血条和 FormVfxHolder、能力复制、独立受伤、敌人范围/单体/随机目标、卡牌双结算、合并快照、房间键和精确身体状态恢复，以及 Ghost → Fairy → Tail → 最终淘汰顺序的原生 fixture。

2026-10-22：`Verify-Native.ps1 -Suite TwinSlime` 已通过，包括 FormVfxHolder、旧血条退役回调、真实双体选敌/受击/出牌、恢复和最终淘汰。实际图形的 Formation 组合也已通过巨人化双体、奥斯提和傀儡站位验收；巨人化按 Player 归属附着两具身体。持久化 fixture 会生成原生 `RunManager.ToSave` 快照并检查 `RunState.FromSerializable` 的序列化结果，然后直接执行同一房间的恢复入口；它不代替真正退出并重新加载游戏的验收。本能力不增加角色永久最大生命。