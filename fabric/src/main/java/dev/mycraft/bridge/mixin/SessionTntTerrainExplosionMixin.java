package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.llamalad7.mixinextras.sugar.Local;
import dev.mycraft.bridge.HostGroundBlock;
import dev.mycraft.bridge.NativeWorldGuard;
import dev.mycraft.bridge.TntRuntime;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.ExplosionDamageCalculator;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.Optional;

/** Let native explosion rays cross the air above our partial terrain's actual collision surface. */
@Mixin(ServerExplosion.class)
public abstract class SessionTntTerrainExplosionMixin {
    @WrapOperation(method = "calculateExplodedPositions", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/world/level/ExplosionDamageCalculator;getBlockExplosionResistance(Lnet/minecraft/world/level/Explosion;Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/level/block/state/BlockState;Lnet/minecraft/world/level/material/FluidState;)Ljava/util/Optional;"))
    private Optional<Float> mycraft$partialTerrain(ExplosionDamageCalculator calculator, Explosion explosion,
                                                  BlockGetter world, BlockPos pos, BlockState state, FluidState fluid,
                                                  Operation<Optional<Float>> original, @Local(index = 17) double rayY) {
        // Slot 17 is the native ray sample Y in the pinned MC 26.3 bytecode, not its origin Y.
        // A partial support otherwise acts like a full bedrock voxel and extinguishes every
        // ray from embedded TNT before vanilla can select/prime neighboring TNT blocks.
        if (TntRuntime.ownsExplosion(explosion) && NativeWorldGuard.ownsSupport(explosion.level(), pos, state)
                && state.hasProperty(HostGroundBlock.HEIGHT) && state.getValue(HostGroundBlock.HEIGHT) < 256
                && fluid.isEmpty() && rayY > pos.getY() + state.getCollisionShape(world, pos).bounds().maxY)
            return Optional.empty();
        return original.call(calculator, explosion, world, pos, state, fluid);
    }
}
