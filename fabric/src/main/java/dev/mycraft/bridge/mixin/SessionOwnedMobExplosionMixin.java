package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.ModifyReturnValue;
import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.llamalad7.mixinextras.sugar.Local;
import dev.mycraft.bridge.HostGroundBlock;
import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.MobProjectileRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.ExplosionDamageCalculator;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.material.FluidState;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import java.util.List;
import java.util.ArrayList;
import java.util.Optional;

@Mixin(ServerExplosion.class)
public abstract class SessionOwnedMobExplosionMixin {
    @WrapMethod(method = "explode")
    private int mycraft$nativeMobBlast(Operation<Integer> original) {
        boolean captured = MobProjectileRuntime.beginExplosion((ServerExplosion)(Object)this);
        boolean completed = false;
        try { int result = original.call(); completed = true; return result; }
        finally { if (captured) MobProjectileRuntime.endExplosion(completed); }
    }
    @WrapOperation(method = "hurtEntities", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/server/level/ServerLevel;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Ljava/util/List;"))
    private List<Entity> mycraft$ownedVictims(ServerLevel level, Entity source, AABB bounds, Operation<List<Entity>> original) {
        List<Entity> targets = original.call(level, source, bounds);
        if (!MobProjectileRuntime.ownsExplosion((ServerExplosion)(Object)this)) return targets;
        return targets.stream().filter(MobRuntime::allowedTarget).toList();
    }
    @ModifyReturnValue(method = "calculateExplodedPositions", at = @At("RETURN"))
    private List<BlockPos> mycraft$ownedBlocks(List<BlockPos> positions) {
        var explosion = (ServerExplosion)(Object)this;
        if (!MobProjectileRuntime.ownsExplosion(explosion)) return positions;
        // Filter before vanilla block loot/chain reactions, not after the block has already dropped items.
        // Vanilla interactWithBlocks shuffles this list in place. Preserve that
        // original mutable contract when removing foreign block candidates.
        return new ArrayList<>(positions.stream().filter(pos -> NativeWorldGuard.allow(explosion.level(), pos)).toList());
    }
    @WrapOperation(method = "calculateExplodedPositions", at = @At(value = "INVOKE", target =
            "Lnet/minecraft/world/level/ExplosionDamageCalculator;getBlockExplosionResistance(Lnet/minecraft/world/level/Explosion;Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/level/block/state/BlockState;Lnet/minecraft/world/level/material/FluidState;)Ljava/util/Optional;"))
    private Optional<Float> mycraft$partialGround(ExplosionDamageCalculator calculator, Explosion explosion,
            BlockGetter world, BlockPos pos, BlockState state, FluidState fluid, Operation<Optional<Float>> original,
            @Local(index = 17) double rayY) {
        if (MobProjectileRuntime.ownsExplosion(explosion) && NativeWorldGuard.ownsSupport(explosion.level(), pos, state)
                && state.hasProperty(HostGroundBlock.HEIGHT) && state.getValue(HostGroundBlock.HEIGHT) < 256
                && fluid.isEmpty() && rayY > pos.getY() + state.getCollisionShape(world, pos).bounds().maxY)
            return Optional.empty();
        return original.call(calculator, explosion, world, pos, state, fluid);
    }
}
