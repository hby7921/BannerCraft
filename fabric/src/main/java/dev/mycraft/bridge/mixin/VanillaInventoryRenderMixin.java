package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.VanillaInventoryBridge;
import net.minecraft.client.renderer.GameRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyVariable;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(GameRenderer.class)
public abstract class VanillaInventoryRenderMixin {
    // The real Screen still extracts itself and its tooltips. Skipping level extraction also skips the
    // Minecraft HUD and leaves the native GUI's alpha over a transparent cleared render target.
    @ModifyVariable(method = "extract", at = @At("HEAD"), argsOnly = true)
    private boolean mycraft$inventoryGuiOnly(boolean renderLevel) {
        return renderLevel && !VanillaInventoryBridge.ownsScreen();
    }
    @Inject(method = "render", at = @At("TAIL"))
    private void mycraft$captureInventory(CallbackInfo ci) { VanillaInventoryBridge.capture(); }
}
