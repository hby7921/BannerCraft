package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.LightningStateAccess;
import net.minecraft.world.entity.LightningBolt;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(LightningBolt.class)
public interface LightningStateAccessor extends LightningStateAccess {
    @Accessor("life") int mycraft$lightningLife();
}
