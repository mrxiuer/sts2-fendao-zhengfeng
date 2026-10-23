# 0.5.0 本轮交付核对

2026-10-23。本记录对应用户本轮效果、展示、性能和好友测试发布要求。用户最新要求尽快上线、不再执行不必要测试；因此未重跑完整All，也未把逐个原版/第三方事件穷举作为发布前提。原始专项日志保存在artifacts/verification，历史失败记录仍保留。

| 要求 | 实现及现有证据 |
| --- | --- |
| 新增15项效果、重复强化与候选渠道 | Catalog.cs/Catalog.Advanced.cs共65项；91项核心检查含正常offer/choose、禁用、上限和重复权重。15项明细见NEXT-EFFECTS，规则与配置已正式接入。 |
| 随机球、穿透、传导、二次升级、350金币补尾巴、飞行、暴击、负面翻倍 | AdvancedEffects及对应原生补丁；mechanics.log有52项PASS，覆盖真实球槽、原生伤害/格挡、升级禁令、真实尾巴复活及目标锁定；经济专项另覆盖收益和层数。 |
| 傀儡、遁地、巨人化、时间线、两回合魅惑、双生、奖励刷新 | 七项各自原生专项与NewEffectsNetwork新局/双方重启已通过；不是仅规则模拟。puppet_body/puppet_life/burrow/giant/timeline/charm/twin_slime/reroll均有对应完成标记。 |
| 史莱姆原版形象、镜像、奥斯提/傀儡/巨人站位 | TwinSlimeRuntime复用原版形象；formation.log验证4友方节点/96可见矩形无重叠；formation与双生专项已通过。 |
| 精简提示、卡面、状态、图标和独立配置 | 真实Single验证65图标、原生模式图标、遗物附近展示、BaseLib同级入口、模式/调试/效果开关；原生卡面与耐久、状态展示见各图形专项。 |
| 再来一次：卡牌下方刷新、金币不刷新 | NativeCardRewardRefresh与RewardRerollEffect；reroll.log验证原生按钮及取消不返还次数；多人刷新专项验证药水/卡牌/遗物/模式候选。 |
| 逆境生财800上限、强化+400、每类诅咒1/3/5；商店不净化 | Catalog/Engine与91项核心检查中对应边界；README及EFFECTS已同步，旧净化命令被拒绝。 |
| 房主保存退出、战斗从头、原生保存时机参考 | Recovery/RewardRecovery及四人专项、营地/事件/R键专项；本次最新Recovery新局与双方全部重启通过。发奖/事件中保存及确认即断线专项分别通过。范围见RECOVERY。 |
| 后期多人领奖性能 | 四进程真实点击及原始JSON：金币客机P95 98.9341ms；卡牌/遗物/献祭/Boss选择96.6881/145.9609/149.9350/162.5201ms；刷新≤196.2640ms。曾有选牌272.1062ms波动，保留记录，不宣称已定位全部波动原因。 |
| 本轮上线必要检查 | 最新Single、Network、Recovery新局/全进程重启通过。构建0警告/错误，91核心检查。未把以前的All写成本轮完整All。 |
| 安装文件 | ZIP运行内容只有FendaoZhengfeng.dll/JSON；与构建、本机部署及工坊目录DLL一致，SHA256 F79FA2218AECEB142EBEC3DA8B2C104B7C986EAEAA3122A3387F4E8CA9392C64。 |
| Steam仅好友发布 | 官方上传器成功上传3825045315；登录Steam原生查询Result=OK、FriendsOnly、13534156字节、BaseLib依赖3737335127。身份存入packaging/workshop-item-id.txt，后续更新复用。 |

用户随后授权同步GitHub，源码、README与0.5.0-preview发行包一并更新。未穷尽的多页事件中间状态、复杂连续选牌、第三方组合与Steam不同电脑完整竞赛仍列为好友测试限制，不伪称已经实测。没有继续添加无尽模式或新增需求。

订阅：https://steamcommunity.com/sharedfiles/filedetails/?id=3825045315
