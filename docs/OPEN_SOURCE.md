# BannerCraft 开源发布说明

整理日期：2026-10-11。项目公开名称：**BannerCraft**；推荐 GitHub 仓库名：`BannerCraft`。本项目维护者已选择 MIT，源码版权署名采用现有元数据中的 `hby7921`。

## 采用的协议

根目录 [LICENSE](../LICENSE) 使用标准 MIT 原文，包含：

```text
Copyright (c) 2026 hby7921
```

MIT 允许其他人学习、修改、再分发、商用和闭源制作派生版本。复制或分发本项目代码的重要部分时，对方必须保留版权声明和完整 MIT 许可文本；这项要求也适用于闭源分发。声明可以随软件放在许可文件或第三方许可说明中。

MIT 不要求在界面显著位置展示原作者，也不要求所有派生项目继续使用 BannerCraft 名称。README 中欢迎注明项目来源，是致谢建议，没有给标准 MIT 添加新限制。原作者的声明应被保留，但它不意味着派生作者独立新增的全部代码都属于原作者。[MIT 官方选型说明](https://choosealicense.com/licenses/mit/)

常见选择的区别如下，不把“放在公开仓库”与“有开源授权”混为一谈：

| 协议 | 商用 / 闭源派生分发 | 主要要求 |
| --- | --- | --- |
| [MIT](https://choosealicense.com/licenses/mit/) | 允许 / 允许 | 保留版权和许可，条款短，适合方便复用的社区项目 |
| [Apache-2.0](https://choosealicense.com/licenses/apache-2.0/) | 允许 / 允许 | 保留许可与适用声明，标注修改，含明确专利授权 |
| [GPL-3.0](https://choosealicense.com/licenses/gpl-3.0/) | 允许 / 受 copyleft 限制 | 分发受 GPL 覆盖的派生作品时，按 GPL 提供相应源码及授权 |

它们都允许商业用途。若附加“不得商用”等用途限制，就不能直接称为符合开放源代码定义的标准开源许可。[OSI 开源定义](https://opensource.org/osd)

GitHub 上常见的标准许可包括 MIT、Apache-2.0 和 GPL；这里没有用未经核实的排名来替代选型。我们已核对的 SkyCraft、EldenCraft、Minecraft-Ring、minecraft-in-bannerlord 固定版本均为 MIT。公开仓库如果缺少许可文件，其他人并不会自动得到一般性的修改与再分发授权。[GitHub 许可说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)

## 本次整理结果

- `README.md`：项目定位、已实现功能、当前版本、未完成部分与源码入口。
- `LICENSE`：BannerCraft 的 MIT 协议与作者署名。
- `THIRD_PARTY_NOTICES.md`：参考项目的固定来源、原作者和许可边界。
- `.gitignore`：排除游戏运行环境、账号、存档、提取素材、构建产物、缓存和本机过程记录。
- `docs/BUILDING.md`：从源码准备资源、构建、验证和接入的步骤。
- `tools/prepare-local-assets.ps1`：通过本机游戏输入生成所需资源，避免要求别人复制开发机目录。

原先的 STATUS、CODEX_ACCEPTANCE、研究报告和 `.workflow` 记录保留在本机作为历史证据，并由 Git 排除。公开说明只整理当前能力，没有把历史构建或局部测试写成全套实战成功。

## 本次整理的验证

从独立、路径含空格的源码副本运行本机资源入口，七步生成全部成功；没有复制开发机素材或 Building 原版样本。随后使用新生成资源验证：

| 检查 | 结果 |
| --- | --- |
| Demo 回归 | 39 项通过 |
| Inventory 帧/会话回归 | 20 项通过 |
| Visuals 模型检查 | 84 项通过 |
| EntityVisuals 原版模型/纹理/姿态检查 | 3707 项通过 |
| 公开文件核对 | 源码/配置/文档均为文本；无二进制素材，常见真实凭据扫描未匹配 |
| 文档与脚本 | 本地链接、项目名称/协议元数据、PowerShell 语法检查通过 |

Visuals 验证所选皮肤与清单的 SHA256，可选配色诊断通过 `--expect-green-skin` 参数开启。没有重跑完整骑砍构建、MC GameTests 或双游戏实战；历史原生检查与当前未验收边界仍按 README 说明。

## 可以公开的内容

原创 C#、Java、PowerShell、Python 源码，原创模块 XML/Gauntlet 布局、默认配置、协议文档、测试程序和项目说明。

不随源码公开的内容包括：

- `runtime/`：启动器、游戏/JDK 下载、账号记录、存档、运行缓存。
- `.toolchain/`、`.research/`、`.build-game/`：本机工具链、上游参考 checkout、编译与部署产物、截图及诊断证据。
- `_Module/Visuals`、GUI 图集及其生成 XML：从 MC 客户端提取的像素、模型和图集。
- `tests/Building/Fixtures`、`tests/EntityVisuals/Fixtures`、`tests/GenericMobVisuals/Fixtures`、头颅 GUI 样本：原版几何、纹理和渲染快照。
- DLL、JAR、账号/密钥文件、运行日志等。
- `.bannercraft.local.json` 等本机路径配置。公开脚本的项目路径均相对仓库，游戏目录与可选皮肤由参数或本机配置提供。

Minecraft EULA 区分原创 Mod 与游戏原有代码/内容，并保留对原版纹理等内容的权利。本仓库采用公开生成器、由使用者本机提供原版输入的方式；MIT 许可不替代两款游戏自己的条款。[Minecraft EULA](https://www.minecraft.net/en-us/eula)

## GitHub 首次公开

建议仓库简介：

> Minecraft gameplay inside Mount & Blade II: Bannerlord custom battles.

建议主题：`bannerlord`、`minecraft`、`fabric`、`modding`、`csharp`、`java`。首次公开定位为实验源码项目，不承诺所有版本兼容或完整军队实战已经验收。

本地整理与公开上传是两个步骤。首次提交前使用 `git status --short` 和 `git diff --cached --stat` 检查待提交文件，尤其确认 runtime、素材和账号文件没有被追踪。`.gitignore` 不会移除已追踪文件，也不会清除旧提交中的内容；当前目录此前没有 Git 历史。

从已审查提交生成源码归档可以使用 `git archive --format=zip HEAD -o BannerCraft-source.zip`。不要把整个工作目录、`_Module` 或 `.build-game/stage` 直接压缩上传。
