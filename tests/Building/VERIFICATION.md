# Building adapter verification — 2026-10-07

Executed from the repository root:

```powershell
dotnet run --project tests/Building/MycraftBuildingTests.csproj
```

Result: **39/39 checks passed**. The project links the production `BlockGeometry.cs`;
it does not reimplement the geometry. Coverage includes negative-coordinate flooring,
MC/Bannerlord height conversion, terrain tolerance, rising slopes, invalid numeric input,
six entry faces and placement normals, reach/native-obstacle clipping, parallel and
inside-block rays, human/horse overlap, 256-cell limits, outward triangle winding,
and the five real opaque 16×16 RGBA texture files.

Native API review used BannerlordSage MCP, via its official `tools/call-mcp-tool.ts`
client. `Scene.BoxCast` accepts world-space box minimum/maximum, a sweep direction,
distance, and excluded body flags; native `SpawnedItemEntity` also sweeps downward
using this API. `BattleBlockScene.CanPlace` samples terrain at four corners and the
center, rejects agent bounds and native collision inside the proposed cell, and checks
all four corners against battle boundaries. Targeting stops at native props rather
than selecting a bridge block behind one.

Current collision creation retains the native `editor_cube` prefab's independent
physics component. It keeps the entity visible and removes only its instance render
component. Actual native A/B tests showed invisible collider entities do not participate
in ray queries in this runtime. No copying/clearing of shared PhysicsShape is used;
Mission lifetime retains resource wrappers through scene removal.

These tests do **not** execute TaleWorlds native rendering/physics. Required game
checks remain: block appearance and collider alignment; player/enemy/projectile
collision; placement beside terrain slopes, trees, buildings, and adjacent cubes;
nearer native objects blocking target selection; removal and disconnect cleanup.
No dynamic navmesh generation or NPC path routing around constructions is claimed.
Latest pure geometry/snapshot/TNT checks: 119 passed, including fractional support,
signed terrain quantization and presentation-only explosion scaling. Separate native
scene tests cover three full/compound create-raycast-remove cycles and TNT materials.
Native evidence and remaining live acceptance are recorded in
`.workflow/active/BW-20261005-hero-m1-steve/02-integration/TNT_VERIFICATION.md`.
The shared main-project build and deployment belong to the root integration task.
