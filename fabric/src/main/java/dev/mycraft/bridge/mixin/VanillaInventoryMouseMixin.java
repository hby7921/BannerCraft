package dev.mycraft.bridge.mixin;

import com.mojang.blaze3d.platform.Window;
import dev.mycraft.bridge.VanillaInventoryBridge;
import net.minecraft.client.MouseHandler;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(MouseHandler.class)
public abstract class VanillaInventoryMouseMixin {
    @Inject(method = "getScaledXPos(Lcom/mojang/blaze3d/platform/Window;)D", at = @At("HEAD"), cancellable = true)
    private void mycraft$inventoryX(Window window, CallbackInfoReturnable<Double> cir) {
        if (VanillaInventoryBridge.ownsScreen()) cir.setReturnValue(VanillaInventoryBridge.mouseX());
    }
    @Inject(method = "getScaledYPos(Lcom/mojang/blaze3d/platform/Window;)D", at = @At("HEAD"), cancellable = true)
    private void mycraft$inventoryY(Window window, CallbackInfoReturnable<Double> cir) {
        if (VanillaInventoryBridge.ownsScreen()) cir.setReturnValue(VanillaInventoryBridge.mouseY());
    }
}
