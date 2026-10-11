package dev.mycraft.bridge.mixin;

import dev.mycraft.bridge.MobTargetSelectorAccess;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.ai.goal.GoalSelector;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(Mob.class)
public abstract class MobTargetSelectorAccessor implements MobTargetSelectorAccess {
    @Accessor("targetSelector") public abstract GoalSelector mycraft$targetSelector();
}
