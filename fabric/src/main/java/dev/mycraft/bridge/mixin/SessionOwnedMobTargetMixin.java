package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.BridgeTargetEntity;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Vanilla ambient skeletons keep their original targeting; session skeletons cannot acquire unrelated world entities. */
@Mixin(Mob.class)
public abstract class SessionOwnedMobTargetMixin {
    @Inject(method = "canAttack", at = @At("HEAD"), cancellable = true)
    private void mycraft$attackTarget(LivingEntity target, CallbackInfoReturnable<Boolean> callback) {
        boolean owned = MobRuntime.owns((Mob)(Object)this);
        if (owned && !MobRuntime.canTarget((Mob)(Object)this, target) || !owned && target instanceof BridgeTargetEntity)
            callback.setReturnValue(false);
    }
    @Inject(method = "setTarget", at = @At("HEAD"), cancellable = true)
    private void mycraft$setTarget(LivingEntity target, CallbackInfo callback) {
        boolean owned = MobRuntime.owns((Mob)(Object)this);
        if (target != null && (owned && !MobRuntime.canTarget((Mob)(Object)this, target) || !owned && target instanceof BridgeTargetEntity)) callback.cancel();
    }
}
