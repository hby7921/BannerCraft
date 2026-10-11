package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.BridgeTargetEntity;
import dev.mycraft.bridge.NativeWorldGuard;
import dev.mycraft.bridge.TntRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.List;

@Mixin(ServerExplosion.class)
public abstract class SessionTntExplosionMixin {
    @WrapOperation(method = "hurtEntities", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/server/level/ServerLevel;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
    private List<Entity> mycraft$ownedBlastTargets(ServerLevel level, Entity source, AABB bounds,
                                                  Operation<List<Entity>> original) {
        List<Entity> nativeTargets = original.call(level, source, bounds);
        if (!TntRuntime.ownsExplosion((ServerExplosion)(Object)this)) return nativeTargets;
        // Filtering before native damage AND pushFromExplosion protects foreign HP and motion.
        // Original distance/exposure/knockback calculations still run for every allowed target.
        return nativeTargets.stream().filter(target -> target instanceof BridgeTargetEntity
                || NativeWorldGuard.ownsEntity(target)).toList();
    }
    @WrapMethod(method = "explode")
    private int mycraft$nativeTntExplosion(Operation<Integer> original) {
        boolean captured = TntRuntime.beginExplosion((ServerExplosion)(Object)this);
        boolean completed = false;
        try {
            int result = original.call(); completed = true; return result;
        } finally { if (captured) TntRuntime.endExplosion(completed); }
    }
}
