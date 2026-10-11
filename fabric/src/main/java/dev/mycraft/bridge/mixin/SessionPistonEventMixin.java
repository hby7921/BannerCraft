package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.piston.PistonBaseBlock;
import net.minecraft.world.level.block.piston.PistonStructureResolver;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Piston events run after the input scope has ended; only an exact owned base starts a new scope. */
@Mixin(PistonBaseBlock.class)
public abstract class SessionPistonEventMixin {
    @WrapMethod(method = "neighborChanged")
    private void mycraft$foreignNeighbor(BlockState state, Level level, BlockPos pos,
            net.minecraft.world.level.block.Block source, net.minecraft.world.level.redstone.Orientation orientation,
            boolean moved, Operation<Void> original) {
        if (NativeWorldGuard.active(level) && !NativeWorldGuard.ownsBlock(level, pos)) return;
        original.call(state, level, pos, source, orientation, moved);
    }
    @WrapMethod(method = "triggerEvent")
    private boolean mycraft$ownedEvent(BlockState state, Level level, BlockPos pos, int event, int direction,
                                       Operation<Boolean> original) {
        if (!(level instanceof ServerLevel server)) return original.call(state, level, pos, event, direction);
        if (NativeWorldGuard.active(level) && !NativeWorldGuard.ownsBlock(level, pos)) return false;
        NativeWorldGuard.enterOwnedBlock(server, pos);
        try {
            // Retraction touches/removes the front block before moveBlocks runs. Its actual
            // ownership must be checked before vanilla can remove a foreign block entity.
            if (NativeWorldGuard.active(level) && (event == 1 || event == 2)
                    && !NativeWorldGuard.allow(level, pos.relative(state.getValue(PistonBaseBlock.FACING)))) return false;
            return original.call(state, level, pos, event, direction);
        } finally { NativeWorldGuard.exit(); }
    }

    @Inject(method = "isPushable", at = @At("HEAD"), cancellable = true)
    private static void mycraft$foreignBlocksAreImmovable(BlockState state, Level level, BlockPos pos,
            Direction direction, boolean destroy, Direction pistonDirection, CallbackInfoReturnable<Boolean> callback) {
        if (NativeWorldGuard.active(level) && !NativeWorldGuard.allow(level, pos)) callback.setReturnValue(false);
    }

    @Inject(method = "moveBlocks", at = @At("HEAD"), cancellable = true)
    private void mycraft$validateWholeMotion(Level level, BlockPos base, Direction direction, boolean extending,
                                             CallbackInfoReturnable<Boolean> callback) {
        if (!NativeWorldGuard.active(level)) return;
        var resolver = new PistonStructureResolver(level, base, direction, extending);
        if (!resolver.resolve()) return; // Vanilla retains its own rejection and head cleanup order.
        var writes = new java.util.HashSet<BlockPos>();
        writes.add(base);
        writes.add(base.relative(direction));
        writes.addAll(resolver.getToPush());
        writes.addAll(resolver.getToDestroy());
        for (BlockPos source : resolver.getToPush()) writes.add(source.relative(resolver.getPushDirection()));
        if (!NativeWorldGuard.allowPistonWrites(level, writes)) {
            callback.setReturnValue(false);
        }
    }
}
