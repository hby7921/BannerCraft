package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** General vanilla creatures retain session physics without pushing or consuming foreign entities. */
@Mixin(Entity.class)
public abstract class SessionOwnedMobInteractionMixin {
    @Inject(method = "push(Lnet/minecraft/world/entity/Entity;)V", at = @At("HEAD"), cancellable = true)
    private void mycraft$foreignPush(Entity other, CallbackInfo callback) {
        Entity self = (Entity)(Object)this;
        if (MobRuntime.owns(self) && !MobRuntime.allowedTarget(other)
                || MobRuntime.owns(other) && !MobRuntime.allowedTarget(self)) callback.cancel();
    }
    @Inject(method = "remove", at = @At("HEAD"), cancellable = true)
    private void mycraft$foreignConsumption(Entity.RemovalReason reason, CallbackInfo callback) {
        Entity self = (Entity)(Object)this;
        // Foreign ticks have their own inactive scope. Only bridge-scoped removal
        // (such as an owned frog consuming an ambient slime) needs this protection.
        if ((reason == Entity.RemovalReason.KILLED || reason == Entity.RemovalReason.DISCARDED)
                && NativeWorldGuard.active(self.level()) && !MobRuntime.allowedTarget(self)) callback.cancel();
    }
}
