package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.level.ServerPlayerGameMode;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Guards the authoritative entry point too, including stale client packets after a GUI closes. */
@Mixin(ServerPlayerGameMode.class)
public abstract class HostServerUseMixin {
    @Inject(method = "useItemOn", at = @At("HEAD"), cancellable = true)
    private void mycraft$hostUseOn(ServerPlayer player, Level level, ItemStack stack, InteractionHand hand,
                                   BlockHitResult hit, CallbackInfoReturnable<InteractionResult> callback) {
        if (NativeWorldGuard.ownsHostInput(player.getUUID()) && !NativeWorldGuard.active(level))
            callback.setReturnValue(InteractionResult.FAIL);
    }
    @Inject(method = "useItem", at = @At("HEAD"), cancellable = true)
    private void mycraft$hostUse(ServerPlayer player, Level level, ItemStack stack, InteractionHand hand,
                                 CallbackInfoReturnable<InteractionResult> callback) {
        if (NativeWorldGuard.ownsHostInput(player.getUUID()) && !NativeWorldGuard.active(level))
            callback.setReturnValue(InteractionResult.FAIL);
    }
}
