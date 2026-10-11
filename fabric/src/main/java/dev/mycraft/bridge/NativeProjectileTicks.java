package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.TicketType;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/** Bounded, transient native simulation tickets along actual session projectile trajectories. */
final class NativeProjectileTicks {
    // A private identity keeps these leases separate from vanilla/other mods; no FLAG_PERSIST.
    private static final TicketType TYPE = new TicketType(40L, TicketType.FLAG_LOADING | TicketType.FLAG_SIMULATION);
    private static final int TICKET_RADIUS = 2; // FULL(33)-2 = ENTITY_TICKING(31).
    private static final int MAX_CENTRES = 32, MAX_FULL_CHUNKS = 96, MAX_PROJECTILES_PER_TICK = 64;
    private static final int LOOKAHEAD_TICKS = 8;
    private static final Map<ChunkPos, Lease> LEASES = new LinkedHashMap<>();
    private static final Map<UUID, Route> ROUTES = new LinkedHashMap<>();
    private static ServerLevel level;
    private static long ticks;
    private static int logBudget;

    private static final class Lease {
        int references;
        CompletableFuture<?> loading;
        long requested;
        Lease(CompletableFuture<?> loading) { this.loading = loading; requested = ticks; }
    }
    private static final class Route {
        final Projectile projectile;
        Vec3 lastPosition;
        int lastNativeTick, stationaryTicks;
        boolean warned;
        Set<ChunkPos> stable = Set.of(), preparing = Set.of();
        Route(Projectile projectile) {
            this.projectile = projectile; lastPosition = projectile.position(); lastNativeTick = projectile.tickCount;
        }
        Set<ChunkPos> held() {
            var held = new LinkedHashSet<>(stable); held.addAll(preparing); return held;
        }
    }
    private NativeProjectileTicks() { }

    static void begin(ServerLevel sessionLevel) { clear(); level = sessionLevel; }
    static void added(Projectile projectile) {
        if (level == null || projectile.level() != level || !WeaponRuntime.ownsProjectile(projectile)) return;
        var route = ROUTES.computeIfAbsent(projectile.getUUID(), id -> new Route(projectile));
        prepare(route); // Submit the first horizon before the entity's first native tick.
    }
    static void tick(Collection<Projectile> projectiles, Vec3 sourcePosition) {
        if (level == null) return;
        ticks++;
        var current = new LinkedHashSet<UUID>();
        for (var projectile : projectiles) current.add(projectile.getUUID());
        for (UUID id : new ArrayList<>(ROUTES.keySet())) if (!current.contains(id)) retire(id);
        var moving = new ArrayList<Route>();
        for (var projectile : projectiles) {
            if (projectile.isRemoved() || projectile.level() != level) { retire(projectile.getUUID()); continue; }
            var route = ROUTES.computeIfAbsent(projectile.getUUID(), id -> new Route(projectile));
            boolean advanced = projectile.tickCount != route.lastNativeTick;
            boolean moved = projectile.position().distanceToSqr(route.lastPosition) > 1.0e-8;
            // A frozen native tick with nonzero velocity is an unloaded boundary, not an embedded arrow.
            if (advanced) route.stationaryTicks = moved ? 0 : route.stationaryTicks + 1;
            route.lastNativeTick = projectile.tickCount;
            route.lastPosition = projectile.position();
            Vec3 velocity = projectile.getDeltaMovement();
            if (route.stationaryTicks >= 2 || !finite(velocity) || velocity.lengthSqr() < 1.0e-8) {
                release(route); continue;
            }
            moving.add(route);
        }
        moving.sort(Comparator.comparingDouble(route -> route.projectile.position().distanceToSqr(sourcePosition)));
        for (int index = 0; index < moving.size(); index++) {
            var route = moving.get(index);
            if (index < MAX_PROJECTILES_PER_TICK) prepare(route);
            else release(route); // Bound work while retaining the genuine native entity/stack.
        }
        for (var entry : LEASES.entrySet()) {
            level.getChunkSource().addTicketWithRadius(TYPE, entry.getKey(), TICKET_RADIUS);
            var lease = entry.getValue();
            if (lease.loading.isCompletedExceptionally() && ticks - lease.requested >= 20) {
                lease.loading = level.getChunkSource().addTicketAndLoadWithRadius(TYPE, entry.getKey(), TICKET_RADIUS);
                lease.requested = ticks;
            }
        }
    }
    private static void prepare(Route route) {
        if (level == null || route.projectile.isRemoved()) return;
        Set<ChunkPos> desired = corridor(route.projectile);
        if (desired.isEmpty()) { release(route); return; }
        if (!route.preparing.equals(desired) && !route.preparing.isEmpty()) {
            // A bounce/loyalty turn may return to the stable footprint while an old forward
            // horizon is still loading. Never promote that obsolete horizon over the current one.
            var obsolete = new LinkedHashSet<>(route.preparing);
            obsolete.removeAll(route.stable); obsolete.removeAll(desired);
            for (ChunkPos pos : obsolete) relinquish(pos);
            var stillDesired = new LinkedHashSet<>(route.preparing); stillDesired.retainAll(desired);
            route.preparing = desired.equals(route.stable) ? Set.of() : stillDesired;
        }
        Set<ChunkPos> held = route.held();
        if (!desired.equals(route.preparing) && !desired.equals(route.stable)) {
            var additional = new LinkedHashSet<>(desired); additional.removeAll(held);
            if (canAcquire(additional)) {
                for (ChunkPos pos : additional) acquire(pos);
                // Keep the previous movement footprint until the replacement horizon is ready.
                var obsolete = new LinkedHashSet<>(route.preparing);
                obsolete.removeAll(route.stable); obsolete.removeAll(desired);
                for (ChunkPos pos : obsolete) relinquish(pos);
                route.preparing = desired;
            } else {
                // Under pressure, acquire the actual current and next chunk, keeping the same hard budgets.
                var minimum = minimumCorridor(route.projectile);
                var minimumAdditional = new LinkedHashSet<>(minimum); minimumAdditional.removeAll(held);
                if (canAcquire(minimumAdditional)) {
                    for (ChunkPos pos : minimumAdditional) acquire(pos);
                    var obsolete = new LinkedHashSet<>(route.preparing);
                    obsolete.removeAll(route.stable); obsolete.removeAll(minimum);
                    for (ChunkPos pos : obsolete) relinquish(pos);
                    route.preparing = minimum;
                }
            }
        }
        if (!route.preparing.isEmpty() && (route.preparing.equals(desired)
                || route.preparing.equals(minimumCorridor(route.projectile))) && ready(route.preparing)) {
            var obsolete = new LinkedHashSet<>(route.stable); obsolete.removeAll(route.preparing);
            for (ChunkPos pos : obsolete) relinquish(pos);
            route.stable = route.preparing;
            route.preparing = Set.of();
        }
        if (!route.warned && !ready(desired)) {
            route.warned = true;
            if (logBudget++ < 16) BridgeRuntime.log("Native projectile horizon preparing id=" + route.projectile.getUUID()
                    + " nativeTick=" + route.projectile.tickCount + " position=" + route.projectile.position()
                    + " velocity=" + route.projectile.getDeltaMovement() + " centres=" + LEASES.size()
                    + " fullChunks=" + fullChunkCount() + "; native flight resumes when chunks are entity-ticking");
        }
    }
    private static Set<ChunkPos> corridor(Projectile projectile) {
        var minimum = minimumCorridor(projectile);
        var corridor = new LinkedHashSet<ChunkPos>();
        for (ChunkPos pos : minimum) for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            corridor.add(new ChunkPos(pos.x() + dx, pos.z() + dz));
        return corridor;
    }
    private static Set<ChunkPos> minimumCorridor(Projectile projectile) {
        var result = new LinkedHashSet<ChunkPos>();
        Vec3 position = projectile.position(), velocity = projectile.getDeltaMovement();
        if (!finite(position) || !finite(velocity) || velocity.lengthSqr() > 1024) return result;
        result.add(ChunkPos.containing(BlockPos.containing(position)));
        // Sweep at most 8 real native velocity steps; substeps cover diagonal/chunk boundaries.
        for (int tick = 0; tick < LOOKAHEAD_TICKS; tick++) {
            Vec3 next = position.add(velocity);
            int steps = Math.max(1, (int) Math.ceil(Math.max(Math.abs(velocity.x), Math.abs(velocity.z)) / 4));
            for (int step = 1; step <= steps; step++)
                result.add(ChunkPos.containing(BlockPos.containing(position.lerp(next, (double) step / steps))));
            position = next;
            // Ticket prediction uses current native velocity only; vanilla keeps all actual flight physics.
        }
        return result;
    }
    private static boolean finite(Vec3 vector) {
        return Double.isFinite(vector.x) && Double.isFinite(vector.y) && Double.isFinite(vector.z);
    }
    private static boolean canAcquire(Set<ChunkPos> additional) {
        var centres = new LinkedHashSet<>(LEASES.keySet()); centres.addAll(additional);
        return centres.size() <= MAX_CENTRES && fullFootprint(centres).size() <= MAX_FULL_CHUNKS;
    }
    private static Set<ChunkPos> fullFootprint(Collection<ChunkPos> centres) {
        var footprint = new LinkedHashSet<ChunkPos>();
        // Level31 propagates through FULL33 at radius2: conservatively count its complete 5×5 halo.
        for (ChunkPos pos : centres) for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++)
            footprint.add(new ChunkPos(pos.x() + dx, pos.z() + dz));
        return footprint;
    }
    private static void acquire(ChunkPos pos) {
        var lease = LEASES.computeIfAbsent(pos, key -> new Lease(
                level.getChunkSource().addTicketAndLoadWithRadius(TYPE, key, TICKET_RADIUS)));
        lease.references++;
    }
    private static boolean ready(Set<ChunkPos> positions) {
        if (positions.isEmpty()) return false;
        for (ChunkPos pos : positions) {
            var lease = LEASES.get(pos);
            if (lease == null || !lease.loading.isDone() || lease.loading.isCompletedExceptionally()
                    || !level.isPositionEntityTicking(new BlockPos(pos.x() * 16, 0, pos.z() * 16))) return false;
        }
        return true;
    }
    private static void relinquish(ChunkPos pos) {
        var lease = LEASES.get(pos);
        if (lease != null && --lease.references == 0) {
            level.getChunkSource().removeTicketWithRadius(TYPE, pos, TICKET_RADIUS);
            LEASES.remove(pos);
        }
    }
    private static void release(Route route) {
        for (ChunkPos pos : route.held()) relinquish(pos);
        route.stable = Set.of(); route.preparing = Set.of();
    }
    static void retire(UUID id) {
        var route = ROUTES.remove(id);
        if (route != null && level != null) release(route);
    }
    static int centreCount() { return LEASES.size(); }
    static int fullChunkCount() { return fullFootprint(LEASES.keySet()).size(); }
    static void clear() {
        if (level != null) for (ChunkPos pos : LEASES.keySet())
            level.getChunkSource().removeTicketWithRadius(TYPE, pos, TICKET_RADIUS);
        LEASES.clear(); ROUTES.clear(); level = null; ticks = 0; logBudget = 0;
    }
}
