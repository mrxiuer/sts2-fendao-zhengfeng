# 杀戮尖塔2：分道争锋

《杀戮尖塔2》的独立推进竞速联机 MOD 设计项目。玩家独立选择路线、独立完成战斗，通过献祭遗物取得干扰、契约或增益，以率先完成全部幕为胜利目标。

当前阶段：完整玩法设计与技术研究，尚未实现 MOD 代码。规则已获确认，数值属于待验证的平衡基线，不代表已通过游戏测试。

## 项目文档

- [玩法规则](docs/RULES.md)：地图、献祭、胜负、随机候选、补偿与净化。
- [效果目录](docs/EFFECTS.md)：12 项诅咒、7 项双刃契约、10 项增益。
- [技术架构](docs/ARCHITECTURE.md)：独立推进、同步、恢复及研究边界。
- [加载与发布规范](docs/DISTRIBUTION.md)：本地加载、工坊内容与发布验证。
- [待决事项](docs/OPEN_QUESTIONS.md)：尚未确认或尚未验证的行为。

## 目录与交付边界

源码和设计仓库：`D:\Projects\sts2-fendao-zhengfeng`。

游戏安装根目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2`。

最终本地产物目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\FendaoZhengfeng`。该子目录为计划部署位置，目前不创建或部署任何 MOD 产物。

游戏目录只用于只读研究与未来最终产物部署，不作为源码、文档或 Git 仓库目录。不得替换游戏 EXE、原版程序集、PCK 或其他原版文件。未来以游戏 MOD 加载器加载独立包，功能修改限于加载期间的运行时扩展。

计划技术标识：`FendaoZhengfeng`。中文显示名：`杀戮尖塔2：分道争锋`。技术标识尚未注册，正式发布前检查唯一性。

## 本机核验记录

2026-10-18 只读核验：安装目录存在；`release_info.json` 声明 `v0.111.0`，commit `41cef1ea`，branch `v0.111.0`。这仅是本地安装信息，不代表最新发行版本，也不证明 MOD 已兼容。

当前没有可加载构建、工坊条目、远程 Git 仓库或发布版本。不生成伪造的加载清单、已发布标识或兼容性承诺。
