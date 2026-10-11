package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(FluidState.class)
public abstract class SessionFluidTickMixin {
    @WrapMethod(method = "tick")
    private void mycraft$guardTick(ServerLevel level, BlockPos pos, BlockState state, Operation<Void> original) {
        NativeWorldGuard.enterBlock(level, pos);
        try { original.call(level, pos, state); }
        finally { NativeWorldGuard.exit(); }
    }
    @WrapMethod(method = "randomTick")
    private void mycraft$guardRandom(ServerLevel level, BlockPos pos, RandomSource random, Operation<Void> original) {
        NativeWorldGuard.enterBlock(level, pos);
        try { original.call(level, pos, random); }
        finally { NativeWorldGuard.exit(); }
    }
}
