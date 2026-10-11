package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.AABB;

/** Host physics owns overlaps with its mirrored cells, including quantized terrain supports. */
final class BridgePoseSpace {
    static boolean isForeignCollision(BlockGetter world, BlockPos position, BlockState actual,
                                      BlockState visible, BlockState support, AABB target) {
        if(actual.equals(visible) || actual.equals(support))return false;
        for(var local:actual.getCollisionShape(world,position).toAabbs())
            if(local.move(position.getX(),position.getY(),position.getZ()).intersects(target))return true;
        return false;
    }
    private BridgePoseSpace() { }
}
