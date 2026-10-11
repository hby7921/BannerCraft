package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.block.RenderShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Check placement only after the clicked block has had its vanilla interaction opportunity. */
@Mixin(BlockItem.class)
public abstract class SessionBlockPlacementMixin {
    @Inject(method="place",at=@At("HEAD"),cancellable=true)
    private void mycraft$hostPlacement(BlockPlaceContext context,CallbackInfoReturnable<InteractionResult> callback) {
        if(!NativeWorldGuard.active(context.getLevel()))return;
        BlockItem item=(BlockItem)(Object)this;
        if(!NativeWorldGuard.placementAllowed(context.getLevel()) || item.getBlock().defaultBlockState().getRenderShape()!=RenderShape.MODEL)
            callback.setReturnValue(InteractionResult.FAIL);
    }
}
