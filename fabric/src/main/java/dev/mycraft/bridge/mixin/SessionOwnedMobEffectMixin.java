package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.MobProjectileRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Splash and owner-retired projectiles cannot apply damage or status effects to unrelated world entities. */
@Mixin(LivingEntity.class)
public abstract class SessionOwnedMobEffectMixin {
    @Inject(method = "addEffect(Lnet/minecraft/world/effect/MobEffectInstance;Lnet/minecraft/world/entity/Entity;)Z",
            at = @At("HEAD"), cancellable = true)
    private void mycraft$foreignEffect(MobEffectInstance effect, Entity source, CallbackInfoReturnable<Boolean> callback) {
        if ((MobRuntime.owns(source) || MobProjectileRuntime.sourceId(source) != null || MobProjectileRuntime.impactSourceId() != null)
                && !MobRuntime.allowedTarget((LivingEntity)(Object)this)) callback.setReturnValue(false);
    }
    @Inject(method = "hurtServer", at = @At("HEAD"), cancellable = true)
    private void mycraft$foreignProjectileDamage(ServerLevel level, DamageSource source, float damage,
                                                 CallbackInfoReturnable<Boolean> callback) {
        if (MobRuntime.damageSourceId(source) != null && !MobRuntime.allowedTarget((LivingEntity)(Object)this))
            callback.setReturnValue(false);
    }
}
