# BannerCraft

把 Minecraft 的物品、建造和生物带进《骑马与砍杀 2》的自定义战斗。

**Minecraft gameplay inside Mount & Blade II: Bannerlord custom battles.**

BannerCraft 是一个实验中的双端模组：骑砍负责战场、士兵 AI 和指挥，Minecraft 负责真实物品、库存、生物和原版战斗规则。两款游戏在同一台 Windows 电脑上运行，通过本机桥接连接。当前版本为 `0.1.0`，主要范围是单人自定义野战。

## 已经做了什么

| 部分 | 当前实现 |
| --- | --- |
| 玩家与界面 | 接入后显示 MC 玩家外观、主副手、九格热栏、生命和凋零首领血条；背包和容器画面来自实际 MC 原版界面 |
| 库存与交互 | 真实选槽、背包操作、主副手使用、原版右键优先级；生存消耗、耐久和创造模式规则由 MC 处理 |
| 战斗 | 近战、盾牌、弓、弩、矛、三叉戟，以及实际投射物与伤害回执；保留 MC 冷却、下坠、附魔和条件判定 |
| 建造与红石 | 本场方块放置、挖掘、烘焙模型和碰撞同步；活塞、红石供电、发射器箭矢等原版交互 |
| TNT | 放置、点燃、连爆、爆炸伤害和原版粒子素材的宿主显示；默认同场上限 100 |
| 生物与船 | 凋零骷髅、真实三头凋零、通用刷怪蛋生物，以及船、运输船和竹筏的模型与状态同步 |
| 军队预设 | 巴丹尼亚费奥纳冠军对战真实凋零；包含 200、300、600 名士兵和多个凋零数量的测试入口，保留原版指挥官与指挥 |
| 桥接稳定性 | 会话隔离、严格序号、伤害去重、可靠回执、位置快照合并、有界队列和临时地面支撑清理 |

这些内容处在不同验证阶段。已有两端构建、程序回归、MC 原生 GameTests，以及骑砍中的隔离场景检查；**它们不等于完整双游戏实战全部验收通过**。最近的 300 巴弓对 5 凋零预设仍需持续实战、最后一只首领死亡后的胜利与正常退场验证。

当前尚未交付：战役模式、多人联机、完整地形与导航网格镜像、完整鞘翅飞行、船骑乘输入，以及全 MC 生物阵营选择界面。复杂地形、输入衔接、声音和特殊渲染效果还需要实战完善。

## 运行环境

| 组件 | 当前开发版本 |
| --- | --- |
| 平台 | Windows x64 |
| Mount & Blade II: Bannerlord | `v1.4.8` |
| Bannerlord.Harmony | `2.4.2` |
| Minecraft Java Edition | `26.3` |
| Fabric Loader / Fabric API | `0.19.5` / `0.161.0+26.3` |
| 开发工具 | .NET SDK 10、JDK 25、Gradle 9.7.0、PowerShell 7、Python 3 与 Pillow |

两款游戏都需要使用者自己的合法安装。GitHub 源码不包含游戏程序、原版纹理、模型、启动器账号或存档。Minecraft 资源在使用者本机生成，步骤见 [构建与运行](docs/BUILDING.md)。

## 如何接入

1. 按 [构建与运行](docs/BUILDING.md) 生成本机资源、构建并安装两端模组。
2. 在 Minecraft 进入独立测试世界，在骑砍进入普通自定义野战并开始战斗。
3. 在 MC 聊天中输入 `/mycraft connect`，然后回到骑砍。
4. 滚轮选择 MC 热栏，`E` 打开背包，左右键使用真实 MC 物品。退出会话可用 `/mycraft disconnect`，状态可用 `/mycraft status`。

未接入时使用普通骑砍人物和装备。接入后的规则取决于真实 MC 玩家当前模式。建议先在独立超平坦测试世界和小规模野战中验证，再尝试军队预设。

正式名称是 **BannerCraft**。源码目录、内部模组 ID、文件名和 `/mycraft` 命令沿用现有名称，以兼容当前配置；在 GitHub 上可以直接把仓库命名为 `BannerCraft`。

## 源码结构

```text
src/Mycraft/       骑砍 C# 模组：桥接、战斗、建造、模型和 Gauntlet UI
fabric/           Minecraft Fabric Java 模组与原生 GameTests
protocol/         两端协议与交互扩展说明
tests/            C# 回归检查与模型校验程序
tools/            本机资源生成、安装准备与诊断工具
docs/             构建说明和开源发布说明
```

两个主要入口分别是 `src/Mycraft/SubModule.cs` 和 `fabric/src/main/java/dev/mycraft/bridge/MycraftBridge.java`。通信采用 `127.0.0.1:38765` 上的 UTF-8 JSON Lines；只有本机单会话连接。

## 参与开发

欢迎通过 Issues 提交复现步骤，通过 Pull Requests 提交小范围修改。请注明两款游戏版本、实际使用的 DLL/JAR 和验证方式。协议字段或会话规则修改需要同步 C#、Java 和 `protocol/`；附带测试结果，并区分程序检查、原生游戏测试和完整双端实战。

提交代码前确认它可按本项目许可贡献。不要提交游戏 DLL/JAR、提取的原版素材、账号、存档、运行日志或本机模型快照。

## 许可与致谢

原创源码、脚本和文档采用 [MIT License](LICENSE)，版权署名为 **hby7921**。MIT 允许修改、商用和闭源分发；复制或分发本项目代码的重要部分时，需要保留版权声明和完整许可文本。欢迎在衍生项目说明中注明 BannerCraft 的来源。

MIT 授权范围不包含 Minecraft、Bannerlord 的游戏内容或第三方组件；它们仍适用各自条款。BannerCraft 是社区项目，与 Mojang、Microsoft 或 TaleWorlds 没有官方隶属关系。

感谢 SkyCraft、EldenCraft、Minecraft-Ring 和 minecraft-in-bannerlord 提供研究参考。固定来源、版权声明和许可边界见 [第三方说明](THIRD_PARTY_NOTICES.md)；协议选择和发布准备见 [开源发布说明](docs/OPEN_SOURCE.md)。
