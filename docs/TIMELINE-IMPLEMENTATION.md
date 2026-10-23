# 额外时间线实现说明

道具 `extra_timeline` 的每次授予各向永久牌组加入一张基础版 `ExtraTimelineCard`。该卡为2费稀有技能，不可升级；重复授予会按不同收据继续加牌，不会强化已有时间线卡。

打出卡牌后，玩家先选一名拥有可用公开牌组快照的对手，再通过原生 `CardSelectCmd.FromSimpleGrid` 选择牌。候选只从比赛状态 `Racer.MarketCards` 读取；每个条目都是公开档案保存的 `NativeCheckpoint.EncodeCard` 包，解码后保留升级、附魔和二次强化字段。代码不构造随机候选池、不读取在线玩家当前牌组，也不向对手发送任何改牌命令。选中的模板只用于生成本地战斗卡副本，副本 `DeckVersion` 为空，绝不会加入永久牌组。

单体敌方或友方目标由原生 `NTargetManager` 选取。取消玩家、卡牌或目标选择时，不会调用复制卡的 `CardCmd.AutoPlay`；若目标选择已启动，临时战斗副本会从战斗牌堆与状态中移除。无可选目标时沿用原生 AutoPlay 的无目标处理。基础时间线卡本身仍按正常出牌规则支付2能量；复制牌走原生免费 AutoPlay，不支付其能量或星币，X费与特殊资源效果遵循游戏原生 AutoPlay 语义。自动打出的“额外时间线”副本不再触发该效果，避免递归复制。

`TimelineRules.OpponentSnapshots` 只排除本地玩家和没有卡牌包的快照，不检查对手是否在线、已淘汰或已完赛。打开选择器前会复制 reducer 中的公开卡牌列表，因此对手断线后保留的最后一份快照仍可用；在没有已收到快照时则明确跳过，不伪造卡牌。若单张公开包无法解码，会跳过该包并保留其余真实候选。

## 收据接线

收据契约为 `Kind = "timeline_card"`、`Id = 授予事件 ID + ":timeline"`、`Value = 授予事件 ID`。将 `await ApplyTimelineReceipt(ctx, r)` 加到 `RaceModifier.ApplyReceiptCore` 的 switch 分支中，放在父层统一的 `S.MarkReceiptApplied(r.Id)` 和 ack 之前。该方法只处理本地原生卡牌添加和幂等状态，不调用全局 `S.ReceiptApplied`、不标记全局收据，也不发送 ack。卡牌自带保存的 `GrantReceiptId` 与修饰器保存的 `AppliedTimelineReceiptIds` 可在重放时识别已经添加的卡。

## 验证边界

`NativeTimelineVerification.Run(NGame)` 覆盖相同收据重放、不同授予各加一张、二次强化真实卡包复制、成功的原生卡牌选择、取消卡牌选择、取消原生目标选择，以及双方永久牌组和保留公开快照未被修改。对手夹具由真实 `PublicProfiles.Capture` 生成；没有随机卡牌或临时伪造的公开牌。主代理需在 `NativeVerification` 的统一 fixture 分发中接入该验证，并接入上述收据分支。

本实现未修改 `Catalog`、`RaceModifier` 主文件、`ApplyReceipts`、`NativeVerification` 或脚本，因此尚未进入正常效果目录、尚未接入生产收据分发，也没有启动游戏或运行原生验证。卡牌美术留给主代理；本次没有添加占位图。按任务约定，本 Agent 未编译。

## 2026-10-22 集成验收

Core授予与RaceModifier收据接线现已完成，卡面与图标已嵌入DLL。`Verify-Native.ps1 -Suite Timeline` 实际通过10项原生检查：永久授予幂等、不同获取、保留二次强化的真实对手牌包、原生免费副本、取消选牌与选敌，以及双边永久牌组不变。夹具修正了取得淬炼余火后MaxUpgradeLevel已为2却按1筛选的错误。实际原生手牌悬停卡面已通过，仍需真实多人选择/断线恢复，不据此宣称全范围完成。正常候选尚未开放。

额外时间线不进入自身的复制候选，避免出现能选但没有效果的卡。卡面说明改为“选择对手牌组中1张牌，免费打出其副本。不能选择「额外时间线」。”；旧副本的递归保护仍保留作为防御性检查。

文案调整后，真实图形回归再次通过；对手快照确有2张额外时间线时，仅含该牌的牌组不会打开无效选择器，不生成无效副本，也不扣副本资源。测试按真实序列化卡牌类型判断，MarketCard.Id是卡牌实例身份而非模型类型。
