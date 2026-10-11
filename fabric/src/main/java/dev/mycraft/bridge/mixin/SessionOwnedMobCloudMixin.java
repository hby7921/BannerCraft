package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.MobProjectileRuntime;
import dev.mycraft.bridge.MobRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.AreaEffectCloud;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.List;

/** Lingering original mob potions retain their source and cannot modify foreign HP/effects. */
@Mixin(AreaEffectCloud.class)
public abstract class SessionOwnedMobCloudMixin {
    @WrapMethod(method = "serverTick")
    private void mycraft$nativeCloudSource(ServerLevel level, Operation<Void> original) {
        boolean captured = MobProjectileRuntime.beginCloud((AreaEffectCloud)(Object)this);
        try { original.call(level); }
        finally { if (captured) MobProjectileRuntime.endImpact(); }
    }
    @WrapOperation(method = "serverTick", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/world/level/Level;getEntitiesOfClass(Ljava/lang/Class;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
    private List<LivingEntity> mycraft$originalCloudVictims(Level level, Class<LivingEntity> type, AABB bounds,
                                                           Operation<List<LivingEntity>> original) {
        List<LivingEntity> targets = original.call(level, type, bounds);
        if (!MobProjectileRuntime.owns((AreaEffectCloud)(Object)this)) return targets;
        return targets.stream().filter(MobRuntime::allowedTarget).toList();
    }
}
