package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.server.network.ServerGamePacketListenerImpl;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.network.protocol.game.ServerboundPlayerActionPacket;
import net.minecraft.network.protocol.game.ServerboundInteractPacket;
import net.minecraft.network.protocol.game.ServerboundMovePlayerPacket;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Do not accept a second stream of attacks/mining/drop/swap/release from background MC keys. */
@Mixin(ServerGamePacketListenerImpl.class)
public abstract class HostServerInputMixin {
    @Shadow public ServerPlayer player;
    @Inject(method = "handlePlayerAction", at = @At("HEAD"), cancellable = true)
    private void mycraft$ignoreWindowAction(ServerboundPlayerActionPacket packet, CallbackInfo callback) {
        if (NativeWorldGuard.ownsHostInput(player.getUUID())) callback.cancel();
    }
    @Inject(method = "handleInteract", at = @At("HEAD"), cancellable = true)
    private void mycraft$ignoreWindowAttack(ServerboundInteractPacket packet, CallbackInfo callback) {
        if (NativeWorldGuard.ownsHostInput(player.getUUID())) callback.cancel();
    }
    @Inject(method = "handleMovePlayer", at = @At("HEAD"), cancellable = true)
    private void mycraft$ignoreWindowMovement(ServerboundMovePlayerPacket packet, CallbackInfo callback) {
        // Only read the immutable player UUID and volatile session owner here: HEAD can be on Netty.
        // Host pose updates use server-thread teleport directly; teleport acknowledgments remain untouched.
        if (NativeWorldGuard.ownsHostInput(player.getUUID())) callback.cancel();
    }
}
