package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LightningBolt;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.List;
import java.util.function.Predicate;

@Mixin(LightningBolt.class)
public abstract class HostChannelingLightningMixin {
    @WrapMethod(method="tick")
    private void mycraft$lightningScope(Operation<Void> original) {
        var bolt=(LightningBolt)(Object)this;boolean owned=WeaponRuntime.ownsLightning(bolt);
        if(owned)WeaponRuntime.observeLightning(bolt);
        if(owned)WeaponRuntime.beginLightning(bolt);
        try{original.call();}finally{if(owned)WeaponRuntime.endLightning();}
    }
    @WrapOperation(method="tick",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/Level;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;Ljava/util/function/Predicate;)Ljava/util/List;"))
    private List<Entity> mycraft$ownedTargets(Level level,Entity except,AABB bounds,Predicate<Entity> originalPredicate,
                                              Operation<List<Entity>> original) {
        if(WeaponRuntime.ownsLightning((LightningBolt)(Object)this)) originalPredicate=originalPredicate.and(WeaponRuntime::allowedTarget);
        return original.call(level,except,bounds,originalPredicate);
    }
}
