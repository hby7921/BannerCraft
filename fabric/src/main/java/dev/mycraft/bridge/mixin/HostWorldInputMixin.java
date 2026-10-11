package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The host supplies world input; the MC window still receives genuine inventory-screen input. */
@Mixin(Minecraft.class)
public abstract class HostWorldInputMixin {
    private boolean mycraft$hostOwnsInput() {
        var player = ((Minecraft)(Object)this).player;
        return player != null && NativeWorldGuard.ownsHostInput(player.getUUID());
    }
    @Inject(method = "startUseItem", at = @At("HEAD"), cancellable = true)
    private void mycraft$stopWindowUse(CallbackInfo callback) { if (mycraft$hostOwnsInput()) callback.cancel(); }
    @Inject(method = "startAttack", at = @At("HEAD"), cancellable = true)
    private void mycraft$stopWindowAttack(CallbackInfoReturnable<Boolean> callback) {
        if (mycraft$hostOwnsInput()) callback.setReturnValue(false);
    }
    @Inject(method = "continueAttack", at = @At("HEAD"), cancellable = true)
    private void mycraft$stopWindowMining(boolean held, CallbackInfo callback) {
        if (mycraft$hostOwnsInput()) callback.cancel();
    }
}
