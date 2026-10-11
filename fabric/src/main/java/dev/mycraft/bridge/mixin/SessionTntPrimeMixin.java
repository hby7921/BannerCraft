package dev.mycraft.bridge.mixin;

import com.llamalad7.mixinextras.injector.wrapmethod.WrapMethod;
import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import dev.mycraft.bridge.NativeWorldGuard;
import dev.mycraft.bridge.TntRuntime;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.block.TntBlock;
import org.spongepowered.asm.mixin.Mixin;

@Mixin(TntBlock.class)
public abstract class SessionTntPrimeMixin {
    @WrapMethod(method = "prime(Lnet/minecraft/world/level/Level;Lnet/minecraft/core/BlockPos;Lnet/minecraft/world/entity/LivingEntity;Lnet/minecraft/world/item/ItemStack;)Z")
    private static boolean mycraft$prime(Level level, BlockPos pos, LivingEntity owner, ItemStack item, Operation<Boolean> original) {
        if (!(level instanceof ServerLevel server)) return original.call(level, pos, owner, item);
        NativeWorldGuard.enterBlock(server, pos);
        try {
            if (NativeWorldGuard.active(server)
                    && (!NativeWorldGuard.allow(server, pos) || !TntRuntime.canPrime(server))) return false;
            return original.call(level, pos, owner, item);
        } finally { NativeWorldGuard.exit(); }
    }
    @WrapMethod(method = "wasExploded")
    private void mycraft$chain(ServerLevel level, BlockPos pos, Explosion explosion, Operation<Void> original) {
        NativeWorldGuard.enterBlock(level, pos);
        try { original.call(level, pos, explosion); }
        finally { NativeWorldGuard.exit(); }
    }
}
