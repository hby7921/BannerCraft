package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.level.Explosion;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayDeque;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.Consumer;

/** Mirrors only genuinely spawned, session-owned vanilla TNT. No replacement fuse or explosion damage formula. */
public final class TntRuntime {
    static final int MAX_TNT = 32;
    static final float HOST_PEAK_DAMAGE = 100;
    // MC 26.3 ExplosionDamageCalculator: full exposure at radius 4 is 7 * (4 * 2) + 1 = 57.
    private static final float VANILLA_PEAK_DAMAGE = 57;
    private static final Map<UUID, PrimedTnt> OWNED = new LinkedHashMap<>();
    private static final ArrayDeque<Blast> BLASTS = new ArrayDeque<>();
    private static ServerLevel level;
    private static String sid = "";
    private static Consumer<JsonObject> reliable = message -> { };
    private static Consumer<JsonObject> states = message -> { };
    private static long ticks;
    private static long lastCapacityLog;
    private static final class Hit {
        float damage;
        Vec3 knockback = Vec3.ZERO;
    }
    private static final class Blast {
        final ServerExplosion explosion;
        final String id = UUID.randomUUID().toString();
        final Map<Integer, Hit> hits = new LinkedHashMap<>();
        Blast(ServerExplosion explosion) { this.explosion = explosion; }
    }

    private TntRuntime() { }
    public static void begin(String session, ServerPlayer owner, Consumer<JsonObject> reliableSink,
                             Consumer<JsonObject> stateSink) {
        clear();
        sid = session; level = owner.level();
        reliable = java.util.Objects.requireNonNull(reliableSink);
        states = java.util.Objects.requireNonNull(stateSink);
    }
    static boolean canAdd(Entity entity) { return !(entity instanceof PrimedTnt) || OWNED.size() < MAX_TNT; }
    public static boolean canPrime(ServerLevel world) {
        if (level != world || OWNED.size() < MAX_TNT && NativeWorldGuard.canSpawnOwnedTnt(world)) return true;
        long now = System.nanoTime();
        if (now - lastCapacityLog >= 1_000_000_000L) {
            lastCapacityLog = now;
            BridgeRuntime.log("TNT native spawn budget reached; original TNT block retained");
        }
        return false;
    }
    static void added(ServerLevel world, Entity entity) {
        if (world != level || sid.isEmpty() || !(entity instanceof PrimedTnt tnt)) return;
        if (OWNED.putIfAbsent(tnt.getUUID(), tnt) == null) {
            publishState(tnt, true);
            BridgeRuntime.log("TNT primed entity=" + tnt.getUUID() + " position=" + tnt.position() + " fuse=" + tnt.getFuse());
        }
    }
    public static void tick() {
        if (level == null) return;
        ticks++;
        var iterator = OWNED.entrySet().iterator();
        while (iterator.hasNext()) {
            PrimedTnt tnt = iterator.next().getValue();
            if (tnt.isRemoved() || tnt.level() != level) { publishState(tnt, false); iterator.remove(); }
            else if ((ticks & 1) == 0) publishState(tnt, true); // Native motion/fuse at 10 Hz; host interpolates.
        }
    }
    private static void publishState(PrimedTnt tnt, boolean alive) {
        JsonObject message = Proto.msg(sid, 0, "tnt_state");
        message.addProperty("entityId", tnt.getUUID().toString());
        message.addProperty("x", tnt.getX()); message.addProperty("y", tnt.getY()); message.addProperty("z", tnt.getZ());
        message.addProperty("fuse", Math.clamp(tnt.getFuse(), 0, 32767)); message.addProperty("alive", alive);
        states.accept(message);
    }
    public static boolean beginExplosion(ServerExplosion explosion) {
        if (!ownsExplosion(explosion)) return false;
        BLASTS.push(new Blast(explosion));
        NativeWorldGuard.enter(level, net.minecraft.core.BlockPos.containing(explosion.center()));
        return true;
    }
    public static boolean ownsExplosion(Explosion explosion) {
        Entity source = explosion.getDirectSourceEntity();
        return level != null && explosion.level() == level && source instanceof PrimedTnt
                && OWNED.containsKey(source.getUUID());
    }
    public static void endExplosion(boolean completed) {
        NativeWorldGuard.exit();
        Blast blast = BLASTS.pop();
        if (!completed) return;
        Vec3 center = blast.explosion.center();
        JsonObject event = Proto.msg(sid, 0, "explosion");
        event.addProperty("eventId", blast.id); event.addProperty("blastId", blast.id);
        event.addProperty("x", center.x); event.addProperty("y", center.y); event.addProperty("z", center.z);
        event.addProperty("radius", blast.explosion.radius());
        reliable.accept(event); // One visual/sound event, always ordered before its captured entity hits.
        for (var entry : blast.hits.entrySet()) {
            Hit hit = entry.getValue();
            if (!(hit.damage > 0) || !Float.isFinite(hit.damage)) continue;
            // Scale only native TNT damage. Vanilla exposure, distance, armor and hurt frames
            // have already been applied; the unrelated weapon conversion setting stays intact.
            float damage = hit.damage * (HOST_PEAK_DAMAGE / VANILLA_PEAK_DAMAGE);
            if (!(damage > 0) || !Float.isFinite(damage)) {
                BridgeRuntime.log("TNT damage conversion rejected for actor=" + entry.getKey());
                continue;
            }
            JsonObject message = Proto.msg(sid, 0, "explosion_hit");
            message.addProperty("eventId", UUID.randomUUID().toString()); message.addProperty("blastId", blast.id);
            message.addProperty("eid", entry.getKey());
            message.addProperty("damage", damage);
            message.addProperty("knockbackX", hit.knockback.x); message.addProperty("knockbackY", hit.knockback.y);
            message.addProperty("knockbackZ", hit.knockback.z);
            reliable.accept(message);
        }
        BridgeRuntime.log("TNT exploded blast=" + blast.id + " position=" + center + " radius="
                + blast.explosion.radius() + " affectedActors=" + blast.hits.size());
    }
    static boolean captures(DamageSource source) {
        Blast blast = BLASTS.peek();
        return blast != null && source == blast.explosion.getDamageSource();
    }
    static void captureDamage(int actorId, DamageSource source, float damage) {
        if (actorId <= 0 || !captures(source) || !(damage > 0) || !Float.isFinite(damage)) return;
        Blast blast = BLASTS.peek();
        if (!blast.hits.containsKey(actorId) && blast.hits.size() >= ActorRegistry.CAPACITY) return;
        blast.hits.computeIfAbsent(actorId, ignored -> new Hit()).damage += damage;
    }
    static void captureKnockback(int actorId, Vec3 motion) {
        Blast blast = BLASTS.peek();
        if (blast == null || actorId <= 0 || !Double.isFinite(motion.x) || !Double.isFinite(motion.y) || !Double.isFinite(motion.z)) return;
        Hit hit = blast.hits.get(actorId);
        if (hit != null) hit.knockback = motion;
    }
    static int ownedCount() { return OWNED.size(); }
    public static void clear() {
        for (PrimedTnt tnt : OWNED.values()) {
            publishState(tnt, false);
            tnt.discard();
        }
        OWNED.clear(); BLASTS.clear(); level = null; sid = ""; ticks = 0; lastCapacityLog = 0;
        reliable = message -> { }; states = message -> { };
    }
}
