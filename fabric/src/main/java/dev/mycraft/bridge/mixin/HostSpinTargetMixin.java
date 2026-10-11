package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.List;

@Mixin(LivingEntity.class)
public abstract class HostSpinTargetMixin {
    @WrapOperation(method="checkAutoSpinAttack",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/Level;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
    private List<Entity> mycraft$hostSpinTargets(Level level,Entity except,AABB bounds,Operation<List<Entity>> original) {
        List<Entity> entities=original.call(level,except,bounds);
        if(WeaponRuntime.ownsAttacker((Entity)(Object)this))return entities.stream().filter(WeaponRuntime::allowedTarget).toList();
        return entities;
    }
}
