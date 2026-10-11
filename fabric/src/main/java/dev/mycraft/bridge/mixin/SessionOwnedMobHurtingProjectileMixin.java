package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.BridgeTargetEntity;
import dev.mycraft.bridge.MobProjectileRuntime;
import dev.mycraft.bridge.MobRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.hurtingprojectile.AbstractHurtingProjectile;
import org.objectweb.asm.Opcodes;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** Native fireballs/skulls must hit the transport body without granting it MC movement authority. */
@Mixin(AbstractHurtingProjectile.class)
public abstract class SessionOwnedMobHurtingProjectileMixin {
    @WrapOperation(method = "canHitEntity", at = @At(value = "FIELD", target =
            "Lnet/minecraft/world/entity/Entity;noPhysics:Z", opcode = Opcodes.GETFIELD))
    private boolean mycraft$hittableHostTransport(Entity target, Operation<Boolean> original) {
        if (MobProjectileRuntime.owns((AbstractHurtingProjectile)(Object)this)
                && target instanceof BridgeTargetEntity actor && MobRuntime.isActor(actor)) return false;
        return original.call(target);
    }
}
