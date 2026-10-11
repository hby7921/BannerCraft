package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import dev.mycraft.bridge.OwnedMobLoot;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(LivingEntity.class)
public abstract class SessionOwnedMobLootMixin {
    @WrapMethod(method="dropAllDeathLoot")
    private void mycraft$actualOwnedLoot(ServerLevel level,DamageSource source,Operation<Void> original){
        var entity=(LivingEntity)(Object)this;
        if(!MobRuntime.owns(entity)){original.call(level,source);return;}
        NativeWorldGuard.enterEntity(level,entity);OwnedMobLoot.enter();
        try{original.call(level,source);}
        finally{OwnedMobLoot.exit();NativeWorldGuard.exit();}
    }
}
