package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.InsideBlockEffectApplier;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.BaseFireBlock;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(BaseFireBlock.class)
public abstract class HostLightningFireContactMixin {
    @WrapMethod(method="entityInside")
    private void mycraft$ownedLightningFire(BlockState state,Level level,BlockPos pos,Entity entity,
            InsideBlockEffectApplier effects,boolean moving,Operation<Void> original) {
        if(WeaponRuntime.ownsFire(level,pos)) {
            if(!WeaponRuntime.allowedTarget(entity))return;
            WeaponRuntime.fireContact(entity,pos);
        }
        original.call(state,level,pos,entity,effects,moving);
    }
}
