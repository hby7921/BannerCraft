package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import dev.mycraft.bridge.TntRuntime;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.block.state.BlockBehaviour;
import net.minecraft.world.item.ItemStack;
import java.util.function.BiConsumer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(BlockBehaviour.BlockStateBase.class)
public abstract class SessionBlockTickMixin {
    @WrapMethod(method = {"tick", "randomTick"})
    private void mycraft$guardTick(ServerLevel level, BlockPos pos, RandomSource random, Operation<Void> original) {
        NativeWorldGuard.enterBlock(level, pos);
        try { original.call(level, pos, random); }
        finally { NativeWorldGuard.exit(); }
    }
    @WrapMethod(method = "onExplosionHit")
    private void mycraft$protectExplosion(ServerLevel level, BlockPos pos, Explosion explosion,
                                           BiConsumer<ItemStack, BlockPos> drops, Operation<Void> original) {
        NativeWorldGuard.enterBlock(level, pos);
        try {
            if (!NativeWorldGuard.allow(level, pos)) return;
            // Vanilla clears the block BEFORE TntBlock.wasExploded. Reject here, before
            // that irreversible step, so capacity cannot silently swallow a chained TNT.
            if (NativeWorldGuard.active(level) && level.getBlockState(pos).is(net.minecraft.world.level.block.Blocks.TNT)
                    && !TntRuntime.canPrime(level)) return;
            original.call(level, pos, explosion, drops);
        } finally { NativeWorldGuard.exit(); }
    }
}
