# 再来一次：实现与接线契约

## 规则与当前状态

`encore_rewards` 的基础层级让每份未领取的非金币奖励刷新 1 次；每次强化增加 1 次，单份奖励上限为 3 次。计数按奖励身份单独保存。Core 先在宿主排序的命令中记账，之后原生适配器才修改现有奖励对象；刷新不会更换奖励对象或另造 `RewardKey`。已领取、已跳过/解决或没有剩余次数的条目必须拒绝刷新。

当前实现覆盖原生药水、卡牌、随机及预设遗物、特定卡牌奖励，以及通过原生 `RewardsSet.Offer` / `NRewardsScreen.ShowScreen` 展示的战斗、宝箱、事件和自定义赠礼；商店赠奖也只有在走这条原生奖励集合入口时才纳入。反编译确认 `NLinkedRewardSet.Create` 会为子奖励创建普通 `NRewardButton`，因此链接奖励子项也走同一按钮补丁并使用独立键。卡牌移除、取回被偷卡牌等服务操作没有随机奖励内容，不显示误导性的刷新按钮。模式 N 选 1 候选也有独立刷新规则。原生生成器和事务恢复测试已通过；事件、商店赠礼及链接奖励的完整交互仍待逐项验收。

## 原生奖励刷新语义

| 原生奖励 | 刷新方式与恢复数据 |
| --- | --- |
| 金币 | 不提供刷新按钮，拒绝刷新请求，金额及刷新次数保持不变。 |
| 药水 | 以同一玩家的新 `PotionReward` 走原生 `Populate()`，再把生成结果写回原奖励并清除缓存图标。 |
| 随机卡牌 | 调用原生 `CardReward.Reroll()`，保留原卡池、来源、稀有度选项和原生生成流程。若有自定义 `CardPoolFilter`，首次展示时冻结 `GetPossibleCards` 得到的允许卡牌 ID；把 ID 集合写进奖励恢复数据，读档时还原为筛选器，以便加载后继续刷新且不扩大候选范围。 |
| 随机遗物 | 保留原稀有度抽取语义，并从玩家运行中的原生遗物袋/共享袋抽取，优先排除当前遗物。 |
| 预设遗物 | 替换为玩家原生遗物袋中同稀有度的遗物，并同步更新 `_relic` 与 `_predeterminedRelic`，保证新的具体结果仍可序列化。原生 `RelicReward` 只保存预设模型及稀有度，不保存事件或商店的原始候选池；因此目前能保留稀有度和原生运行遗物池，不能保证重抽结果来自 FakeMerchant 等来源的专属货架池。 |
| 特定卡牌（`SpecialCardReward`） | 在包含原卡模型的卡池中按原稀有度走原生非战斗卡牌生成，并把旧卡记入卡牌选择历史为未选。该类型原生用于任务卡、事件指定卡和返还被偷卡牌；其保存格式会保留卡牌的升级/附魔等修改。刷新会生成一张新的同稀有度卡，因此不会保留旧卡的专属修改或指定身份，这是此类奖励刷新所定义的替换语义。 |
| 服务操作（例如卡牌移除或取回技能） | 不显示刷新按钮，也不扣次数。它们没有可重新生成的原生奖励内容；将服务操作当作奖励重掷会重复同一服务而不会改变奖励。 |

上表对应的原生依据可在 `research/decompiled/RewardsSet.cs`、`Reward.cs`、`SerializableReward.cs`、`RelicReward.cs`、`SpecialCardReward.cs` 和 `RewardsCmd.cs` 核对。`RewardsSet` 明确同时承载战斗/宝箱与事件、遗物提供的自定义奖励；`RewardsCmd.OfferCustom` 也通过同一 `RewardsSet.Offer()` 展示自定义赠礼。捕获/恢复补丁把当前集合附加到现有 `RecoverySnapshot`；非战斗集合按房间内序号区分，恢复时从保存的 `StorageKey` 采用原序号并推进后续计数，避免重开进程后错配集合。读档恢复待展示的自定义集合时保留跳过规则。覆盖范围以这些原生展示入口为准。

卡牌使用原生选牌界面下方的刷新按钮；漂流木的原生次数优先消耗，随后消耗模式次数。其他支持的奖励在对应行显示刷新图标及 `剩余/上限`；金币不显示刷新入口。刷新命令提交后会留下 `reroll_native` 收据；适配器同步改写原生奖励并立刻记录该收据的 `AppliedReceipts` 标记，中间不留异步间隔，然后捕获新奖励内容并确认收据。恢复时在奖励集合填充完成、行按钮创建之前，按稳定奖励键重放未标记收据；已经标记的收据不再重掷。匹配当前集合的已应用收据可以确认；即使领取后的行已从集合移除，只要 `ConsumedRewards` 与已应用标记都吻合，也会确认该收据。未标记但已领取的收据会保留、记录异常且不会重掷；不匹配当前集合的未应用收据保留到对应集合恢复。奖励身份沿用稳定房间键与集合内序号/链接路径；领取状态复用 `Match.ConsumedRewards`，计数放在 `Racer.History` 的哈希键中，不靠 UI 本地变量保存。

## Core 与 Engine 接口

新增 `Core/RewardRerollRules.cs`：

- `Limit(Racer)`、`NativeUsage(Racer, rewardKey)`、`OfferUsage(Racer, offerId)` 提供次数状态。
- `reward_reroll` reducer 必须在一次宿主排序的 Engine 命令中调用 `CommitNative(match, player, key)`。只有命令被接受后，Mod 层才调用 `ApplyNativeReroll`。命令 ID 由 `RaceSession.New` 创建，重复投递必须由现有 `Engine.ProcessedCommands` 去重；不要在 UI 本地先扣次数。
- `offer_reroll` reducer 调用 `RefreshOffer(match, player, rewardKey, route)`。该方法检查 Offer 未解决且未领取，在 Core 状态中同时更改候选代次和记账。
- `OfferId(playerId, rewardKey, route)` 必须与原 `offer` reducer 的键完全一致。候选用奖励键、路线和递增代次作稳定生成盐；`Match.Offers` 保存当前候选列表，关闭、取消或重开选择器只能复用它，不能重新生成。

模式 UI 通过 `RewardRerollOfferUi.CanRefresh`、`RefreshLabel` 和 `RefreshAsync` 接入 `RaceUi.ChooseEffect`。选择刷新行后提交 `offer_reroll`，等待 Core 状态更新，再用同一个 Offer 重绘；普通候选仍走原 `choose` 命令，返回仍执行原取消行为。不要在每次打开候选选择器时直接调用 `Candidates.Generate`。

## 接线与开放状态

Engine、模式候选刷新、原生奖励显示与恢复补丁已接线。效果仍留在待验收目录，没有进入正式候选池；完成整批验收后再开放。

## 验证

`NativeRerollVerification.VerifyRules()` 覆盖层级上限、独立计数、已领取拒绝、Offer 代次和序列化后候选/计数不回退。`VerifyNativeGenerators(player)` 检查原生生成器、金币刷新拒绝及金额不变、自定义卡池筛选、预设遗物和特定卡牌语义；并通过真实 `reward_reroll` 命令和 `NativeCheckpoint.Bundle` 验证命令提交后旧内容恢复时重放一次、带 `AppliedReceipts` 的提交后快照在确认前恢复时不再重掷，以及重复重放保持内容不变。另覆盖奖励已领取后已应用收据无可见奖励行仍可排队确认，以及已领取但没有已应用标记时保留收据且不重掷/不确认。2026-10-22：构建无警告/错误，81项核心检查通过；`Verify-Native.ps1 -Suite Reroll` 与实际图形界面 `Capture-Native.ps1 -EffectSuite Reroll` 均通过。实际金币行没有刷新按钮，直接调用刷新也不改变金额或次数；卡牌下方的原生按钮已验证漂流木与模式次数独立、刷新后继续选牌、耗尽后隐藏及取消不返还次数。恢复事务使用实际药水奖励验证。



2026-10-22完整流程发现并修复：BindRewardKeys曾用屏幕索引覆盖远征悬赏遗物已绑定的ticket。现在生成屏幕身份只对尚未绑定身份的奖励生效，已有ticket、恢复身份或已用于收据的身份保持不变。Campaign实际第五幕远征遗物的身份、精英混合献祭渠道和完整五幕流程通过，Reroll专项回归通过。
