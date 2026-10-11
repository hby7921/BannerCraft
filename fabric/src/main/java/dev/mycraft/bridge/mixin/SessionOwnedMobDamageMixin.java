package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(LivingEntity.class)
public abstract class SessionOwnedMobDamageMixin {
    @Inject(method = "hurtServer", at = @At("HEAD"), cancellable = true)
    private void mycraft$foreignTarget(ServerLevel level, DamageSource source, float damage, CallbackInfoReturnable<Boolean> callback) {
        if (MobRuntime.owns(source.getEntity()) && !MobRuntime.allowedTarget((LivingEntity)(Object)this)) callback.setReturnValue(false);
    }
}
