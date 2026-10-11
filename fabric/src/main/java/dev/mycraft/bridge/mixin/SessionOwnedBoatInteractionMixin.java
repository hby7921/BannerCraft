package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.mycraft.bridge.BoatRuntime;
import dev.mycraft.bridge.NativeWorldGuard;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.vehicle.boat.AbstractBoat;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.AABB;
import java.util.List;
import java.util.function.Predicate;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Vanilla pickup/pushing stays intact within a session; unrelated world passengers and motion are untouched. */
@Mixin(AbstractBoat.class)
public abstract class SessionOwnedBoatInteractionMixin {
    @WrapOperation(method = "tick", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getEntities(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;Ljava/util/function/Predicate;)Ljava/util/List;"))
    private List<Entity> mycraft$ownedPassengers(Level level, Entity excluded, AABB box, Predicate<Entity> originalPredicate,
                                                Operation<List<Entity>> original) {
        if (!BoatRuntime.owns((AbstractBoat)(Object)this)) return original.call(level, excluded, box, originalPredicate);
        return original.call(level, excluded, box, originalPredicate.and(NativeWorldGuard::ownsEntity));
    }
    @Inject(method = "push", at = @At("HEAD"), cancellable = true)
    private void mycraft$separateWorldMotion(Entity other, CallbackInfo callback) {
        boolean ownedBoat = BoatRuntime.owns((AbstractBoat)(Object)this);
        boolean ownedOther = NativeWorldGuard.ownsEntity(other);
        if (ownedBoat != ownedOther) callback.cancel();
    }
}
