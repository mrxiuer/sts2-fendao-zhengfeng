# 原生奖励发放与恢复快照

原版 `Reward.SelectUnsynchronized` 先调用 `OnSelect`，再执行 `Hook.AfterRewardTaken`，最后才设置 `SuccessfullySelected`。金币、遗物、药水或卡牌可能已写入原生状态，但奖励还未完成；直接采集会留下可重复领取的组合。

`NativeRewardGrant` 标记这段原生执行区间。周期备份和个人检查点在区间内暂缓采集，已完成的领取先进入 `NativeClaimJournal`，再结束标记。献祭和汲取的原生效果应用也由标记保护；解除标记后采集最终检查点。

房主保存分为排空、冻结两个阶段。排空阶段阻止新的奖励发放，但允许已开始的钩子完成其同步命令；完成后再冻结和采集。在线玩家收到本次随机请求号，先排空本机发放，再冻结、发送快照和对应 `recovery_ready`。房主只接受本次确认，不能用普通周期备份的接收时间替代。协议握手现为 `wire:4`（另含原生领取的备份先行约束，见NATIVE-CLAIM-HANDOFF），所有玩家需要相同新构建；磁盘完整归档格式未因此改成通信增量。

首个原生卡牌选择界面和模式奖励选择界面是未改变物品的等待点，可在界面打开时保存。选择结果在冻结期间暂不进入原生发放，解除后继续原任务；已完成领取的claim也在解除后补交原ID，避免正常保存竞争造成按钮任务报错。退出比赛取消尚未恢复的等待。

排空超过5秒仍未结束、或在线玩家未能在本次等待窗口确认时，不覆盖旧归档并解除保存状态。尤其是已经取得部分内容后仍需后续人工选择的原生钩子，不能把未结束Task序列化为完整奖励；该场景仍需进一步覆盖。已断开的玩家继续使用最近安全备份，不要求其完成房间或选下一节点。

## 原生专项

`scripts/Verify-Native.ps1 -Suite RewardRecovery -GrantInProgress` 已通过主机、客机新局及双方全新进程重启：

- 实际打开Boss额外奖励界面，两侧读选项时保存成功，返回后奖励仍未领取。
- 实际打开原生选牌界面，保存并保持冻结；通过真实卡牌输入点击，两侧牌组不变化；解除后各只获得一张。
- 两侧在实际777金币已到账、`SuccessfullySelected` 尚为false的 `AfterRewardTaken` 停点发起保存；周期快照跳过不完整区间，房主排空后归档。
- 归档中钱包、剩余奖励和权威消费记录一致，双方重启后实际继续领取，不重复结算已完成战斗。

最初新增选牌测试消耗了后续MoltenEgg检查使用的普通奖励，导致重启断言缺少未升级攻击牌；夹具现通过原生工厂补生成另一份候选并确认前提，随后完整新局/重启通过，未删去遗物更新断言。

日志：`artifacts/verification/network/{host,client}-RewardRecovery-GrantInProgress[-resume].log`；归档：`artifacts/verification/RewardRecovery-GrantInProgress-suite`。该项已加入All。当前91项核心检查通过。其他事件、已确认命令与未送达原生备份的交接窗口、复杂多次选择和最终All仍需继续验证，不能据此声明全部恢复完成。


## 事件选择执行区间（2026-10-23）

新增NativeEventStep覆盖EventOption.Chosen的完整异步回调。保存与周期备份不采集已经扣血/发金币但页面尚未完成的中间状态。排空沿用原生EventSynchronizer.AwaitPendingOptionTasks边界；已开始的事件可以在排空期完成其嵌套奖励，新选择被阻止，冻结后不再新增资源变更。嵌套奖励领取记录在外层完成后提交，避免内外任务互相等待。

最新四项定向回归均通过新局与双方全部进程重启：EventRecovery -EventInProgress、EventRecovery -EventPage Rest、RewardRecovery -GrantInProgress、RewardRecovery -ConfirmedClaim。事件专项实际停在扣8生命和金币到账后，保存排空期间再发一份嵌套原生奖励；归档中的资源与最终事件页面一致，冻结输入被阻止，重启不重复收入。并非全部多页事件兼容证明。
