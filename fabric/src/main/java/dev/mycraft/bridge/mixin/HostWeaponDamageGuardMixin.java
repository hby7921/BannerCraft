package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Firework explosions and native automatic attacks must keep foreign living entities untouched. */
@Mixin(LivingEntity.class)
public abstract class HostWeaponDamageGuardMixin {
    @Inject(method = "hurtServer", at = @At("HEAD"), cancellable = true)
    private void mycraft$protectForeignEntity(ServerLevel level, DamageSource source, float damage,
                                              CallbackInfoReturnable<Boolean> callback) {
        if ((source.getDirectEntity() != null && WeaponRuntime.ownsProjectile(source.getDirectEntity())
                || WeaponRuntime.ownsAttacker(source.getEntity()))
                && !WeaponRuntime.allowedTarget((LivingEntity)(Object)this)) callback.setReturnValue(false);
    }
}
