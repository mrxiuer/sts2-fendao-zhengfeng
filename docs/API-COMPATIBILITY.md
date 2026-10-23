# 正式分支与测试分支 API 核验

核验日期：2026-10-23。结论：当前 `public` 与 `public-beta` 中本模组实际使用的 API 不兼容。按用户确认的兼容策略，以 `public-beta` 为开发与运行基准，保留清单的 `min_game_version: 0.111.0`；不增加精确版本锁定或最高版本限制。未来版本是否兼容仍需按实际 API 验证。

## 样本来源

从本机合法 Steam 安装直接复制 macOS arm64 的 `sts2.dll`、XML 接口说明、GodotSharp 和 Harmony 程序集。先保存测试版快照，再由用户切到 Default Public Version，保存正式版快照。随后用户切回 public-beta。没有把第三方程序集加入仓库或发布包。

| 项目 | Default Public Version (`public`) | `public-beta` |
| --- | --- | --- |
| Steam build ID | `23811903` | `24724944` |
| macOS depot | `2868842` | `2868842` |
| depot manifest | `8653035385353091849` | `5045320271505434676` |
| sts2.dll SHA-256 | `e7ceb80669bfaf5c8fccabaa126ae2bb283aba514be5b5b55612579cfd285f18` | `9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4` |
| release_info.json | 未保存，不推测版本号 | `v0.111.0`，commit `41cef1ea` |
| 未修改源码分别引用这两组程序集编译 | 失败，18条 API 相关编译错误 | 成功，0警告、0错误 |

本轮工具为官方 .NET SDK 9.0.319，目标框架仍为 net9.0；不改变用户的全局 SDK 配置。完整记录位于本地 `artifacts/verification/0.5.1/`，不进入运行包。正式分支编译只是揭示了已发现的不兼容点，并不代表修复这些点就能完成正式版移植。

## 已确认的不兼容点

| 模组使用的接口 | 正式分支的差异及影响 |
| --- | --- |
| `AbstractModel.AfterBlockBroken(PlayerChoiceContext, Creature, Creature)` | 正式分支为单 `Creature` 参数，遁地破盾逻辑的 override 不匹配。 |
| `AbstractModel.ModifyDamageMultiplicative(..., CardPlay)` | 正式分支不提供模组所需的该签名，伤害规则 override 不匹配。 |
| `AbstractModel.BeforeCombatRewardOffered(RewardsSet, CombatRoom)` | 正式分支没有所需 override，影响战斗奖励接入。 |
| `StartRunLobbyPlayer` / `IStartRunLobbyListener` | 正式分支使用 `LobbyPlayer`，联机监听器类型和方法签名不匹配。 |
| `EventCombatSynchronizer` | 正式分支缺少本模组引用的类型，影响事件战斗恢复。 |
| `CombatId` | 正式分支缺少本模组引用的类型，影响双生史莱姆同步补丁。 |
| `PoisonPower.Trigger` | 正式分支没有该成员，毒伤来源补丁目标不匹配。 |
| `RunManager.WinRun` | 正式分支没有该成员，比赛终局补丁目标不匹配。 |

编译器已证明本模组需要的 API 有实际差异，因此没有继续穷举两个分支的全部 API 或尝试兼容移植。未以二进制哈希不同本身作为不兼容结论。

## 兼容策略

- 保留必要的最低游戏版本，避免把无法运行的正式版标为可用；工坊、清单说明和安装文档明确要求 public-beta。
- 没有增加最高游戏版本、完整版本字符串相等或构建 ID 白名单。`public-beta` 是当前已验证分支，不宣称未来每个测试版都兼容。
- 不修改原版 `PeerVersionInfo` 或原版房间版本校验。所有联机玩家仍须使用相同游戏版本、模组版本及兼容 BaseLib。
- 0.5.1 只新增显示翻译和说明，规则仍是 `1.0.0-design.14`、协议仍是 `wire:4`。玩家界面语言可不同；新局共享状态使用与语言无关的玩家占位名，角色显示名在本机界面解析。

本轮没有完成实际 Steam 多机对局或逐页中英文界面视觉验收；源码编译及规则/翻译检查不替代这些验证。
