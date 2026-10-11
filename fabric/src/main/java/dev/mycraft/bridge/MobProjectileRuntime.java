package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.AreaEffectCloud;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.Consumer;

/** Mirrors genuine vanilla mob projectiles; no replacement flight, hit or explosion math. */
public final class MobProjectileRuntime {
    private static final int CAPACITY = 240;
    private record Shot(Projectile projectile, UUID source) { }
    private record Cloud(AreaEffectCloud entity, UUID source) { }
    private record Blast(ServerExplosion explosion, UUID source) { }
    private static final Map<UUID, Shot> SHOTS = new LinkedHashMap<>();
    private static final Map<UUID, Cloud> CLOUDS = new LinkedHashMap<>();
    private static final Map<UUID, Long> SENT = new LinkedHashMap<>();
    private static final Map<UUID, String> VISUALS = new LinkedHashMap<>();
    private static final ThreadLocal<ArrayDeque<Blast>> BLASTS = ThreadLocal.withInitial(ArrayDeque::new);
    private static final ThreadLocal<ArrayDeque<UUID>> IMPACTS = ThreadLocal.withInitial(ArrayDeque::new);
    private static ServerPlayer owner;
    private static String sid = "";
    private static long ticks;
    private static Consumer<JsonObject> reliable = message -> { }, states = message -> { };

    private MobProjectileRuntime() { }
    static void begin(String session, ServerPlayer player, Consumer<JsonObject> reliableSink, Consumer<JsonObject> stateSink) {
        clear(); sid = session; owner = player; reliable = reliableSink; states = stateSink;
    }
    public static boolean isCandidate(Entity entity) {
        if (owner == null || entity.level() != owner.level()) return false;
        return entity instanceof Projectile projectile && MobRuntime.owns(projectile.getOwner())
                || entity instanceof AreaEffectCloud cloud && (MobRuntime.owns(cloud.getOwner()) || impactSourceId() != null);
    }
    public static boolean canAdd(Entity entity) {
        return !isCandidate(entity) || SHOTS.containsKey(entity.getUUID()) || CLOUDS.containsKey(entity.getUUID())
                || SHOTS.size() + CLOUDS.size() < CAPACITY;
    }
    public static boolean owns(Entity entity) {
        Shot shot = entity == null ? null : SHOTS.get(entity.getUUID());
        Cloud cloud = entity == null ? null : CLOUDS.get(entity.getUUID());
        return (shot != null && shot.projectile() == entity || cloud != null && cloud.entity() == entity)
                && NativeWorldGuard.ownsEntity(entity);
    }
    public static UUID sourceId(Entity entity) {
        Shot shot = entity == null ? null : SHOTS.get(entity.getUUID());
        if (shot != null && shot.projectile() == entity) return shot.source();
        Cloud cloud = entity == null ? null : CLOUDS.get(entity.getUUID());
        return cloud != null && cloud.entity() == entity ? cloud.source() : null;
    }
    public static boolean beginImpact(Projectile projectile) {
        Shot shot = SHOTS.get(projectile.getUUID());
        if (shot == null || shot.projectile() != projectile || !owns(projectile)) return false;
        IMPACTS.get().push(shot.source()); return true;
    }
    public static boolean beginCloud(AreaEffectCloud cloud) {
        Cloud tracked = CLOUDS.get(cloud.getUUID());
        if (tracked == null || tracked.entity() != cloud || !owns(cloud)) return false;
        IMPACTS.get().push(tracked.source()); return true;
    }
    public static void endImpact() { IMPACTS.get().pop(); }
    public static UUID impactSourceId() {
        return IMPACTS.get().peek();
    }
    public static void added(ServerLevel level, Entity entity) {
        if (!isCandidate(entity) || !NativeWorldGuard.ownsEntity(entity) || !canAdd(entity)) return;
        if (entity instanceof AreaEffectCloud cloud) {
            UUID source = MobRuntime.owns(cloud.getOwner()) ? cloud.getOwner().getUUID() : impactSourceId();
            if (CLOUDS.putIfAbsent(cloud.getUUID(), new Cloud(cloud, source)) == null) OwnedEntityTicks.added(cloud);
            return;
        }
        var projectile = (Projectile)entity;
        if (SHOTS.putIfAbsent(entity.getUUID(), new Shot(projectile, projectile.getOwner().getUUID())) != null) return;
        OwnedEntityTicks.added(projectile);
        publish(projectile, true);
    }
    static void tick() {
        if (owner == null) return;
        ticks++;
        for (Cloud cloud : new ArrayList<>(CLOUDS.values())) if (cloud.entity().isRemoved() || cloud.entity().level() != owner.level()) {
            CLOUDS.remove(cloud.entity().getUUID()); OwnedEntityTicks.retire(cloud.entity().getUUID());
        }
        int budget = 64;
        var updates = new ArrayList<>(SHOTS.values());
        updates.sort(java.util.Comparator.comparingLong(shot -> SENT.getOrDefault(shot.projectile().getUUID(), Long.MIN_VALUE)));
        for (Shot shot : updates) {
            var projectile = shot.projectile();
            boolean alive = !projectile.isRemoved() && projectile.level() == owner.level();
            if (!alive) {
                publish(projectile, false); SHOTS.remove(projectile.getUUID()); SENT.remove(projectile.getUUID()); VISUALS.remove(projectile.getUUID()); OwnedEntityTicks.retire(projectile.getUUID());
                NativeMobModels.retire(projectile.getUUID().toString());
            } else if (budget-- > 0) publish(projectile, true);
        }
    }
    private static void publish(Projectile projectile, boolean alive) {
        String kind = BuiltInRegistries.ENTITY_TYPE.getKey(projectile.getType()).getPath();
        String visualKey = alive ? NativeMobModels.requestProjectile(sid, projectile.getId(), projectile.getUUID().toString()) : "";
        if (visualKey.matches("[a-f0-9]{32}")) VISUALS.put(projectile.getUUID(), visualKey);
        else visualKey = VISUALS.getOrDefault(projectile.getUUID(), "");
        // A first export may be waiting behind the bounded 48 projectile visual slots.
        // Native flight/damage continue, and a previous genuine export remains usable.
        // Never send an unknown live kind with no native model or manufacture a substitute.
        if (alive && !visualKey.matches("[a-f0-9]{32}")
                && !kind.equals("arrow") && !kind.equals("spectral_arrow") && !kind.equals("trident")) return;
        JsonObject state = Proto.msg(sid, 0, "projectile_state");
        state.addProperty("entityId", projectile.getUUID().toString());
        state.addProperty("kind", kind);
        state.addProperty("x", projectile.getX()); state.addProperty("y", projectile.getY()); state.addProperty("z", projectile.getZ());
        Vec3 velocity = projectile.getDeltaMovement();
        state.addProperty("vx", velocity.x); state.addProperty("vy", velocity.y); state.addProperty("vz", velocity.z);
        state.addProperty("nativeTick", projectile.tickCount); state.addProperty("yaw", projectile.getYRot());
        state.addProperty("pitch", projectile.getXRot()); state.addProperty("alive", alive); state.addProperty("color", -1);
        if (visualKey.matches("[a-f0-9]{32}")) state.addProperty("visualKey", visualKey);
        states.accept(state);
        if (alive) SENT.put(projectile.getUUID(), ticks);
    }
    public static boolean ownsExplosion(Explosion explosion) {
        if (owner == null || explosion.level() != owner.level()) return false;
        Entity direct = explosion.getDirectSourceEntity();
        return MobRuntime.owns(direct) || sourceId(direct) != null;
    }
    public static boolean beginExplosion(ServerExplosion explosion) {
        if (!ownsExplosion(explosion)) return false;
        Entity direct = explosion.getDirectSourceEntity();
        UUID source = MobRuntime.owns(direct) ? direct.getUUID() : sourceId(direct);
        BLASTS.get().push(new Blast(explosion, source));
        NativeWorldGuard.enter(owner.level(), net.minecraft.core.BlockPos.containing(explosion.center()));
        return true;
    }
    public static UUID explosionSourceId(DamageSource source) {
        Blast blast = BLASTS.get().peek();
        return blast != null && blast.explosion().getDamageSource() == source ? blast.source() : null;
    }
    public static void endExplosion(boolean completed) {
        NativeWorldGuard.exit();
        Blast blast = BLASTS.get().pop();
        if (!completed) return;
        JsonObject event = Proto.msg(sid, 0, "explosion");
        String id = UUID.randomUUID().toString();
        event.addProperty("eventId", id); event.addProperty("blastId", id);
        Vec3 position = blast.explosion().center();
        event.addProperty("x", position.x); event.addProperty("y", position.y); event.addProperty("z", position.z);
        event.addProperty("radius", blast.explosion().radius()); reliable.accept(event);
        BridgeRuntime.log("Native mob explosion sourceId=" + blast.source() + " radius=" + blast.explosion().radius());
    }
    static void clear() {
        for (Shot shot : SHOTS.values()) {
            publish(shot.projectile(), false); OwnedEntityTicks.retire(shot.projectile().getUUID());
            NativeMobModels.retire(shot.projectile().getUUID().toString()); shot.projectile().discard();
        }
        for (Cloud cloud : CLOUDS.values()) { OwnedEntityTicks.retire(cloud.entity().getUUID()); cloud.entity().discard(); }
        SHOTS.clear(); CLOUDS.clear(); SENT.clear(); VISUALS.clear(); BLASTS.remove(); IMPACTS.remove(); owner = null; sid = ""; ticks = 0;
        reliable = message -> { }; states = message -> { };
    }
}
