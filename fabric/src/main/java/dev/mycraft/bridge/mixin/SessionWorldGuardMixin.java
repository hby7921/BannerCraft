package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(Level.class)
public abstract class SessionWorldGuardMixin {
    @WrapMethod(method = "destroyBlock")
    private boolean mycraft$protectDrops(BlockPos pos, boolean drops, Entity source, int recursion, Operation<Boolean> original) {
        if (!NativeWorldGuard.allow((Level)(Object)this, pos)) return false;
        return original.call(pos, drops, source, recursion);
    }
    @WrapMethod(method = "setBlock(Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/level/block/state/BlockState;II)Z")
    private boolean mycraft$guard(BlockPos pos, BlockState state, int flags, int recursion, Operation<Boolean> original) {
        Level level = (Level)(Object)this;
        if (!NativeWorldGuard.active(level)) return original.call(pos, state, flags, recursion);
        if (!NativeWorldGuard.allowMutation(level, pos, state)) return false;
        NativeWorldGuard.beginMutation(pos);
        try { return original.call(pos, state, flags, recursion); }
        finally {
            NativeWorldGuard.changed(level, pos, level.getBlockState(pos));
            NativeWorldGuard.endMutation(pos);
        }
    }
}
