package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.piston.PistonMovingBlockEntity;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The native moving block's delayed final state retains the scope established by the owned event. */
@Mixin(PistonMovingBlockEntity.class)
public abstract class SessionPistonMovingMixin {
    @WrapOperation(method="moveCollidedEntities",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/Level;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
    private static java.util.List<net.minecraft.world.entity.Entity> mycraft$separateSlimeMotion(Level level,net.minecraft.world.entity.Entity excluded,
            net.minecraft.world.phys.AABB box,Operation<java.util.List<net.minecraft.world.entity.Entity>> original) {
        var entities=original.call(level,excluded,box);
        if(!NativeWorldGuard.active(level))return entities;
        return entities.stream().filter(NativeWorldGuard::ownsEntity).toList();
    }
    @WrapOperation(method="moveStuckEntities",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/Level;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;Ljava/util/function/Predicate;)Ljava/util/List;"))
    private static java.util.List<net.minecraft.world.entity.Entity> mycraft$separateHoneyMotion(Level level,net.minecraft.world.entity.Entity excluded,
            net.minecraft.world.phys.AABB box,java.util.function.Predicate<net.minecraft.world.entity.Entity> predicate,
            Operation<java.util.List<net.minecraft.world.entity.Entity>> original) {
        return original.call(level,excluded,box,NativeWorldGuard.active(level)?predicate.and(NativeWorldGuard::ownsEntity):predicate);
    }
    @Inject(method = "moveEntityByPiston", at = @At("HEAD"), cancellable = true)
    private static void mycraft$foreignEntities(Direction movementDirection, net.minecraft.world.entity.Entity entity,
            double distance, Direction pistonDirection, CallbackInfo callback) {
        if (NativeWorldGuard.active(entity.level()) && !NativeWorldGuard.ownsEntity(entity)) callback.cancel();
    }
    @WrapMethod(method = "tick")
    private static void mycraft$ownedTick(Level level, BlockPos pos, BlockState state, PistonMovingBlockEntity entity,
                                          Operation<Void> original) {
        if (!(level instanceof ServerLevel server)) { original.call(level, pos, state, entity); return; }
        if (NativeWorldGuard.active(level) && !NativeWorldGuard.ownsBlock(level, pos)) return;
        NativeWorldGuard.enterOwnedBlock(server, pos);
        try { original.call(level, pos, state, entity); }
        finally { NativeWorldGuard.exit(); }
    }

    @WrapMethod(method = "finalTick")
    private void mycraft$ownedFinish(Operation<Void> original) {
        var entity = (PistonMovingBlockEntity)(Object)this;
        if (!(entity.getLevel() instanceof ServerLevel server)) { original.call(); return; }
        if (NativeWorldGuard.active(server) && !NativeWorldGuard.ownsBlock(server, entity.getBlockPos())) return;
        NativeWorldGuard.enterOwnedBlock(server, entity.getBlockPos());
        try { original.call(); }
        finally { NativeWorldGuard.exit(); }
    }
}
