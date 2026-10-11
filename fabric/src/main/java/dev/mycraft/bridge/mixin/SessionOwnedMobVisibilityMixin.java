package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.BridgeTargetEntity;
import dev.mycraft.bridge.MobRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.ai.targeting.TargetingConditions;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** Only transport invisibility is ignored; the original selector, distance and LOS still run. */
@Mixin(TargetingConditions.class)
public abstract class SessionOwnedMobVisibilityMixin {
    @WrapOperation(method = "test", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/world/entity/LivingEntity;getVisibilityPercent(Lnet/minecraft/server/level/ServerLevel;Lnet/minecraft/world/entity/Entity;)D"))
    private double mycraft$hostProxyVisibility(LivingEntity target, ServerLevel level, Entity source, Operation<Double> original) {
        if (MobRuntime.owns(source) && target instanceof BridgeTargetEntity actor && MobRuntime.isActor(actor)) return 1;
        return original.call(target, level, source);
    }
}
