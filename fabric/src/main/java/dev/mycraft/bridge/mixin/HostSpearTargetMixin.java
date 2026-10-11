package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.item.component.PiercingWeapon;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Native stab/kinetic queries keep their original rules but cannot stab unrelated MC entities. */
@Mixin(PiercingWeapon.class)
public abstract class HostSpearTargetMixin {
    @Inject(method="canHitEntity",at=@At("HEAD"),cancellable=true)
    private static void mycraft$hostTargets(Entity attacker, Entity target, CallbackInfoReturnable<Boolean> callback) {
        if(WeaponRuntime.ownsAttacker(attacker) && !WeaponRuntime.allowedTarget(target)) callback.setReturnValue(false);
    }
}
