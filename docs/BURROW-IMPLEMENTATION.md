# 地底庇护实现说明

地底庇护的确定性状态规则位于 Core/BurrowRules.cs，原生战斗执行位于 Mod/BurrowEffect.cs，角色动作位于 Mod/BurrowVisuals.cs。成品图标来自 Assets/icons/burrow_sanctuary.png，由现有效果图标资源管线打包和显示。

## 充能与战斗状态

每三场不同战斗胜利储存一次使用次数，上限两次。每层强化将充能所需胜场减一，最低为一场；达到储存上限时不保留额外进度。充能、进度、当前战斗阶段、遁地回合和破盾标记保存在 Racer.History 与 Racer.UsedServices，随比赛存档恢复。

Engine 在新战斗开始时清理旧阶段，在首次结算胜利时登记充能，在战斗结束路径清除临时状态。命令 reducer 负责 burrow_use、burrow_block_broken、burrow_emerge、burrow_ready、burrow_replay 和 burrow_cleanup，所有状态变化都经过 RaceSession.Submit。

使用操作会先提交扣除次数和遁地状态，确认状态生效后再给角色等于最大生命值的格挡并结束回合。下一个玩家回合开始时，未破盾则直接出土；破盾则出土并通过原生 PlayerCmd.EndTurn 跳过该回合，随后恢复操作。回合钩子在首回合早退前检查此状态。native 战斗重建会返还尚未结算的那次使用；普通战斗结束只清除临时状态，不返还已使用次数。

## 原生视觉与接线

BurrowVisuals 为现有 NCreature 添加短暂的视觉控制节点，平滑下沉和淡出原生身体，在入土、破盾和出土时绘制尘土；破盾出土后会在眩晕期间显示环绕星晕。它保留角色现有模型与战斗节点，不覆盖透明图标或添加文字 HUD。BurrowStatusHud.Install 和 Refresh 留作兼容空入口。

遗物点击由主接线调用 BurrowEffect.Click()；角色状态栏使用成品图标并读取 BurrowRules.Read 展示次数、进度和战斗阶段。RaceModifier.AfterBlockBroken 负责记录玩家自身格挡破坏，AfterPlayerTurnStartLate 负责出土、眩晕和恢复，战斗结束钩子调用 BurrowEffect.AfterCombatEnd()。

## Native 验证夹具

NativeBurrowVerification.Run(run, check) 覆盖三胜充能、重复胜利去重、强化间隔与最低间隔、储存上限、JSON 恢复、重复命令、破盾/未破盾状态、重开补偿、跨战清理，以及真实战斗中的最大生命格挡、原生破盾回调、下沉/出土表现与跳过回合。主验证流程可调用：

    await NativeBurrowVerification.Run(run, Check);
## 2026-10-22 集成验收

Engine命令、胜利充能、回合/战后清理、遗物点击和原生状态栏已接线。`Verify-Native.ps1 -Suite Burrow` 原生通过全部检查，涵盖储能、状态序列化、充能回放、实际下沉淡出、真实破盾事件、出土跳过一回合、模型复原和未破盾正常出土。尘土/眩晕节点使用SceneTree.ProcessFrame订阅驱动，退出取消订阅，不依赖此项目未生成的Godot override桥接。随后增加傀儡代盾破盾的组合接线，组合本身仍需测试；正常候选尚未开放。

## 双生与傀儡组合

双生后的两个身体同步遁地、出土、眩晕和恢复。一次使用只扣1次储能，每个存活身体获得等同自身最大生命的格挡；傀儡存活时，两份格挡按正常转移规则累加到同一个傀儡。无傀儡时两份格挡独立，任一身体破盾都会标记这次遁地的共享眩晕状态，另一身体剩余格挡不被消耗。

`Verify-Native.ps1 -Suite Combinations` 与 `Capture-Native.ps1 -EffectSuite Combinations` 已实际通过：真实取得傀儡/双生/巨人化/遁地、致命非攻击分裂、双体动作和状态栏、瞄准第二身体击破傀儡盾、恰好跳过一次共同回合、完整盾正常出土、傀儡死亡后的独立双盾、第二身体破盾、地下胜利清理和下一战复原。三个图形证据保存在 `artifacts/visual/acceptance-combinations`。

同时修复了双生战后清理补丁的参数绑定：`Hook.AfterCombatEnd(IRunState,ICombatState,CombatRoom)` 的战斗状态是第二个参数，应绑定`__1`。此前误绑定`__0`使战后收拢只在随后重置时才执行。
