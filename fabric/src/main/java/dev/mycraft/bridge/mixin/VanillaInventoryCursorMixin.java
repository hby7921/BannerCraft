package dev.mycraft.bridge.mixin;

import com.mojang.blaze3d.platform.InputConstants;
import dev.mycraft.bridge.VanillaInventoryBridge;
import org.lwjgl.sdl.SDLMouse;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** Background menu transitions must not warp the user's cursor into the auxiliary Minecraft window. */
@Mixin(InputConstants.class)
public abstract class VanillaInventoryCursorMixin {
    @Redirect(method = {"grabMouse", "releaseMouse"}, at = @At(value = "INVOKE", target = "Lorg/lwjgl/sdl/SDLMouse;SDL_WarpMouseInWindow(JFF)V"))
    private static void mycraft$keepHostCursor(long window, float x, float y) {
        if (!VanillaInventoryBridge.suppressBackgroundCursorWarp()) SDLMouse.SDL_WarpMouseInWindow(window, x, y);
    }
}
