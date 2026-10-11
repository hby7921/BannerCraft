# BannerCraft 构建与运行

以下步骤面向 Windows x64 源码使用者。当前版本固定为 Bannerlord `v1.4.8`、Minecraft Java `26.3`、Fabric Loader `0.19.5`、Fabric API `0.161.0+26.3`。版本依据是本仓库项目配置，不代表其他游戏版本已适配。

## 1. 准备工具与游戏

安装 .NET SDK 10、PowerShell 7、JDK 25、Gradle 9.7.0、Python 3 和 Pillow。Minecraft 启动器需先完成游戏与依赖库下载，并配置上述 Fabric Loader/API；Bannerlord 需安装 Harmony `2.4.2`。

以下命令都在仓库根目录、PowerShell 7 中运行。按自己的安装位置设置变量，路径示例不需要照搬：

```powershell
$gameDir = 'C:\Games\Mount & Blade II Bannerlord'
$clientJar = 'C:\Minecraft\versions\26.3\26.3.jar'
$jdkDir = 'C:\Tools\jdk-25'
$libraryDir = 'C:\Minecraft\libraries'
$gradle = 'C:\Tools\gradle-9.7.0\bin\gradle.bat'
$env:JAVA_HOME = $jdkDir
$stage = Join-Path (Get-Location) '.build-game\stage'
```

`$clientJar` 必须是完整的原版 MC 26.3 客户端 JAR；`$libraryDir` 必须包含启动器下载的该版本 Java 依赖，包含 Gson 等库。当前 PowerShell 素材工具使用 `System.Drawing.Common`，需要在 Windows 下运行。

游戏脚本可显式传 `-GameDir`（探针使用 `-GameRoot`）。不传参数时，依次读取 `BANNERLORD_GAME_DIR` 环境变量和仓库根目录的 `.bannercraft.local.json`。单独运行 `build-mc-model.ps1` 时，可选皮肤读取 `BANNERCRAFT_SKIN_FILE` 或配置中的 `skinFile`，未配置时使用原版 Steve；通用 `prepare-local-assets.ps1` 入口始终显式选择原版 Steve。

本机配置示例（文件已由 Git 忽略）：

```json
{
  "bannerlordGameDir": "C:/Games/Mount & Blade II Bannerlord",
  "skinFile": ""
}
```

路径配置只保存在使用者本机。不要将该文件加入源码或发行包。

## 2. 在本机生成资源

源码仓库保留生成器，不附带 Minecraft 的纹理、模型、GUI 图集或原版测试样本。新增入口会从本机游戏生成这些文件：

```powershell
python -m pip install Pillow
pwsh -NoProfile -File .\tools\prepare-local-assets.ps1 `
    -ClientJar $clientJar -Jdk $jdkDir -Libraries $libraryDir
```

生成过程包括 Steve/物品/盾牌、方块与 TNT、爆炸粒子、船和生物模型、武器纹理、头颅 GUI 样本及 HUD 图集。输出位于源码树的 `_Module`、测试样本目录和 `.build-game`，均被 Git 排除。该工具只准备本机文件；不安装模组，不启动游戏。

单独工具仍可通过参数使用：`build-mc-model.ps1`、`build-block-assets.ps1`、`build-tnt-effects.ps1`、`build-entity-assets.py`、`build-weapon-assets.py`、`build-mc-hud.ps1`。默认项目路径均以工具所在仓库为基准，游戏位置和可选皮肤由参数或本机配置提供。

## 3. 构建 Minecraft 端

```powershell
& $gradle -p fabric build --no-daemon
if ($LASTEXITCODE -ne 0) { throw 'Fabric build failed.' }
```

输出：`fabric/build/libs/mycraft-bridge-0.1.0.jar`。`build` 会执行桥接、盾牌和原版背包的 Java 回归，并生成 C# 跨语言检查需要的 `fabric/build/bridge-fixtures.jsonl`。Loom 当前为 `1.18-SNAPSHOT`；首次构建需要网络解析 Gradle/Fabric 依赖。

隔离 MC 原生测试可单独运行：

```powershell
& $gradle -p fabric runGameTest --no-daemon
if ($LASTEXITCODE -ne 0) { throw 'Minecraft GameTests failed.' }
```

该任务使用 `fabric/build/run/tntGameTest` 的测试世界。它运行游戏逻辑与生产 mixins，不是双游戏联机验收；某些原生测试存在历史异步 tick 时序问题，报告失败时应保留实际结果。

## 4. 骑砍端与 C# 检查

生成资源并完成 Fabric 构建后，从仓库根目录运行已有检查程序：

```powershell
$projects = @(
    'tests/Bridge/MycraftBridgeTests/MycraftBridgeTests.csproj',
    'tests/Demo/MycraftDemoTests.csproj',
    'tests/Inventory/MycraftInventoryTests.csproj',
    'tests/Visuals/MycraftVisualTests.csproj',
    'tests/EntityVisuals/MycraftEntityVisualTests.csproj'
)
foreach ($project in $projects) {
    dotnet run --project $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Check failed: $project" }
}

pwsh -NoProfile -File .\build-mycraft.ps1 -GameDir $gameDir -Stage $stage
```

`Demo` 和 `Inventory` 可直接从源码运行；`Bridge` 需要 Java 序列化样本，`Visuals`、`EntityVisuals` 需要本机生成的资源。检查程序不加载完整骑砍战场，不能证明真实宿主渲染、碰撞或 AI 都通过。

`Visuals` 默认核对所选皮肤与资源清单的 SHA256，可使用原版 Steve 或自定义皮肤。绿色配色检查是可选诊断参数：`dotnet run --project tests/Visuals/MycraftVisualTests.csproj -c Release -- --expect-green-skin`。

骑砍输出：`.build-game/stage/Modules/Mycraft`。项目使用 `Build.LocalGame.props` 编译时引用使用者本机 TaleWorlds DLL，引用设置为不复制到发布物。生成的主 DLL 名称为 `Mycraft.v1.4.8.dll`，显示名称为 BannerCraft。

还有两类检查需要额外的原生客户端样本，因此不在上述通用列表里：

- `tests/Building` 除本机方块/粒子资源外，还读取 `Fixtures/redstone_wire_power0.bin`、`redstone_wire_power15.bin` 和 `lever_original.rgba`。这些原版纹理/烘焙模型快照已从 Git 排除，标准资产入口不会重建它们；需要另外从真实 MC 客户端采集。当前尚未整理成通用采样入口，不应在缺样本时声称该程序通过。
- `tests/GenericMobVisuals` 需要专用原版模型样本。当前 `tools/test-generic-mob-models.py` 保留本机 JDK、Prism libraries 和 Loom 缓存路径约定；独立克隆后需要按实际环境调整这些路径，再运行生成/校验流程。

## 5. 本机安装与启动

关闭 Bannerlord 后安装本机生成的包：

```powershell
pwsh -NoProfile -File .\install-mycraft.ps1 `
    -GameDir $gameDir -PackageDir (Join-Path $stage 'Modules\Mycraft')
```

安装器会备份已有 `Modules/Mycraft`。将自己构建的桥接 JAR 放到独立 MC Fabric 实例的 `mods`，保留匹配版本的 Fabric API；关闭 MC 后替换 JAR，重新启动才会加载新版本。

在骑砍启动器加载 Harmony、Native、SandBoxCore、CustomBattle、Sandbox 和 BannerCraft。模块目录和 ID 仍为 `Mycraft`。先开普通自定义野战，部署后在 MC 使用存活的主世界玩家输入 `/mycraft connect`。默认连接 `127.0.0.1:38765`，两款游戏应运行在同一台电脑。

可以使用参数化启动入口：

```powershell
pwsh -NoProfile -File .\start-bannerlord-demo.ps1 -GameDir $gameDir
# 固定预设：300 巴弓 + 原版玩家指挥官，对 5 个真实凋零。
pwsh -NoProfile -File .\start-300-fians-5-withers.ps1 -GameDir $gameDir
```

在主菜单或自定义战斗设置页，也可用骑砍控制台 `mycraft.fians_wither 5 300` 选预设。战斗开始后仍需 `/mycraft connect`。凋零保留出生无敌和半血箭免疫；最新预设的持续实战及最终胜利/退场尚需验收。

根目录 `start-minecraft-demo.cmd` 使用仓库内 `runtime/minecraft/PrismLauncher` 的便携启动器约定。也可直接在自己的启动器启动匹配实例；启动器账号和存档不属于源码分发内容。

## 发布物边界

本机生成的完整 `_Module` 或 stage 包含 MC 派生素材，不能因为源码采用 MIT 就把它们作为 MIT 素材包上传。GitHub 首次公开范围为源码、生成器和文档；发行归档应按 [开源发布说明](OPEN_SOURCE.md) 核对内容。
