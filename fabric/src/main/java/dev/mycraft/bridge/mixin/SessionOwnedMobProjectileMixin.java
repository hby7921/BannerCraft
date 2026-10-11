package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.MobProjectileRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.phys.HitResult;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(Projectile.class)
public abstract class SessionOwnedMobProjectileMixin {
    @WrapMethod(method = "onHit")
    private void mycraft$nativeImpactSource(HitResult hit, Operation<Void> original) {
        boolean captured = MobProjectileRuntime.beginImpact((Projectile)(Object)this);
        try { original.call(hit); }
        finally { if (captured) MobProjectileRuntime.endImpact(); }
    }
    @WrapMethod(method = "canHitEntity")
    private boolean mycraft$ownedMobTargets(Entity target, Operation<Boolean> original) {
        if (MobProjectileRuntime.owns((Projectile)(Object)this) && !MobRuntime.allowedTarget(target)) return false;
        return original.call(target);
    }
}
