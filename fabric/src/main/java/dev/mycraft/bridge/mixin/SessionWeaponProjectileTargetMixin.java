package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.Projectile;
import org.spongepowered.asm.mixin.Mixin;

/** Retain vanilla hit eligibility while keeping bridge-fired projectiles off foreign world entities. */
@Mixin(Projectile.class)
public abstract class SessionWeaponProjectileTargetMixin {
    @WrapMethod(method = "canHitEntity")
    private boolean mycraft$ownedTarget(Entity target, Operation<Boolean> original) {
        if (WeaponRuntime.ownsProjectile((Projectile)(Object)this)
                && !WeaponRuntime.allowedTarget(target)) return false;
        return original.call(target);
    }
}
