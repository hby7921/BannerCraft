# Original entity visuals

`python tools/build-entity-assets.py` executes the pinned Minecraft 26.3 client's actual layer builders and `LayerDefinition.bakeRoot`. It exports all original cube vertices, normals, mirrored UVs, hierarchy and initial poses. The wither skeleton uses `SkeletonModel.createBodyLayer().apply(MeshTransformer.scaling(1.2f))`; it does not use Steve's mesh or Bannerlord's human bones. Original boat, chest-boat, raft and chest-raft layers and all 22 original wood-kind textures are included. The asset manifest contains the source client-JAR hash, texture source paths, original dimensions and hashes.

Run the independent pure regression with:

```powershell
dotnet run --project tests/EntityVisuals/MycraftEntityVisualTests.csproj
```

The test compares C# pose fields and hierarchy matrices with Java's original `SkeletonModel.setupAnim`, `AbstractBoatModel.setupAnim`, `ModelPart.translateAndRotate` and `PoseStack`. Fixtures include walking, idle, normal sword swings, aggressive melee, independent paddle phases, positive/negative damage wobble, bubble tilt and underwater suppression. Animation trigonometry uses the original `Mth` sine table; boat bubble rotation preserves the original non-normalized quaternion axis `(1,0,1)`. Texture-alpha clipping removes invisible original cube surfaces before native mesh upload.

The wrappers are independent scene entities. Host mission code supplies the translated MC feet/boat position and manages UUID/proxy lifetime. They do not create agents, change AI, health or collision. Native wrappers, textures and materials are retained by `NativeSceneResources` until their mission is collectible; `Dispose` removes only owned scene entities and is idempotent. Both wrappers are initially hidden until their first `Update`.

`WitherSkeletonVisual` supports the normal adult, right-handed, grounded wither-skeleton pose and original vanilla sword sprites. Riding, swimming poses and arbitrary replacement equipment are outside this slice. `BoatVisual` rejects unknown registry kinds rather than substituting a generic model. Vanilla boat rendering uses yaw and intentionally ignores entity pitch. This is a model/pose port; Minecraft lighting, water depth masking, outline and fog shaders are not replaced in Bannerlord. Pure tests do not replace a native scene render/lifecycle smoke test.
