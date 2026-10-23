# 友方阵形实现

FriendlyFormation 在原生 NCombatRoom.PositionPlayersAndPets 排布后统一整理友方节点，并在 AddCreature、节点移除、Creature 死亡和复活后重新计算。布局测量节点当前变换下的 NCreatureVisuals.Bounds、Hitbox、NHealthBar、NPower 及可见状态文本和图标矩形；每个单位按这些实际矩形的包络占位，再适配可见队伍区域。原生血条根节点尺寸为零，血条占位取其可见子控件的矩形包络。

可用宽度取当前 viewport 中线左侧。队伍边距和单位间距沿用原生 NCombatRoom 的 150/70 世界单位，并经当前节点到 viewport 的实际变换换算。空间不足时优先压紧间距并改成多行；布局不会缩小 Giant。验收会检查每个单位的模型边界、命中框、血条和状态栏是否在 viewport 内、越过敌方中线或与其他友方矩形相交。

## Harmony 集成

PuppetPresentation.cs 中 PuppetAddedPositionPatch 和 PuppetLayoutPositionPatch 仍保留原生事件入口，但 PositionFollowers 已改为代理 FriendlyFormation.Reflow，不再把傀儡固定放在 owner 的 +230 位置。傀儡的立绘、状态可见性和其他行为补丁保持独立。

统一入口之后，房间监视器逐帧读取友方的原生 bounds、Hitbox、Giant 默认/当前缩放以及最近一次布局采集到的血条和状态控件尺寸。尺寸签名不包含位置；尺寸稳定 0.15 秒后才触发一次重排，所以重排本身不会形成循环，也不会在每帧扫描子节点。Giant 获得、退场和房间视口尺寸变化都会更新签名。

## 原生验收

在本地原生验证流程中，使用 Necrobinder 角色并进入实际战斗后调用 NativeFormationVerification.Run(run, Check)。

夹具会实际召唤 Osty、替身傀儡，赋予 Giant 与双生史莱姆效果并创建第二个玩家实体；待 Giant 变换和 Osty 原生尺寸动画完成后，它验证四名友方的节点、模型 bounds、命中框、血条和状态栏，再做傀儡死亡及重召后的重排检查。Giant 退场并恢复原生尺寸后，夹具还会验证监视器自动重排且矩形不再相交。角色不是 Necrobinder、已有测试分裂或傀儡、或不在战斗中时会报告前置条件失败；开局已有的原生奥斯提可正常复用。

设定 FDZF_SCREENSHOT_DIR 后，夹具会额外保存 formation-01-giant-necrobinder-four-allies.png 与 formation-02-puppet-resummoned.png。未设置目录时仅运行矩形检查。

2026-10-22：真实图形组合验收通过，四友方共93个可见模型/UI矩形无重叠；傀儡死亡后三单位、重新召唤、巨人化退场自动重排均通过。布局整体下沿固定在可见战斗区域，避免不同行高反复重排导致向上漂移。覆盖脚本 `Capture-Native.ps1 -EffectSuite Formation`；规则模型和图形验收不能替代全部联机/分辨率验证。

同轮补测：重复重排5次漂移0.000；真实新增人工制品后状态栏计入96个可见矩形，重排无重叠。尺寸签名除控件尺寸外，还包括相对角色根节点的局部占位、可见性和原生状态容器子节点数量，新增/移除图标不会被旧缓存漏掉。
