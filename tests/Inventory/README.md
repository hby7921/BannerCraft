# Vanilla inventory bridge verification

This slice transports the real MC 26.3 `InventoryScreen`, `CreativeModeInventoryScreen`, or already-open native containers such as crafting tables and furnaces. An existing `AbstractContainerScreen` is adopted only when its menu is exactly the live local player's current menu. C# draws its rendered frame and forwards input; it no longer owns slot layout, recipes, tabs, search results, or a creative item palette.

## Automated checks

- `dotnet run --project tests/Inventory/MycraftInventoryTests.csproj -c Release`: frame header, size, session/open token, freshness, closure and duplicate-frame boundaries.
- With the local JDK 25: `gradle -p fabric vanillaInventoryRegression`: actual MC `KeyEvent` interpretation, SDL mouse/modifier translation, input-file size and lease boundaries. The task is also part of `check`.
- Build against installed Bannerlord assemblies: `build-mycraft.ps1`.

## Inspected local API evidence

MC 26.3 unobfuscated client: `Gui.screen()/setScreen`, `Screen.mouseClicked(MouseButtonEvent, boolean)`, `mouseReleased`, `mouseDragged`, `mouseScrolled`, `keyPressed`, `charTyped`, `Screenshot.takeScreenshot(RenderTarget, Consumer<NativeImage>)`, `GameRenderer.render`, `NativeImage.getPixels`.

26.3 uses SDL, so its key E is 8, Escape is 41, mouse left/middle/right are 1/2/3, Shift mask is 3, Ctrl is 192 and Alt is 768. Transport logical keys are translated in `VanillaInventoryInput`; using old GLFW codes directly compiles but fails at runtime.

Installed Bannerlord API inspected with ILSpy: `GauntletLayer.Update(IReadOnlyList<int>)` receives committed Unicode characters; `SpritePart` resolves textures through its category; `Texture.CreateFromByteArray` and delayed release are available. `UIContext.Scale` maps the aspect-preserved frame to Gauntlet coordinates.

All input-file reads/writes and pixel conversion/file publication are on workers. GPU readback is requested on the MC render thread, native screen changes run on the client tick, and Bannerlord texture uploads happen on its mission/UI tick. The transport is scoped to `%LOCALAPPDATA%/Mycraft/vanilla-ui/<32 lowercase hex session>/`, with a unique token per inventory opening. Lease expiry is 3 seconds; the host releases its overlay after 8 seconds without a usable frame. Input is acknowledged/retransmitted in a maximum 256-event window. Frames are at most 1280×720 RGBA at 8 Hz, atomically published, with one GPU readback in flight. Downsampling occurs on the GPU before readback, bounding the staging buffer to 3,686,400 bytes. A closed screen overwrites its large frame with a 72-byte close record.

## 2026-10-07 live-regression diagnosis and repair

The observed battle returns at 20:31:42, 20:32:25, 20:35:03 and 20:36:51 correlate across Minecraft `latest.log` and Bannerlord `mycraft.log` with `MC bridge space obstructed` / `demo abort`. They are not evidence of an inventory process crash. The earlier full-screen opaque Minecraft framebuffer did visually replace the Bannerlord scene, explaining a concrete source of the apparent switch to Minecraft. Old transport records contained input against frames 18 and 104, so the inventory frame stream had run. Logs cannot establish whether the OS foreground window actually changed.

Local MC 26.3 bytecode confirms `Gui.setScreen` calls `MouseHandler.releaseMouse`, initialization and `updateTitle`, but has no window-raise/focus call. `InputConstants.grabMouse/releaseMouse` do call `SDL_WarpMouseInWindow`. The new narrow redirect suppresses that warp only during background bridge menu operations; it retains vanilla relative-mouse-mode changes and does not activate either game.

When this bridge owns a container, `GameRenderer.extract` now omits world rendering and its HUD while extracting the original Screen, recipe panels and tooltips over transparent clear. Its complete GUI canvas is preserved, so mouse coordinates need no cropped-panel approximation. Vanilla's translucent screen background darkens the Bannerlord scene beneath the original panel. Native `TRACY_BLIT` uses `ColorTargetState.DEFAULT`: RGBA8_UNORM, all four color-write bits, no blending. It copies the full canvas with nearest sampling into a bounded target. Unlike `Screenshot.takeScreenshot` (which forces alpha to 255), direct GPU readback preserves alpha. Minecraft's `TRANSLUCENT` blend produces premultiplied color on transparent clear, so the worker unpremultiplies color and flips the rows before uploading a straight RGBA texture to Bannerlord. Bannerlord's prefab no longer paints an opaque black full-screen backdrop.

Client inventory exceptions, GPU capture failures, and host texture/UI exceptions close only this overlay and release focus. They do not call `BridgeClient.close`, abort a battle, or change server-session state. Open, first-frame and local-close diagnostics now distinguish those events in both logs.

This rendering change still needs post-restart validation on the user's Intel UHD OpenGL driver: alpha/color correctness, item/entity preview lighting, scaled tooltips, background/minimized operation and mouse alignment. Compilation and byte-buffer tests cannot establish GPU compatibility. The engine shutdown log's ERC2236/nonzero GPU-reference message and watchdog EXIT_PROCESS_DEBUG_EVENT without an exception stack are recorded as observations, not assigned as an inventory crash cause. Running games were not controlled, stopped, restarted or updated during this repair.

## Required in-game acceptance (not run by the automated checks)

1. Open E in creative: real creative tabs, search, all catalog items and the player inventory tab appear, with no custom bottom palette. Search both Latin text and committed Chinese IME text.
2. Open E in survival: original armor/offhand slots, crafting grid/result and recipe book work. Shift-click armor and craft with actual server-authoritative item counts.
3. Exercise left pickup/place, right split/place-one, Shift transfer, left/right drag distribution, double-click collection, middle creative clone, hotbar number shortcuts, scroll tabs/list, Ctrl-A/C/V and Backspace in search.
4. Press E while searching: the native search box decides whether to consume it. Escape/E outside text input close via vanilla. Reopen and confirm counts agree with the hotbar.
5. Close while carrying a stack and disconnect while dragging. `AbstractContainerScreen.onClose()` calls the vanilla `LocalPlayer.closeContainer()` path; menu teardown handles the carried stack. Verify there is no duplicated/dropped-by-bridge item and no stuck host focus or shield use.
6. Move/resize or minimize MC, run the two games with different resolutions/UI scales, and confirm frame orientation/color and pointer alignment. Validate background rendering on the user's GPU; automated checks do not prove GPU readback works while minimized.
7. Right-click a crafting table/furnace: the native container should open automatically through the host's server-container signal. E can also adopt an already-open native container. Close it and confirm ordinary block actions resume. Switch between native containers or game modes while open: forwarding follows only the local player's current menu. Each event identifies the frame shown in the previous host UI render; clicks from the old container frame are acknowledged and discarded until the new container has actually been shown.
8. Open chat/pause/settings, die, disconnect or end the Bannerlord mission. Those screens are never adopted or replaced by this bridge. Non-container screen replacement ends forwarding and releases the overlay.

No actual gameplay/visual acceptance is claimed by these checks.
