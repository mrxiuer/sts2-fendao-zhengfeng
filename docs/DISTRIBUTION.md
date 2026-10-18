# 原生加载与 Steam 创意工坊发布规范

状态：发布设计与资料核验，不是已生成的 MOD 包或已完成的发布流程。

## 1. 本地目录

- 开发仓库：`D:\Projects\sts2-fendao-zhengfeng`。
- 游戏目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2`，只读研究。
- 最终产物计划部署：`D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\FendaoZhengfeng`。
- 中间编译、缓存、解包及反编译产物留在仓库忽略目录，不进入游戏目录和发布包。

不替换游戏可执行文件、原版 DLL、原版 PCK 或现有 MOD 文件。未来仅部署本 MOD 专用子目录的完整产物，不清空整个 mods 目录。当前未部署任何内容。

## 2. 加载包设计

社区文档描述本地 mods 目录和工坊订阅目录两条加载来源；MOD 可由同名 JSON 清单、DLL 与 PCK 组成，具体按有无代码及资源决定。清单应声明稳定 id、名称、作者、版本、依赖和 affects_gameplay；本项目改变游戏行为，必须正确声明影响玩法，不能伪装成客户端外观 MOD。[加载和清单参考](https://github.com/Alchyr/ModTemplate-StS2/wiki/Modding-Basics)

未来创建清单前先验证安装版本加载器的实际格式。当前不生成虚假的最低兼容版本、作者、依赖版本或不存在的 DLL/PCK 引用。计划 id 为 FendaoZhengfeng，发布前验证唯一性。

本地包与工坊包从同一构建生成，内容与版本一致，不依赖开发机绝对路径。资源通过 MOD 资源位置定位。订阅包由 Steam 管理路径，不将工坊安装路径写死为本地 mods 路径。避免同一 id 的本地副本与工坊副本同时启用造成重复加载。

## 3. 工坊准备

Mega Crit 官方提供 sts2-mod-uploader。官方说明使用独立工作区，content 目录内容上传；workshop.json 配置发布信息；预览 image.png 小于 1 MB；已有条目的更新依赖保留 mod_id.txt。以上为核验时的文档说明，实际使用前检查所选工具版本。[官方上传器说明](https://github.com/megacrit/sts2-mod-uploader/blob/main/README.md)

后续准备独立 workshop 工作区，content 仅包含可分发运行包，不包含源码仓库、个人配置、反编译游戏代码、整份游戏资源或无权分发的第三方资产。首次发布后保留并管理条目标识，更新复用原条目，不能创建重复页面替代升级。

不在当前阶段运行上传器、登录 Steam、创建条目、订阅依赖或上传发布内容。公开发布需完成具体发布包与说明的审查后执行。

## 4. 未来发布验证

1. 确认目标游戏分支和版本，锁定必需依赖；不自动宣称兼容所有未来版本。
2. 使用正式加载器在本地独立目录加载，确认包发现、资源和清单匹配。
3. 联机参与者核对影响玩法的 MOD、协议与规则版本，记录不兼容原因。
4. 验证关闭 MOD 后原版可启动，且不会写坏原版存档；具体存档命名空间须查证。
5. 以干净环境验证发布包不依赖开发机路径和研究目录。
6. 分别验证本地与工坊订阅加载，防止重复 id；核对升级后旧局恢复策略。
7. 发布说明使用正式中文，明确竞速模式、29 项内容、依赖、版本、安装与已知限制，不将研究目标描述成已完成能力。

## 5. 资料与本机证据

资料核验日期为 2026-10-18。本机 release_info.json 声明 v0.111.0、commit 41cef1ea；本次只读核验安装根目录与 mods 目录，没有载入或执行游戏程序集。

主要资料：

- [Mega Crit 官方上传器](https://github.com/megacrit/sts2-mod-uploader)
- [社区模板与加载说明](https://github.com/Alchyr/ModTemplate-StS2/wiki/Modding-Basics)
- [BaseLib 源码](https://github.com/Alchyr/BaseLib-StS2)
- [BaseLib 文档](https://alchyr.github.io/BaseLib-Wiki/)

这些资料支持包格式与研究入口，不证明本项目独立战斗架构已经可行。
