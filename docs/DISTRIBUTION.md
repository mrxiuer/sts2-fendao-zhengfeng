# 原生加载与 Steam 创意工坊发布

0.5.1 中英文更新于2026-10-23更新原工坊条目 `3825045315`，保留好友可见性和BaseLib依赖；GitHub发布 `v0.5.1-preview`，保留0.5.0旧包。当前兼容基准为 `public-beta`，正式分支存在已确认的 API 差异，见 [API核验](API-COMPATIBILITY.md)。

0.5.0已于2026-10-23上传[Steam创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3825045315)，仅好友可见。GitHub同步0.5.0源码及preview安装包；发行页见README。

0.3.0 起清单使用对象式依赖 `{ "id": "BaseLib", "min_version": "3.4.7" }`，修复旧字符串依赖触发的原生清单迁移错误。`scripts/Package.ps1` 从已构建的 DLL/JSON 生成安装 ZIP 和 SHA256SUMS.txt，不包括第三方程序集或个人配置。

下载与通用安装步骤以仓库根 README 为准；下列绝对路径仅记录开发机布局。

## 本地目录与安装

- 源码仓库：`D:\Projects\sts2-fendao-zhengfeng`。
- 游戏目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2`。
- 本 MOD 运行目录：`mods\FendaoZhengfeng`。
- 运行文件：`FendaoZhengfeng.dll`、`FendaoZhengfeng.json`。

项目使用单 DLL，包含规则引擎、原生接入、67张128×128显示图标及其他美术资源，不要求附属 Core DLL。界面使用 Godot 原生控件，目前无需 PCK。清单声明 has_dll、affects_gameplay，不声明不存在的资源包；0.1.4 起声明 BaseLib 依赖，用于独立模组配置条目。每位玩家须自行安装并启用 BaseLib，本包不重分发其 DLL/PCK。基于本机 v0.111.0 编译，其他版本兼容性需另行验证。

构建、研究、日志、缓存和第三方游戏程序集不会进入运行包。部署脚本仅复制本 MOD 的两份产物，不覆盖游戏 EXE、原版 DLL、原版 PCK 或其他 MOD。

所有参与者使用相同游戏和 MOD 版本，并在多人开局前启用“分道争锋”。不要同时启用本地包和同 ID 的工坊副本。移除本 MOD 时退出游戏，移除其专用子目录即可；独立检查点另存于 user://FendaoZhengfeng。

## 工坊准备

`scripts/Prepare-Workshop.ps1` 创建 `artifacts/workshop`：content 包含两份运行文件，workshop.json 为官方上传器元数据，强制使用用户要求的好友可见性 `friends_only`。重复准备从仓库模板刷新标题、双语描述、更新说明及依赖，保留 mod_id.txt，并将旧工作区的可见性改为好友可见，不自动上传。

作者沿用清单中的“分道争锋项目组”；预览复用本项目已有原创模式图标的显示纹理，小于1MB，发布说明见docs/releases/0.5.0.md。未添加未经用户授权的开源许可证。首次上传后保留 mod_id.txt，后续更新复用该条目。上传前通过独立订阅环境验证，当前本地加载成功不能替代工坊订阅加载验证。

官方上传器工作区模板明确支持 `private`、`public`、`unlisted` 和 `friends_only`；本项目采用最后一种。配置依据见[官方工作区说明](https://github.com/megacrit/sts2-mod-uploader/blob/main/template/README.md)。准备文件不等于已经发布，上传与订阅加载验证须在其余验收完成后执行。

参考：[原生加载清单](https://github.com/Alchyr/ModTemplate-StS2/wiki/Modding-Basics)、[Mega Crit 官方上传器](https://github.com/megacrit/sts2-mod-uploader/blob/main/README.md)。格式以本机加载器和已核对文档为依据；实际发布时重新核对上传器版本。

## 验证范围

见 [VERIFICATION.md](VERIFICATION.md)。GitHub 地址为 https://github.com/mrxiuer/sts2-fendao-zhengfeng ，安装包通过 Releases 分发；本轮Steam条目3825045315，原生只读查询已确认FriendsOnly和BaseLib依赖。


本轮发布门槛按用户最新要求收紧为必要加载/联机/保存恢复/运行包校验，不再等待逐个事件或完整All穷举。官方上传器v0.2.0从MegaCrit官方GitHub发布下载，工作区依赖3737335127（本机BaseLib清单确认）。运行内容仅两份本模组文件。


## 0.5.0 实际发布结果

官方上传器返回成功，条目3825045315。随后使用当前登录Steam账号的原生GetQueryUGCResult只读核对：Result=OK、Visibility=FriendsOnly、Bytes=13534156、BaseLib依赖3737335127存在。匿名API不返回好友条目，不能用其FileNotFound结果判断上传失败。

条目身份固定保存在packaging/workshop-item-id.txt，准备脚本在生成工作区时恢复该ID并拒绝不一致，后续更新复用同一条目。用户随后授权同步GitHub，0.5.0源码及preview发行包一并更新。新包仅两份运行文件，DLL SHA256 F79FA2218AECEB142EBEC3DA8B2C104B7C986EAEAA3122A3387F4E8CA9392C64，与本机构建/部署一致。
