package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.TicketType;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/** Session-only vanilla entity simulation. Never manually ticks an entity or forces a permanent chunk. */
final class OwnedEntityTicks {
    private static final TicketType TYPE = new TicketType(40L, TicketType.FLAG_LOADING | TicketType.FLAG_SIMULATION);
    private static final int RADIUS = 2, MAX_CENTRES = 32, MAX_FULL_CHUNKS = 96;
    private static final Map<ChunkPos, Lease> LEASES = new LinkedHashMap<>();
    private static final Map<UUID, Route> ROUTES = new LinkedHashMap<>();
    private static ServerLevel level;
    private static long ticks;
    private static final class Lease {
        int references;
        CompletableFuture<?> future;
        long requested;
        Lease(ChunkPos pos) { future = level.getChunkSource().addTicketAndLoadWithRadius(TYPE, pos, RADIUS); requested = ticks; }
    }
    private static final class Route {
        final Entity entity;
        Set<ChunkPos> stable = Set.of(), preparing = Set.of();
        Route(Entity entity) { this.entity = entity; }
        Set<ChunkPos> held() { var result = new LinkedHashSet<>(stable); result.addAll(preparing); return result; }
    }
    private OwnedEntityTicks() { }
    static void begin(ServerLevel sessionLevel) { if (level != sessionLevel) { clear(); level = sessionLevel; } }
    private static boolean admitted(Entity entity) {
        return NativeWorldGuard.ownsEntity(entity) || entity instanceof BridgeTargetEntity target && MobRuntime.keepsEffectTicking(target);
    }
    static void added(Entity entity) {
        if (entity.level() != level || !admitted(entity)) return;
        prepare(ROUTES.computeIfAbsent(entity.getUUID(), id -> new Route(entity)));
    }
    static void tick() {
        if (level == null) return;
        ticks++;
        for (var route : new ArrayList<>(ROUTES.values())) {
            if (route.entity.isRemoved() || route.entity.level() != level || !admitted(route.entity))
                retire(route.entity.getUUID());
            else prepare(route);
        }
        for (var entry : LEASES.entrySet()) {
            level.getChunkSource().addTicketWithRadius(TYPE, entry.getKey(), RADIUS);
            Lease lease = entry.getValue();
            if (lease.future.isCompletedExceptionally() && ticks - lease.requested >= 20) {
                lease.future = level.getChunkSource().addTicketAndLoadWithRadius(TYPE, entry.getKey(), RADIUS);
                lease.requested = ticks;
            }
        }
    }
    private static Set<ChunkPos> desired(Entity entity) {
        Vec3 pos = entity.position(), motion = entity.getDeltaMovement();
        var result = new LinkedHashSet<ChunkPos>();
        if (!finite(pos) || !finite(motion) || motion.lengthSqr() > 1024) return result;
        result.add(ChunkPos.containing(BlockPos.containing(pos)));
        // An extra centre ahead protects ordinary motion across an entity-ticking boundary.
        // Stationary mobs still need their current centre for native AI and effect ticks.
        result.add(ChunkPos.containing(BlockPos.containing(pos.add(motion.scale(8)))));
        return result;
    }
    private static void prepare(Route route) {
        Set<ChunkPos> wanted = desired(route.entity);
        if (wanted.isEmpty()) return;
        var held = route.held();
        var added = new LinkedHashSet<>(wanted); added.removeAll(held);
        var centres = new LinkedHashSet<>(LEASES.keySet()); centres.addAll(added);
        if (centres.size() > MAX_CENTRES || footprint(centres).size() > MAX_FULL_CHUNKS) return;
        for (ChunkPos pos : added) {
            Lease lease = LEASES.computeIfAbsent(pos, Lease::new); lease.references++;
        }
        var obsoletePending = new LinkedHashSet<>(route.preparing);
        obsoletePending.removeAll(route.stable); obsoletePending.removeAll(wanted);
        for (ChunkPos pos : obsoletePending) relinquish(pos);
        route.preparing = wanted.equals(route.stable) ? Set.of() : wanted;
        if (ready(wanted)) {
            var obsolete = new LinkedHashSet<>(route.stable); obsolete.removeAll(wanted);
            for (ChunkPos pos : obsolete) relinquish(pos);
            route.stable = wanted; route.preparing = Set.of();
        }
    }
    private static boolean ready(Set<ChunkPos> wanted) {
        for (ChunkPos pos : wanted) {
            Lease lease = LEASES.get(pos);
            if (lease == null || !lease.future.isDone() || lease.future.isCompletedExceptionally()
                    || !level.isPositionEntityTicking(new BlockPos(pos.x() * 16, 0, pos.z() * 16))) return false;
        }
        return true;
    }
    private static boolean finite(Vec3 v) { return Double.isFinite(v.x) && Double.isFinite(v.y) && Double.isFinite(v.z); }
    private static Set<ChunkPos> footprint(Set<ChunkPos> centres) {
        var result = new LinkedHashSet<ChunkPos>();
        for (ChunkPos pos : centres) for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++)
            result.add(new ChunkPos(pos.x() + dx, pos.z() + dz));
        return result;
    }
    private static void relinquish(ChunkPos pos) {
        Lease lease = LEASES.get(pos);
        if (lease != null && --lease.references == 0) {
            level.getChunkSource().removeTicketWithRadius(TYPE, pos, RADIUS); LEASES.remove(pos);
        }
    }
    static void retire(UUID id) {
        Route route = ROUTES.remove(id);
        if (route != null && level != null) for (ChunkPos pos : route.held()) relinquish(pos);
    }
    static int centreCount() { return LEASES.size(); }
    static int fullChunkCount() { return footprint(LEASES.keySet()).size(); }
    static void clear() {
        if (level != null) for (ChunkPos pos : LEASES.keySet()) level.getChunkSource().removeTicketWithRadius(TYPE, pos, RADIUS);
        LEASES.clear(); ROUTES.clear(); level = null; ticks = 0;
    }
}
