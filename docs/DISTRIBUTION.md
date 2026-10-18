# 原生加载与 Steam 创意工坊发布

当前版本：0.1.1 开发构建。已完成本机原生加载与独立目录部署，尚未上传工坊。

## 本地目录与安装

- 源码仓库：`D:\Projects\sts2-fendao-zhengfeng`。
- 游戏目录：`D:\SteamLibrary\steamapps\common\Slay the Spire 2`。
- 本 MOD 运行目录：`mods\FendaoZhengfeng`。
- 运行文件：`FendaoZhengfeng.dll`、`FendaoZhengfeng.json`。

项目使用单 DLL，包含规则引擎和原生接入，不要求附属 Core DLL。界面使用 Godot 原生控件，目前无需 PCK。清单声明 has_dll、affects_gameplay，未声明不存在的资源包和 BaseLib 依赖。基于本机 v0.111.0 编译，其他版本兼容性需另行验证。

构建、研究、日志、缓存和第三方游戏程序集不会进入运行包。部署脚本仅复制本 MOD 的两份产物，不覆盖游戏 EXE、原版 DLL、原版 PCK 或其他 MOD。

所有参与者使用相同游戏和 MOD 版本，并在多人开局前启用“分道争锋”。不要同时启用本地包和同 ID 的工坊副本。移除本 MOD 时退出游戏，移除其专用子目录即可；独立检查点另存于 user://FendaoZhengfeng。

## 工坊准备

`scripts/Prepare-Workshop.ps1` 创建 `artifacts/workshop`：content 包含两份运行文件，workshop.json 为官方上传器元数据，默认私有可见性。重复准备保留已有 metadata 和 mod_id.txt，不自动上传。

正式发布仍需作者署名、许可、原创预览 image.png（小于 1 MB）和已验收的发布说明。首次上传后保留 mod_id.txt，后续更新复用该条目。上传前通过独立订阅环境验证，当前本地加载成功不能替代工坊订阅加载验证。

参考：[原生加载清单](https://github.com/Alchyr/ModTemplate-StS2/wiki/Modding-Basics)、[Mega Crit 官方上传器](https://github.com/megacrit/sts2-mod-uploader/blob/main/README.md)。格式以本机加载器和已核对文档为依据；实际发布时重新核对上传器版本。

## 验证范围

见 [VERIFICATION.md](VERIFICATION.md)。尚未创建远程 Git 仓库、Steam 工坊条目，也未执行公开发布。
