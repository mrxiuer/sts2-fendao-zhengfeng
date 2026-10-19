# 原生加载与 Steam 创意工坊发布

当前版本：0.3.0 开发预览。通过 GitHub Releases 分发，尚未上传工坊。

0.3.0 清单使用对象式依赖 `{ "id": "BaseLib", "min_version": "3.4.7" }`，修复旧字符串依赖触发的原生清单迁移错误。`scripts/Package.ps1` 从已构建的 DLL/JSON 生成安装 ZIP 和 SHA256SUMS.txt，不包括第三方程序集或个人配置。

下载与通用安装步骤以仓库根 README 为准；下列绝对路径仅记录开发机布局。

## 本地目录与安装

- 源码仓库：`D:\Projects\sts2-fendao-zhengfeng`。
- 游戏目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2`。
- 本 MOD 运行目录：`mods\FendaoZhengfeng`。
- 运行文件：`FendaoZhengfeng.dll`、`FendaoZhengfeng.json`。

项目使用单 DLL，包含规则引擎、原生接入及 52 张透明 PNG 图标，不要求附属 Core DLL。界面使用 Godot 原生控件，目前无需 PCK。清单声明 has_dll、affects_gameplay，不声明不存在的资源包；0.1.4 起声明 BaseLib 依赖，用于独立模组配置条目。每位玩家须自行安装并启用 BaseLib，本包不重分发其 DLL/PCK。基于本机 v0.111.0 编译，其他版本兼容性需另行验证。

构建、研究、日志、缓存和第三方游戏程序集不会进入运行包。部署脚本仅复制本 MOD 的两份产物，不覆盖游戏 EXE、原版 DLL、原版 PCK 或其他 MOD。

所有参与者使用相同游戏和 MOD 版本，并在多人开局前启用“分道争锋”。不要同时启用本地包和同 ID 的工坊副本。移除本 MOD 时退出游戏，移除其专用子目录即可；独立检查点另存于 user://FendaoZhengfeng。

## 工坊准备

`scripts/Prepare-Workshop.ps1` 创建 `artifacts/workshop`：content 包含两份运行文件，workshop.json 为官方上传器元数据，默认私有可见性。重复准备保留已有 metadata 和 mod_id.txt，不自动上传。

正式发布仍需作者署名、许可、原创预览 image.png（小于 1 MB）和已验收的发布说明。首次上传后保留 mod_id.txt，后续更新复用该条目。上传前通过独立订阅环境验证，当前本地加载成功不能替代工坊订阅加载验证。

参考：[原生加载清单](https://github.com/Alchyr/ModTemplate-StS2/wiki/Modding-Basics)、[Mega Crit 官方上传器](https://github.com/megacrit/sts2-mod-uploader/blob/main/README.md)。格式以本机加载器和已核对文档为依据；实际发布时重新核对上传器版本。

## 验证范围

见 [VERIFICATION.md](VERIFICATION.md)。GitHub 地址为 https://github.com/mrxiuer/sts2-fendao-zhengfeng ，安装包通过 Releases 分发；尚未发布 Steam 工坊。
