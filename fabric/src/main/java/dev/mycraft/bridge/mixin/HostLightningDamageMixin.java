package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.WeaponRuntime;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LightningBolt;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(Entity.class)
public abstract class HostLightningDamageMixin {
    @WrapMethod(method="thunderHit")
    private void mycraft$captureChanneling(ServerLevel level,LightningBolt bolt,Operation<Void> original) {
        boolean owned=WeaponRuntime.ownsLightning(bolt);
        if(owned && !WeaponRuntime.allowedTarget((Entity)(Object)this))return;
        if(owned)WeaponRuntime.beginLightning(bolt);
        try{original.call(level,bolt);}finally{if(owned)WeaponRuntime.endLightning();}
    }
}
