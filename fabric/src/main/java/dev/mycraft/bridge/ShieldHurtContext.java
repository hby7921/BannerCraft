package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.component.DataComponents;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.component.BlocksAttacks;
import net.minecraft.world.phys.Vec3;

import java.util.Objects;
import java.util.Optional;

/** Immutable hit-time coordinates. The caller applies the pose immediately before native hurtServer. */
final class ShieldHurtContext {
    record Pose(double x, double y, double z, float yaw, float pitch) { }
    record Diagnostic(boolean usingItem, boolean blockingReady, int ticksUsingItem, int blockDelayTicks,
                      float headYaw, double horizontalAngleRadians, float expectedBlocked,
                      Optional<Vec3> sourcePosition, String reason) { }

    private final Optional<Pose> pose;
    private final Optional<Vec3> sourcePosition;

    private ShieldHurtContext(Optional<Pose> pose, Optional<Vec3> sourcePosition) {
        this.pose = pose;
        this.sourcePosition = sourcePosition;
    }

    Optional<Pose> pose() { return pose; }
    Optional<Vec3> sourcePosition() { return sourcePosition; }

    static ShieldHurtContext read(JsonObject message) {
        String[] poseFields = {"x", "y", "z", "yaw", "pitch"};
        int presentPose = count(message, poseFields);
        Optional<Pose> pose = Optional.empty();
        if (presentPose != 0) {
            if (presentPose != poseFields.length) throw new IllegalArgumentException("hurt pose requires x/y/z/yaw/pitch together");
            Vec3 position = position(message, "x", "y", "z");
            float yaw = (float)Proto.number(message, "yaw");
            if (!Float.isFinite(yaw)) throw new IllegalArgumentException("hurt yaw outside finite float range");
            pose = Optional.of(new Pose(position.x, position.y, position.z, yaw, Proto.pitch(message)));
        }

        String[] sourceFields = {"sourceX", "sourceY", "sourceZ"};
        int presentSource = count(message, sourceFields);
        boolean hasSource = message.has("hasSourcePosition") && Proto.bool(message, "hasSourcePosition");
        if (hasSource && presentSource != sourceFields.length)
            throw new IllegalArgumentException("source position requires sourceX/sourceY/sourceZ together");
        if (!hasSource && presentSource != 0)
            throw new IllegalArgumentException("source coordinates require hasSourcePosition=true");
        Optional<Vec3> source = hasSource ? Optional.of(position(message, sourceFields[0], sourceFields[1], sourceFields[2]))
                : Optional.empty();
        return new ShieldHurtContext(pose, source);
    }

    private static int count(JsonObject message, String[] fields) {
        int count = 0;
        for (String field : fields) if (message.has(field)) count++;
        return count;
    }

    private static Vec3 position(JsonObject message, String xKey, String yKey, String zKey) {
        double x = Proto.number(message, xKey), y = Proto.number(message, yKey), z = Proto.number(message, zKey);
        if (Math.abs(x) > 29_999_984 || Math.abs(z) > 29_999_984 || y < -2048 || y > 2048)
            throw new IllegalArgumentException("hurt position outside bridge bounds");
        return new Vec3(x, y, z);
    }

    DamageSource withSourcePosition(DamageSource base) {
        Objects.requireNonNull(base, "base damage source");
        return sourcePosition.map(position -> withSourcePosition(base, position)).orElse(base);
    }

    /** Keep proxy attribution, damage tags and weapon lookup while freezing the actual hit source position. */
    static DamageSource withSourcePosition(DamageSource base, Vec3 position) {
        Objects.requireNonNull(base, "base damage source");
        Objects.requireNonNull(position, "source position");
        if (!Double.isFinite(position.x) || !Double.isFinite(position.y) || !Double.isFinite(position.z))
            throw new IllegalArgumentException("finite source position required");
        return new DamageSource(base.typeHolder(), base.getDirectEntity(), base.getEntity()) {
            @Override public Vec3 getSourcePosition() { return position; }
            @Override public Vec3 sourcePositionRaw() { return position; }
        };
    }

    /** Same direction preparation used by MC26.3 LivingEntity.applyItemBlocking; damage math stays native. */
    static double horizontalAngle(Vec3 victimPosition, float headYaw, Vec3 sourcePosition) {
        if (sourcePosition == null) return (double)(float)Math.PI;
        Vec3 direction = sourcePosition.subtract(victimPosition);
        direction = new Vec3(direction.x, 0, direction.z).normalize();
        return Math.acos(direction.dot(Entity.calculateViewVector(0, headYaw)));
    }

    /** Read-only prediction: never calls applyItemBlocking, damages the shield, or triggers a sound/cooldown. */
    static Diagnostic diagnose(LivingEntity victim, DamageSource source, float damage) {
        boolean using = victim.isUsingItem();
        ItemStack useItem = victim.getUseItem();
        BlocksAttacks attacks = useItem.get(DataComponents.BLOCKS_ATTACKS);
        int ticksUsing = using ? victim.getTicksUsingItem() : 0;
        int delay = attacks == null ? 0 : attacks.blockDelayTicks();
        float yaw = victim.getYHeadRot();
        Vec3 position = source.getSourcePosition();
        double angle = horizontalAngle(victim.position(), yaw, position);
        boolean ready = victim.getItemBlockingWith() != null;
        String reason;
        float blocked = 0;
        if (!(damage > 0) || !Float.isFinite(damage)) reason = "nonpositive_damage";
        else if (!using) reason = "not_using_item";
        else if (attacks == null) reason = "item_does_not_block";
        else if (!ready) reason = "native_block_delay";
        else if (attacks.bypassedBy().map(types -> types.contains(source.typeHolder())).orElse(false)) reason = "damage_type_bypasses";
        else if (source.getDirectEntity() instanceof AbstractArrow arrow && arrow.getPierceLevel() > 0) reason = "piercing_arrow";
        else {
            blocked = attacks.resolveBlockedDamage(source, damage, angle);
            reason = position == null ? "missing_source_position" : blocked > 0 ? "native_block" : "outside_angle_or_damage_filter";
        }
        return new Diagnostic(using, ready, ticksUsing, delay, yaw, angle, blocked, Optional.ofNullable(position), reason);
    }
}
