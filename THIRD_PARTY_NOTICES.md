# BannerCraft third-party notices

BannerCraft's original source, scripts and documentation are licensed under the
MIT license in [LICENSE](LICENSE). Third-party works retain their own copyrights
and licenses. The project name does not imply endorsement by any upstream author,
Mojang, Microsoft or TaleWorlds.

## Research references

The following source checkouts informed development. Versions below identify the
local revisions reviewed, rather than asserting compatibility with every current
upstream release. Their full repositories and runtime distributions are excluded
from this source repository.

| Project | Reviewed revision | Relationship |
| --- | --- | --- |
| [SkyCraft](https://github.com/chasmlol/SkyCraft) | `bfcaf178524b92c2cdeb88e4ce0f13ef9ded6f32` | Minecraft/host split and actor proxy design; `BridgeTargetEntity.java` explicitly acknowledges the pattern |
| [EldenCraft](https://github.com/SocketByte/EldenCraft) | `b8541b3404d43f4b7debca46cebf2a3344b90e27` | Local terrain support, native gameplay authority and input/GUI research |
| [Minecraft-Ring](https://github.com/siddoff/Minecraft-Ring) | `711015afa67ab25c7b9597c68038718d1cef322e` | README and license research; this is not a claim that its native implementation was ported |
| [minecraft-in-bannerlord](https://github.com/chufeng-official/minecraft-in-bannerlord) | `9bfca593864950d165fa63a4f3526851a100dfca` | Bannerlord block prototype and API research |

All four pinned checkouts contain MIT licenses. Their original copyright notices
are retained below as conservative attribution; these notices do not claim that
each upstream author owns all of BannerCraft or that their entire projects were
copied. Preserve the applicable upstream notice when reusing upstream portions.

### Original notices and shared MIT terms

```text
SkyCraft
Copyright (c) 2026 chasmlol

EldenCraft
Copyright (c) 2026 Adam Grzegorzewski

Minecraft-Ring
Copyright (c) 2026 siddoff
Copyright (c) 2026 justbustin (upstream bridge)

minecraft-in-bannerlord
Copyright (c) 2026 minecraft-in-bannerlord contributors

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Games and locally generated assets

Minecraft game software, original textures, skins, models, particles and GUI
assets belong to their respective rights holders. They are not licensed by
BannerCraft's MIT license. Local export tools read the user's own Minecraft
installation. Exported assets and native renderer fixtures are excluded from Git.
See the [Minecraft EULA](https://www.minecraft.net/en-us/eula) and
[Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines).

Mount & Blade II: Bannerlord and TaleWorlds assemblies/content retain their
respective licenses. The C# build references installed game assemblies without
copying them into the mod output. A legal game installation is required.

Generated appearances and inventory frames under the user's local application
data are runtime data, not source artifacts for publication.

## Build and runtime dependencies

Fabric Loader, Fabric API, Fabric Loom, Harmony, the Bannerlord BUTR module SDK,
reference assemblies, JDK, Gradle, Python and Pillow retain their respective
licenses. Package managers resolve build dependencies; they are not relicensed by
the root LICENSE. A locally used launcher, such as Prism Launcher, also retains
its own license and account data.

No bundled game runtime, launcher, JDK or third-party binary is included in this
source publication. If a later release includes a third-party component, preserve
that component's actual license and required notices in the release, in addition
to this summary.
