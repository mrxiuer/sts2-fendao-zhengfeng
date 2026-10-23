# 奖励点击性能调查

## 结论

领奖点击的同步路径包含状态克隆、发布序列化、恢复快照和本地检查点写入。当前检查点在刚发布相同比赛状态后再次序列化完整 Match；单人模式还会把状态压缩后交给一个空实现的发送服务。性能补丁通过复用相同 session、Match 引用和 Revision 的已发布 JSON，并跳过单人模式的空广播，减少这两处重复工作。没有修改共享 Engine、RaceSession、RaceRecovery、RaceRuntime 或 RewardRecovery 源文件。

## 点击链和同步成本

完整链路由原生奖励按钮触发：NRewardButton.OnRelease / GetReward → RewardsSetSynchronizer.SelectLocalReward → 奖励效果调用 RaceSession.Submit → Engine.Execute → RaceSession.PublishState → RaceRuntime.Checkpoint。Engine.Execute 会将 Match 序列化再反序列化，以隔离命令执行状态。PublishState 会序列化新状态并排队保存；Broadcast 会压缩状态并调用传输层。奖励发放后，RaceRuntime.Checkpoint 同步调用 RaceRecovery.SendSnapshot / Capture，RunManager.ToSave 和 SerializableRun.Serialize，并由 RewardRecovery.Capture 编码尚未领取的奖励和卡牌选项；随后检查点再序列化 Match 与 NativeCheckpoint.Bundle。

源码定位：src/FendaoZhengfeng.Mod/RaceSession.cs 的 Submit、OnMessage、PublishState、Broadcast、QueueSave；src/FendaoZhengfeng.Mod/RaceRuntime.cs 的 Checkpoint；src/FendaoZhengfeng.Mod/RaceRecovery.cs 的 SendSnapshot、Capture；src/FendaoZhengfeng.Mod/RewardRecovery.cs 的 Capture；src/FendaoZhengfeng.Core/Engine.cs 的 Execute。Game 的 NetSingleplayerGameService.SendMessage 是空实现，因此本地模式广播仍做压缩但不会发出消息。文件系统写入通过 LatestFileWriter 异步执行；这不代表点击完全不受持久化影响，因为 JSON 生成和排队仍在点击链上，所以 profiler 将同步入队和后台写盘分别报告。

## 补丁行为

RewardPerformance.cs 在默认配置下关闭耗时采样；只有进程启动前设置 FDZF_PROFILE_REWARDS=1 才启用。它记录从原生按钮到 UI 更新的调用链，并分别统计 Engine.Match 克隆 JSON、发布 JSON、网络展开、压缩、快照发送、恢复捕获、RunManager.ToSave、奖励快照、检查点 JSON、保存入队和后台文件写入。阶段耗时彼此嵌套，不应直接求和；后台写盘持续时间也不是用户点击阻塞时间。

发布状态生成的 JSON 会缓存 session、Match 对象引用和 Revision。RaceRuntime.Checkpoint 仅在三者匹配时复用它；否则回退到正常序列化。session 释放时清缓存。Cache 命中不跳过 NativeCheckpoint.Bundle、原生存档或 RewardRecovery 捕获，保留奖励恢复所需数据和写入顺序。

单人模式的 RaceSession.Broadcast 会调用 NetSingleplayerGameService 空发送实现，因此补丁在压缩之前跳过它。多人传输仍走原路径。

## 原生验证

待补丁源文件稳定并编译后，在启动游戏前设置 FDZF_PROFILE_REWARDS=1；集成专用 suite 后运行 scripts/Verify-Native.ps1 -Suite RewardPerformance -GameRoot <游戏目录>。原生 fixture 由统一原生验证入口在 FDZF_NATIVE_VERIFY=1、FDZF_PROFILE_REWARDS=1 且角色为 reward_performance 时直接调用 Run(NGame game)；它会进入实际 Ironclad 战斗、完成战斗、找到屏幕上的 GoldReward 按钮并调用真实 OnRelease，成功时输出自有 marker [FDZF REWARD PERF COMPLETE]。fixture 检查奖励只发放一次、收据已提交、点击链阶段可见、检查点命中 JSON 缓存、单人空广播在压缩前跳过，以及保存入队和后台写盘分别记录。

当前 fixture 已准备好，但此处没有构建或运行游戏。主代理需在原生验收中核对启动、marker、日志数值和游戏行为。实际效果尚未完成验收，不应据此宣称点击延迟已得到实机确认。

## 对共享核心的后续建议

若原生报告表明 Engine.Execute 的 Match 克隆仍是主要同步热点，先用 CloneSerialize / CloneDeserialize 的实际点击时长决定是否值得调整共享 Engine；目前按约束没有更改该文件。不要移除克隆语义。可另行评估保持相同对象隔离和异常行为的优化实现，并用原生 fixture 比较领取前后的点击链和完整奖励恢复。

## 2026-10-22 原生实际采样

`Verify-Native.ps1 -Suite RewardPerformance` 已实际通过9项断言：真实金币奖励按钮、Core领取确认、点击到UI、双向JSON深拷贝、发布/检查点、原生与剩余奖励快照、缓存命中、单机空广播及后台磁盘写入关联。一次起始战斗后的样本为 click_to_ui 11.264ms（release_to_ui 11.505ms），其中 checkpoint_total 3.317ms，recovery_capture 2.508ms；保存入队0.353ms/3次、后台写入3.046ms/3次，后台耗时不可再加到点击耗时上。日志：artifacts/verification/network/reward_performance.log。

这不是多人后期延迟的验收结论，也没有同场景优化前基线；需要继续对网络实际领奖和较大状态采样。最初队列instrumentation挂在可内联的Session.QueueSave，导致计数缺失；现改为真实LatestFileWriter.Enqueue并按path和contents引用关联AtomicWrite，不以删断言掩盖缺失。


## 后续四人后期采样

2026-10-22完成同一构建的四进程A/B：每人160张牌、12效果与较长历史，各12次真实金币按钮点击。客户端中位数265.706→65.340ms，P95 531.885→68.928ms；网络Submit由每次2次变为1次。基线和优化路径均完整通过，比较门槛通过。详见[后期金币奖励验收](LATE-REWARD-PERFORMANCE.md)。这是金币按钮的一阶段，不替代其他奖励类型、网络/存档回归或最终全量。
