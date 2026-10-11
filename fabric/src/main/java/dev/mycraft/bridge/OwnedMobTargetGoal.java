package dev.mycraft.bridge;

import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.monster.spider.Spider;
import net.minecraft.world.entity.ai.goal.target.NearestAttackableTargetGoal;

/** Retains vanilla range, pathfinding and line-of-sight; only the invisible host proxy is exempt from invisibility. */
final class OwnedMobTargetGoal extends NearestAttackableTargetGoal<LivingEntity> {
    private final Mob skeleton;
    OwnedMobTargetGoal(Mob skeleton) {
        super(skeleton, LivingEntity.class, 10, true, false,
                (entity, level) -> (entity instanceof BridgeTargetEntity target && MobRuntime.isActor(target)
                        || MobRuntime.isOwnerPlayer(entity)) && MobRuntime.canTarget(skeleton, entity));
        this.skeleton = skeleton;
        // The visibility mixin exempts only invisible transport proxies; the real
        // MC player's invisibility, original LOS and nearest-target distance stay native.
    }
    @Override public boolean canUse() {
        // Spider's original SpiderTargetGoal refuses initial aggression in daylight.
        return MobRuntime.owns(skeleton) && (!(skeleton instanceof Spider)
                || skeleton.getLightLevelDependentMagicValue() < .5F) && super.canUse();
    }
    @Override public boolean canContinueToUse() {
        return MobRuntime.owns(skeleton) && (skeleton.getTarget() instanceof BridgeTargetEntity target && MobRuntime.isActor(target)
                || MobRuntime.isOwnerPlayer(skeleton.getTarget())) && MobRuntime.canTarget(skeleton, skeleton.getTarget())
                && super.canContinueToUse();
    }
}
