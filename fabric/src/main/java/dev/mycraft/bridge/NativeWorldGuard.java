package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayDeque;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

/** Scoped protection for bridge-owned vanilla mutations; unrelated world work is untouched. */
public final class NativeWorldGuard {
    private record Scope(ServerLevel level, BlockPos origin, boolean active) { }
    private static final ThreadLocal<ArrayDeque<Scope>> SCOPES = ThreadLocal.withInitial(ArrayDeque::new);
    private static final ThreadLocal<java.util.Map<BlockPos, Integer>> MUTATIONS = ThreadLocal.withInitial(java.util.HashMap::new);
    private static final Set<UUID> ENTITIES = new HashSet<>();
    private static final java.util.Map<UUID,Entity> ENTITY_REFS = new java.util.HashMap<>();
    private static final ThreadLocal<Boolean> PLACEMENT_ALLOWED = ThreadLocal.withInitial(()->true);
    private static BuildingRuntime building;
    private static ServerLevel ownedLevel;
    private static volatile UUID hostPlayer;
    private static long observedMutations;

    static void begin(BuildingRuntime runtime, ServerLevel level, UUID player) {
        building = runtime; ownedLevel = level; hostPlayer = player; observedMutations = 0;
    }
    public static boolean ownsHostInput(UUID player) { return player != null && player.equals(hostPlayer); }
    static long observedMutationCount() { return observedMutations; }
    static void preserveEntity(Entity entity) { ENTITIES.remove(entity.getUUID()); ENTITY_REFS.remove(entity.getUUID()); }
    public static boolean ownsEntity(Entity entity) {
        return entity.level() == ownedLevel && (ENTITIES.contains(entity.getUUID()) || entity.getUUID().equals(hostPlayer));
    }
    /** Existing genuine references include live entities awaiting native visible-chunk publication. */
    static java.util.List<Entity> ownedEntitiesSnapshot(){return new java.util.ArrayList<>(ENTITY_REFS.values());}
    public static boolean canSpawnOwnedTnt(ServerLevel level) {
        if(!active(level))return true;
        pruneEntities(level);
        return ENTITIES.size()<1024;
    }
    public static boolean hasOwnedEntityCapacity(ServerLevel level) {
        return hasOwnedLootCapacity(level) && OwnedMobLoot.pendingCount()==0;
    }
    public static boolean hasOwnedEntityCapacity(ServerLevel level,int count) {
        if(count<1 || count>16)return false;
        if(building==null || level!=ownedLevel)return false;
        pruneEntities(level);return ENTITIES.size()+count<=1024 && OwnedMobLoot.pendingCount()==0;
    }
    static boolean hasOwnedLootCapacity(ServerLevel level) {
        if(building==null || level!=ownedLevel)return true;
        pruneEntities(level);return ENTITIES.size()<1024;
    }

    /** Return genuine loot using the existing entity; never rebuild/refund a stack or reset its lifetime/owner. */
    public static void preserveItemDrops(ServerLevel returnLevel, Vec3 returnPosition) {
        if (ownedLevel == null) return;
        if (returnLevel != ownedLevel || returnPosition == null
                || !Double.isFinite(returnPosition.x) || !Double.isFinite(returnPosition.y) || !Double.isFinite(returnPosition.z)) {
            BridgeRuntime.log("Session item drops retained in their original world: return destination is unavailable or differs");
            return;
        }
        int returned = 0;
        var iterator = ENTITIES.iterator();
        while (iterator.hasNext()) {
            UUID id=iterator.next();
            Entity entity = ENTITY_REFS.get(id);
            if(entity==null)entity=ownedLevel.getEntity(id);
            if (!(entity instanceof ItemEntity item) || item.isRemoved() || item.getItem().isEmpty()) continue;
            try {
                // The staging area can be far from the pre-connect position; inspect/load its
                // existing chunk before moving the live entity back into that section.
                returnLevel.getChunkAt(BlockPos.containing(returnPosition));
                item.teleportTo(returnPosition.x, returnPosition.y, returnPosition.z);
                if (!item.isRemoved() && item.level() == returnLevel && item.position().distanceToSqr(returnPosition) < 1.0e-8) {
                    iterator.remove();
                    ENTITY_REFS.remove(id);
                    returned++;
                } else BridgeRuntime.log("Session item drop retained after native return movement failed: " + item.getUUID());
            } catch (RuntimeException failure) {
                // A failed move is never permission to erase a player's real inventory/drop.
                BridgeRuntime.log("Session item drop retained after native return movement error: " + item.getUUID() + " " + failure.getMessage());
            }
        }
        if (returned > 0) BridgeRuntime.log("Returned " + returned + " original item-drop entities to the pre-session position");
    }
    static void clear() {
        building = null;
        hostPlayer = null;
        SCOPES.remove();
        MUTATIONS.remove();
        PLACEMENT_ALLOWED.remove();
        if (ownedLevel != null) for (UUID id : ENTITIES) {
            Entity entity = ENTITY_REFS.get(id);
            if(entity==null)entity=ownedLevel.getEntity(id);
            // Real loot survives even if the return world was unavailable or a native move failed.
            if (entity != null && !(entity instanceof ItemEntity)) entity.discard();
        }
        ENTITIES.clear();
        ENTITY_REFS.clear();
        ownedLevel = null;
    }

    static void enter(ServerLevel level, BlockPos origin) { SCOPES.get().push(new Scope(level, origin.immutable(), true)); }
    public static void enterBlock(ServerLevel level, BlockPos pos) {
        Scope inherited = SCOPES.get().peek();
        if (inherited != null && inherited.active && inherited.level == level) {
            SCOPES.get().push(inherited); return;
        }
        boolean active = building != null && level == ownedLevel && building.ownsNative(pos);
        SCOPES.get().push(new Scope(level, pos.immutable(), active));
    }
    /** Delayed piston work must prove its own origin, rather than inherit a nearby lever's scope. */
    public static boolean ownsBlock(Level level, BlockPos pos) {
        return building != null && level == ownedLevel && building.ownsNative(pos);
    }
    public static void enterOwnedBlock(ServerLevel level, BlockPos pos) {
        SCOPES.get().push(new Scope(level, pos.immutable(), ownsBlock(level, pos)));
    }
    public static void enterEntity(ServerLevel level, Entity entity) {
        SCOPES.get().push(new Scope(level, entity.blockPosition(), building != null && level == ownedLevel
                && ENTITIES.contains(entity.getUUID())));
    }
    public static void exit() { if (!SCOPES.get().isEmpty()) SCOPES.get().pop(); }
    public static boolean active(Level level) {
        Scope scope = SCOPES.get().peek();
        return building != null && scope != null && scope.active && scope.level == level;
    }
    public static boolean isOwnedBlockSpawn(ServerLevel level) {
        Scope scope=SCOPES.get().peek();
        return active(level) && level==ownedLevel && scope!=null && building.ownsVisible(scope.origin)
                && level.getBlockState(scope.origin).is(net.minecraft.world.level.block.Blocks.DISPENSER);
    }
    static boolean setPlacementAllowed(boolean allowed) {
        boolean previous=PLACEMENT_ALLOWED.get();PLACEMENT_ALLOWED.set(allowed);return previous;
    }
    public static boolean placementAllowed(Level level) {return !active(level) || PLACEMENT_ALLOWED.get();}
    public static boolean allow(Level level, BlockPos pos) {
        if (!active(level)) return true;
        Scope scope = SCOPES.get().peek();
        return pos.distSqr(scope.origin) <= 256 && (MUTATIONS.get().containsKey(pos) || building.allowNativeMutation(pos));
    }
    public static boolean allowMutation(Level level, BlockPos pos, BlockState state) {
        if (!active(level)) return true;
        if(WeaponRuntime.lightningActive() && (state.is(net.minecraft.world.level.block.Blocks.FIRE)
                || state.is(net.minecraft.world.level.block.Blocks.SOUL_FIRE)) &&
                !level.getEntitiesOfClass(net.minecraft.world.entity.LivingEntity.class,new net.minecraft.world.phys.AABB(pos),
                    entity->!WeaponRuntime.allowedTarget(entity)).isEmpty())return false;
        Scope scope = SCOPES.get().peek();
        return pos.distSqr(scope.origin) <= 256 && (MUTATIONS.get().containsKey(pos)
                || building.allowNativeMutation(pos, state));
    }
    public static boolean allowPistonWrites(Level level, java.util.Collection<BlockPos> positions) {
        if (!active(level)) return true;
        return building.canReserveNative(positions) && positions.stream().allMatch(pos -> allow(level, pos));
    }
    public static boolean canReplaceHostGround(Level level, BlockPos pos, BlockState state) {
        return active(level) && placementAllowed(level) && building.matchesSupport(pos, state);
    }
    public static boolean ownsSupport(Level level, BlockPos pos, BlockState state) {
        return building != null && level == ownedLevel && building.matchesSupport(pos, state);
    }
    public static void beginMutation(BlockPos pos) {
        pos = pos.immutable();
        MUTATIONS.get().merge(pos, 1, Integer::sum);
        building.reserveNative(pos);
    }
    public static void endMutation(BlockPos pos) {
        MUTATIONS.get().computeIfPresent(pos, (key, count) -> count > 1 ? count - 1 : null);
    }
    public static void changed(Level level, BlockPos pos, BlockState state) {
        if (active(level)) {
            observedMutations++; building.nativeChanged(pos, state);
            WeaponRuntime.fireChanged(level,pos,state,SCOPES.get().peek().origin);
        }
    }
    public static void added(ServerLevel level, Entity entity) {
        if (active(level) && (ENTITIES.size() < 1024 || WeaponRuntime.isOwnedProjectileCandidate(entity))) {
            ENTITIES.add(entity.getUUID());
            ENTITY_REFS.put(entity.getUUID(),entity);
            if (entity instanceof net.minecraft.world.entity.item.PrimedTnt tnt) building.alignPrimedTnt(tnt);
            TntRuntime.added(level, entity);
            WeaponRuntime.added(level, entity);
            MobRuntime.added(level, entity);
            MobProjectileRuntime.added(level, entity);
            BoatRuntime.added(level, entity);
        }
    }
    public static boolean allowEntity(ServerLevel level, Entity entity) {
        if (active(level)) pruneEntities(level);
        if(active(level) && entity instanceof ItemEntity item && ENTITIES.size()>=1024 && OwnedMobLoot.active()){
            OwnedMobLoot.defer(item);return false;
        }
        return !active(level) || ENTITIES.contains(entity.getUUID()) || WeaponRuntime.isOwnedProjectileCandidate(entity)
                || ENTITIES.size() < 1024 && TntRuntime.canAdd(entity)
                    && MobRuntime.canAdd(entity) && BoatRuntime.canAdd(entity) && MobProjectileRuntime.canAdd(entity);
    }
    private static void pruneEntities(ServerLevel level) {
        ENTITIES.removeIf(id->{
            Entity actual=ENTITY_REFS.get(id);
            if(actual==null)actual=level.getEntity(id);
            // The native visible lookup omits a live entity while its chunk is
            // preparing. Only actual removal/dimension change retires ownership.
            boolean removed=actual!=null && (actual.isRemoved() || actual.level()!=level);
            if(removed)ENTITY_REFS.remove(id);
            return removed;
        });
    }
}
