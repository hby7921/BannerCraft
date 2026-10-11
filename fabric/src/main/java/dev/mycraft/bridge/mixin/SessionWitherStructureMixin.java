package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.LevelReader;
import net.minecraft.world.level.block.WitherSkullBlock;
import net.minecraft.world.level.block.entity.SkullBlockEntity;
import net.minecraft.world.level.block.state.pattern.BlockPattern;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Validate the complete native structure before its original destructive spawn sequence. */
@Mixin(WitherSkullBlock.class)
public abstract class SessionWitherStructureMixin {
    @Inject(method="checkSpawn(Lnet/minecraft/world/level/Level;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/level/block/entity/SkullBlockEntity;)V",at=@At("HEAD"),cancellable=true)
    private static void mycraft$capacity(Level level,BlockPos pos,SkullBlockEntity skull,CallbackInfo callback) {
        if(level instanceof ServerLevel server && NativeWorldGuard.active(level) && !MobRuntime.hasCapacity(server))callback.cancel();
    }
    @WrapOperation(method="checkSpawn(Lnet/minecraft/world/level/Level;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/level/block/entity/SkullBlockEntity;)V",at=@At(value="INVOKE",target="Lnet/minecraft/world/level/block/state/pattern/BlockPattern;find(Lnet/minecraft/world/level/LevelReader;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/block/state/pattern/BlockPattern$BlockPatternMatch;"))
    private static BlockPattern.BlockPatternMatch mycraft$ownedPattern(BlockPattern pattern,LevelReader level,BlockPos pos,
            Operation<BlockPattern.BlockPatternMatch> original) {
        var match=original.call(pattern,level,pos);
        if(match==null || !(level instanceof ServerLevel server) || !NativeWorldGuard.active(server))return match;
        for(int x=0;x<match.getWidth();x++)for(int y=0;y<match.getHeight();y++)for(int z=0;z<match.getDepth();z++) {
            var cell=match.getBlock(x,y,z);
            if(!cell.getState().isAir() && !NativeWorldGuard.ownsBlock(server,cell.getPos()))return null;
        }
        return match;
    }
}
