package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.item.ItemEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Preserve vanilla equipment pickup within the session's actual item provenance. */
@Mixin(Mob.class)
public abstract class SessionOwnedMobPickupMixin {
    @Inject(method = "pickUpItem", at = @At("HEAD"), cancellable = true)
    private void mycraft$ownedItems(ServerLevel level, ItemEntity item, CallbackInfo callback) {
        if (MobRuntime.owns((Mob)(Object)this) && !NativeWorldGuard.ownsEntity(item)) callback.cancel();
    }
}
