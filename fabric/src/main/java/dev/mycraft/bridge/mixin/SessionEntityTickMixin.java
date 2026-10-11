package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(ServerLevel.class)
public abstract class SessionEntityTickMixin {
    @WrapMethod(method = "tickNonPassenger")
    private void mycraft$guardTick(Entity entity, Operation<Void> original) {
        NativeWorldGuard.enterEntity((ServerLevel)(Object)this, entity);
        try { original.call(entity); }
        finally { NativeWorldGuard.exit(); }
    }
    @WrapMethod(method = "addFreshEntity")
    private boolean mycraft$added(Entity entity, Operation<Boolean> original) {
        ServerLevel level = (ServerLevel)(Object)this;
        if (!NativeWorldGuard.allowEntity(level, entity)) return false;
        boolean added = original.call(entity);
        if (added) NativeWorldGuard.added(level, entity);
        return added;
    }
}
