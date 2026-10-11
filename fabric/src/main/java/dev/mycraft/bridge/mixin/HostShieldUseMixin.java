package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.BridgeRuntime;
import net.minecraft.client.multiplayer.MultiPlayerGameMode;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The background MC window has no held use key; it must not cancel host-owned use. */
@Mixin(MultiPlayerGameMode.class)
public abstract class HostShieldUseMixin {
    @Inject(method = "releaseUsingItem", at = @At("HEAD"), cancellable = true)
    private void mycraft$keepHostItemUse(Player player, CallbackInfo callback) {
        if (BridgeRuntime.ownsShieldUse(player.getUUID())) callback.cancel();
    }
}
