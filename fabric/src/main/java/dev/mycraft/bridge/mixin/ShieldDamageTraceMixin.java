package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.ShieldDamageTrace;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.server.level.ServerLevel;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(LivingEntity.class)
public abstract class ShieldDamageTraceMixin {
    @Inject(method="applyItemBlocking",at=@At("RETURN"))
    private void mycraft$observeBlock(ServerLevel level,DamageSource source,float damage,CallbackInfoReturnable<Float> result) {
        ShieldDamageTrace.record((LivingEntity)(Object)this,result.getReturnValueF());
    }
}
