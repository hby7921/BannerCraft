package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Temporary world ownership and real inventory mutations, only on the MC server thread. */
final class BuildingRuntime {
    private static final EquipmentSlot[] ARMOR = {EquipmentSlot.FEET, EquipmentSlot.LEGS,
            EquipmentSlot.CHEST, EquipmentSlot.HEAD};
    private static final int REQUEST_HISTORY = 4096;
    private static final int MAX_SUPPORTS = 4096;
    private final Map<BlockPos, PlacedBlock> placed = new LinkedHashMap<>();
    private final Map<BlockPos, BlockState> supports = new LinkedHashMap<>();
    private final GroundSupportLeases groundLeases=new GroundSupportLeases(MAX_SUPPORTS);
    private long nextGroundCleanup;
    private record PendingGround(net.minecraft.world.entity.Entity entity,JsonObject message,long since) { }
    private final Map<java.util.UUID,PendingGround> pendingEntityGround=new LinkedHashMap<>();
    private static final class GroundCapacityDeferred extends IllegalArgumentException {
        GroundCapacityDeferred(){super("session ground budget reached");}
    }
    private final java.util.Set<BlockPos> pendingSupportRestores = new java.util.LinkedHashSet<>();
    private final java.util.Set<BlockPos> dirty = new java.util.LinkedHashSet<>();
    private final java.util.Set<BlockPos> nativeOwned = new java.util.HashSet<>();
    private final RequestHistory<Outcome> requests = new RequestHistory<>(REQUEST_HISTORY);
    private ServerPlayer owner;
    private ServerLevel level;
    private String sid = "";
    private int revision;
    private List<ItemStack> previousStacks;
    private InventoryState snapshot;
    private record PlacedBlock(BlockState state, ItemStack debit) { }
    private record Outcome(String type, boolean ok, String message, BuildingRules.Request build) { }

    void begin(ServerPlayer player, String session) {
        owner = player;
        level = player.level();
        sid = session;
        revision = 0;
        previousStacks = null;
        snapshot = null;
        NativeWorldGuard.begin(this, level, player.getUUID());
        publishInventory(true);
    }

    void tick() {
        if (owner == null || owner.isRemoved()) return;
        restoreRemovedSupports();
        var iterator = placed.entrySet().iterator();
        while (iterator.hasNext()) {
            var entry = iterator.next();
            // An unloaded chunk is not evidence that its block was removed.
            if (!level.isLoaded(entry.getKey())) continue;
            BlockState actual = level.getBlockState(entry.getKey());
            if (actual.equals(entry.getValue().state)) continue;
            BlockPos pos = entry.getKey();
            // Block entities (e.g. the furnace ticker) also change properties outside
            // scheduled BlockState.tick. A lit/open/powered variant is still our block.
            if(actual.getBlock()==entry.getValue().state.getBlock()) {
                entry.setValue(new PlacedBlock(actual,entry.getValue().debit));
                dirty.add(pos);
                continue;
            }
            // Native changes are registered synchronously by the guard. A mismatch here
            // belongs to unrelated Minecraft work: relinquish ownership rather than erase it.
            iterator.remove();
            nativeOwned.remove(pos);
            dirty.add(pos);
        }
        flushNativeChanges();
        if(level.getGameTime()>=nextGroundCleanup){reclaimGround(java.util.Set.of(),java.util.Map.of(),0);nextGroundCleanup=level.getGameTime()+10;}
        resumePendingGround();
        nativeOwned.removeIf(pos->!placed.containsKey(pos)&&!supports.containsKey(pos)&&!pendingSupportRestores.contains(pos)&&!dirty.contains(pos));
        publishInventory(false);
    }

    boolean handle(JsonObject message) {
        String type = Proto.type(message);
        if (!type.equals("inventory_request") && !type.equals("inventory_swap")
                && !type.equals("creative_give") && !type.equals("build_request")) return false;
        if (owner == null || !owner.isAlive() || owner.level() != level) return true;
        if (type.equals("inventory_request")) {
            publishInventory(true);
            return true;
        }
        // A malformed envelope is a protocol error; valid rejected actions stay nonfatal.
        String id = BuildingRules.requestId(message);
        BuildingRules.Request buildRequest = null;
        try {
            Outcome prior = requests.get(id);
            if (prior != null) {
                replay(id, prior);
                return true;
            }
            // Keep all accepted request IDs until session end. Evicting old swaps would
            // let a delayed retry mutate the inventory twice.
            if (!requests.canAccept(id)) {
                replay(id, new Outcome(type.equals("build_request") ? "build_result" : "inventory_result",
                        false, "session request limit reached; reconnect to continue", null));
                return true;
            }
            if (type.equals("build_request")) {
                buildRequest = BuildingRules.read(message);
                build(id, buildRequest);
            }
            else inventory(id, message, type);
        } catch (IllegalArgumentException e) {
            Outcome failure = new Outcome(type.equals("build_request") ? "build_result" : "inventory_result",
                    false, e.getMessage(), buildRequest);
            remember(id, failure);
            replay(id, failure);
        }
        return true;
    }

    void validatePose(double x, double y, double z) {
        if (owner == null || level == null) throw new IllegalArgumentException("building session unavailable");
        var target = owner.getBoundingBox().move(x - owner.getX(), y - owner.getY(), z - owner.getZ()).deflate(0.01);
        for (BlockPos position : BlockPos.betweenClosed((int) Math.floor(target.minX), (int) Math.floor(target.minY),
                (int) Math.floor(target.minZ), (int) Math.floor(target.maxX), (int) Math.floor(target.maxY),
                (int) Math.floor(target.maxZ))) {
            if (!level.isInWorldBounds(position))
                throw new IllegalArgumentException("MC bridge space outside world height; adjust groundY");
            // Teleporting may enter a not-yet-loaded chunk. Validate the real saved block,
            // never interpret an unloaded destination as empty and never clear terrain.
            level.getChunkAt(position);
            BlockState state = level.getBlockState(position);
            // Host terrain is represented by temporary support blocks too. They are
            // deliberately quantized to the host voxel grid and may overlap the exact
            // native slope/player bounds. Only foreign MC terrain is a fatal obstruction.
            // Use the same ownership test as the mutation guard (visible + support).
            PlacedBlock visible=placed.get(position);
            if(BridgePoseSpace.isForeignCollision(level,position,state,visible==null?null:visible.state,supports.get(position),target))
                throw new IllegalArgumentException("MC bridge space obstructed at " + position.toShortString()
                        + " by " + state + "; target=" + x + "," + y + "," + z
                        + "; original Minecraft terrain is protected");
        }
    }

    private void inventory(String id, JsonObject message, String type) {
        // Refresh before checking rev: changes made in the real MC UI invalidate old requests too.
        refreshInventory();
        if (Proto.integer(message, "rev") != revision)
            throw new IllegalArgumentException("inventory changed; refreshed snapshot");
        if (owner.containerMenu != owner.inventoryMenu)
            throw new IllegalArgumentException("close the Minecraft container before changing inventory");
        var inventory = owner.getInventory();
        BridgeRuntime.cancelItemUse(owner);
        if (type.equals("inventory_swap")) {
            int from = BuildingRules.inventorySlot(message, "fromSlot");
            int to = BuildingRules.inventorySlot(message, "toSlot");
            ItemStack a = getMutableSlot(from), b = getMutableSlot(to);
            setMutableSlot(from, b);
            setMutableSlot(to, a);
        } else {
            if (owner.gameMode() != GameType.CREATIVE) throw new IllegalArgumentException("Creative player required");
            String item = Proto.string(message, "item");
            if (!BuildingRules.CREATIVE_ITEMS.contains(item)) throw new IllegalArgumentException("item outside demo palette");
            int slot = Proto.selectedSlot(message);
            var actualItem = BuiltInRegistries.ITEM.getValue(Identifier.parse(item));
            if (actualItem == null) throw new IllegalArgumentException("item unavailable");
            ItemStack stack = new ItemStack(actualItem);
            stack.setCount(Math.min(64, stack.getMaxStackSize()));
            inventory.setItem(slot, stack);
        }
        inventory.setChanged();
        owner.inventoryMenu.broadcastChanges();
        Outcome outcome = new Outcome("inventory_result", true, "inventory updated", null);
        remember(id, outcome);
        replay(id, outcome);
    }

    private void build(String id, BuildingRules.Request request) {
        BlockPos pos = new BlockPos(request.x(), request.y(), request.z());
        if (!BuildingRules.inRange(request, owner.getX(), owner.getY(), owner.getZ()))
            throw new IllegalArgumentException("block must be within six blocks of player");
        if (!level.isInWorldBounds(pos) || !level.isLoaded(pos) || !owner.mayInteract(level, pos))
            throw new IllegalArgumentException("block is outside loaded editable world");
        if (owner.gameMode() != GameType.SURVIVAL && owner.gameMode() != GameType.CREATIVE)
            throw new IllegalArgumentException("Survival or Creative required");
        if (request.action().equals("place")) {
            if (placed.size() >= BuildingRules.MAX_BLOCKS) throw new IllegalArgumentException("256 block limit reached");
            if (!level.getBlockState(pos).isAir()) throw new IllegalArgumentException("existing terrain cannot be overwritten");
            ItemStack selected = owner.getMainHandItem();
            if (selected.isEmpty() || !BuildingRules.BLOCK_ITEMS.contains(itemId(selected))
                    || !(selected.getItem() instanceof BlockItem blockItem))
                throw new IllegalArgumentException("select a block from the demo palette");
            BlockState block = blockItem.getBlock().defaultBlockState();
            if (owner.getBoundingBox().intersects(new net.minecraft.world.phys.AABB(pos)))
                throw new IllegalArgumentException("cannot place inside player");
            ItemStack debit = owner.gameMode() == GameType.SURVIVAL ? selected.copyWithCount(1) : ItemStack.EMPTY;
            if (!level.setBlock(pos, block, Block.UPDATE_ALL)) throw new IllegalArgumentException("world rejected placement");
            placed.put(pos, new PlacedBlock(block, debit));
            if (!debit.isEmpty()) selected.shrink(1);
        } else {
            PlacedBlock owned = placed.get(pos);
            if (owned == null) throw new IllegalArgumentException("only this session's blocks can be broken");
            if (!level.getBlockState(pos).equals(owned.state)) {
                placed.remove(pos);
                // If MC already mined it, its vanilla drops own the refund; never duplicate those.
                emitBlock(request);
                throw new IllegalArgumentException("session block already changed in Minecraft");
            }
            if (!level.setBlock(pos, Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL))
                throw new IllegalArgumentException("world rejected break");
            placed.remove(pos);
            refund(owned.debit);
        }
        owner.getInventory().setChanged();
        owner.inventoryMenu.broadcastChanges();
        Outcome outcome = new Outcome("build_result", true,
                request.action().equals("place") ? "block placed" : "block removed", request);
        remember(id, outcome);
        replay(id, outcome);
    }

    private void replay(String id, Outcome outcome) {
        if (outcome.build != null) emitBlock(outcome.build);
        BridgeClient.send(Proto.operationResult(sid, outcome.type, id, outcome.ok, outcome.message));
        publishInventory(true);
    }

    private void emitBlock(BuildingRules.Request request) {
        var owned = placed.get(new BlockPos(request.x(), request.y(), request.z()));
        // Only session-owned construction is mirrored; unrelated MC terrain never appears in BL.
        String item = owned == null ? "minecraft:air"
                : BuiltInRegistries.BLOCK.getKey(owned.state.getBlock()).toString();
        BlockState state = owned == null ? Blocks.AIR.defaultBlockState() : owned.state;
        JsonObject message = Proto.blockState(sid, request, item);
        message.addProperty("stateId", Block.getId(state));
        message.addProperty("modelKey", state.isAir() || net.fabricmc.loader.api.FabricLoader.getInstance().getEnvironmentType()
                != net.fabricmc.api.EnvType.CLIENT ? "" : NativeBlockModels.request(sid, level,
                new BlockPos(request.x(), request.y(), request.z()), state));
        BridgeClient.send(message);
    }

    boolean ownsNative(BlockPos pos) {
        if (level == null) return false;
        BlockState actual = level.getBlockState(pos);
        PlacedBlock block = placed.get(pos);
        return block != null && actual.equals(block.state) || actual.equals(supports.get(pos));
    }
    boolean ownsVisible(BlockPos pos) { return placed.containsKey(pos); }
    boolean matchesSupport(BlockPos pos, BlockState state) { return state.equals(supports.get(pos)) && level.getBlockState(pos).equals(state); }
    void reserveNative(BlockPos pos) { nativeOwned.add(pos.immutable()); }
    boolean canReserveNative(java.util.Collection<BlockPos> positions) {
        long newPositions = positions.stream().distinct().filter(pos -> !nativeOwned.contains(pos)).count();
        return nativeOwned.size() + newPositions <= BuildingRules.MAX_BLOCKS;
    }

    boolean allowNativeMutation(BlockPos pos) {
        if (level == null || owner == null || !level.isInWorldBounds(pos) || !level.isLoaded(pos)
                || !owner.mayInteract(level, pos)) return false;
        PlacedBlock current = placed.get(pos);
        if (current != null) return level.getBlockState(pos).equals(current.state);
        if (supports.containsKey(pos)) return false;
        return level.getBlockState(pos).isAir()
                && (nativeOwned.contains(pos) || nativeOwned.size() < BuildingRules.MAX_BLOCKS);
    }

    boolean allowNativeMutation(BlockPos pos, BlockState state) {
        if (allowNativeMutation(pos)) return true;
        BlockState support = supports.get(pos);
        return support != null && state.is(Blocks.TNT) && HostGroundBlock.canSinkTnt(support)
                && level.getBlockState(pos).equals(support) && NativeWorldGuard.placementAllowed(level)
                && level.isInWorldBounds(pos) && level.isLoaded(pos) && owner.mayInteract(level, pos);
    }

    void nativeChanged(BlockPos pos, BlockState state) {
        pos = pos.immutable();
        PlacedBlock previous = placed.get(pos);
        BlockState support = supports.get(pos);
        if (support != null && state.equals(support)) return; // Hidden physics never becomes host-visible construction.
        if (state.is(Blocks.TNT) || previous != null && previous.state.is(Blocks.TNT))
            BridgeRuntime.log("WORLD owned pos=" + pos.toShortString() + " from="
                    + (previous == null ? "air" : previous.state) + " to=" + state);
        nativeOwned.add(pos);
        if (state.isAir()) {
            placed.remove(pos);
            if (support != null) pendingSupportRestores.add(pos);
        }
        else placed.put(pos, new PlacedBlock(state, ItemStack.EMPTY));
        dirty.add(pos);
    }

    private void restoreRemovedSupports() {
        var iterator = pendingSupportRestores.iterator();
        while (iterator.hasNext()) {
            BlockPos pos = iterator.next();
            if (!level.isLoaded(pos)) continue;
            BlockState support = supports.get(pos);
            if (support != null && level.getBlockState(pos).isAir() && !placed.containsKey(pos))
                level.setBlock(pos, support, Block.UPDATE_CLIENTS);
            // Never restore over an independently replaced, non-air block.
            iterator.remove();
        }
    }

    void alignPrimedTnt(net.minecraft.world.entity.item.PrimedTnt tnt) {
        BlockPos source = tnt.blockPosition();
        BlockState support = supports.get(source);
        if (support == null) {
            source = source.below();
            if (placed.containsKey(source)) return; // An actual visible block remains the support for stacked TNT.
            support = supports.get(source);
        }
        if (support == null) return;
        BlockState actual = level.getBlockState(source);
        if (!actual.equals(support) && !actual.isAir() && !ownsVisible(source)) return;
        // Only change initial Y. Native horizontal impulse, vertical bounce impulse and fuse are untouched.
        tnt.setPos(tnt.getX(), source.getY() + HostGroundBlock.height(support), tnt.getZ());
    }

    void prepareNativeTarget(NativeInteractionRules.Request request) {
        prepareNativeTarget(request, 1, false);
    }

    void prepareNativeTarget(NativeInteractionRules.Request request, double height, boolean sampledHeight) {
        BlockPos pos = new BlockPos(request.x(), request.y(), request.z());
        if (!level.isInWorldBounds(pos) || !level.isLoaded(pos) || !owner.mayInteract(level, pos))
            throw new IllegalArgumentException("target is outside loaded editable world");
        double reach = owner.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.BLOCK_INTERACTION_RANGE);
        if (owner.getEyePosition().distanceToSqr(new net.minecraft.world.phys.Vec3(request.hitX(), request.hitY(), request.hitZ()))
                > (reach + .1) * (reach + .1)) throw new IllegalArgumentException("target is beyond native reach");
        if (request.terrain()) {
            if (supports.containsKey(pos)) {
                if (!level.getBlockState(pos).equals(supports.get(pos)))
                    throw new IllegalArgumentException("host ground proxy was replaced by unrelated Minecraft terrain");
                BlockState sampled = HostGroundBlock.state(height);
                if (sampledHeight && !sampled.equals(supports.get(pos))) {
                    if (!level.setBlock(pos, sampled, Block.UPDATE_CLIENTS)) throw new IllegalArgumentException("could not update host ground height");
                    supports.put(pos.immutable(), sampled);
                }
                groundLeases.touch(cell(pos),level.getGameTime(),true);
                return;
            }
            if (!level.getBlockState(pos).isAir())
                throw new IllegalArgumentException("host ground overlaps original Minecraft terrain; choose empty bridge space");
            ensureGroundRoom(java.util.Set.of(pos),java.util.Map.of(column(pos),cell(pos)),1);
            BlockState state = HostGroundBlock.state(height);
            if (!level.setBlock(pos, state, Block.UPDATE_CLIENTS)) throw new IllegalArgumentException("could not prepare host ground");
            supports.put(pos.immutable(), state);
            groundLeases.touch(cell(pos),level.getGameTime(),true);
        } else if (!ownsVisible(pos) || !ownsNative(pos)) {
            throw new IllegalArgumentException("only session-owned blocks can be interacted with");
        }
    }

    /** Entity terrain retains the same sampled support ownership and budget as player/TNT terrain. */
    void prepareOwnedEntityGround(JsonObject message, net.minecraft.world.entity.Entity entity) {
        try{prepareEntityGroundChecked(message,entity);pendingEntityGround.remove(entity.getUUID());}
        catch(GroundCapacityDeferred pressure){
            PendingGround previous=pendingEntityGround.get(entity.getUUID());
            if(previous==null&&pendingEntityGround.size()>=16)throw pressure;
            pendingEntityGround.put(entity.getUUID(),new PendingGround(entity,message.deepCopy(),previous==null?level.getGameTime():previous.since));
        }
    }
    private void prepareEntityGroundChecked(JsonObject message,net.minecraft.world.entity.Entity entity){
        if (entity == null || entity.isRemoved() || entity.level() != level || !NativeWorldGuard.ownsEntity(entity))
            throw new IllegalArgumentException("ground target is not a live session-owned entity");
        var point=new net.minecraft.world.phys.Vec3(Proto.number(message,"x"),Proto.number(message,"y"),Proto.number(message,"z"));
        if(!Double.isFinite(point.x)||!Double.isFinite(point.y)||!Double.isFinite(point.z)
                || Math.abs(point.x)>30_000_000 || Math.abs(point.z)>30_000_000 || Math.abs(point.y)>2048)
            throw new IllegalArgumentException("entity ground anchor is outside world bounds");
        BlockPos anchor=BlockPos.containing(point);
        if (point.distanceToSqr(entity.position()) > 24 * 24)
            throw new IllegalArgumentException("entity ground anchor is beyond the owned movement corridor");
        prepareNativeGround(message, anchor);
    }

    /** Host-sampled terrain under the native bounce/drift path, never an invented flat platform. */
    void prepareNativeGround(JsonObject message, BlockPos origin) {
        if (!message.has("groundCount")) return;
        long count = Proto.integer(message, "groundCount");
        if (count > 81) throw new IllegalArgumentException("ground patch exceeds 9x9 budget");
        var additions = new java.util.LinkedHashMap<BlockPos, BlockState>();
        var coveredHeights = new java.util.LinkedHashMap<BlockPos, BlockState>();
        var columns = new java.util.HashSet<Long>();
        var requested = new java.util.LinkedHashSet<BlockPos>();
        var requestedColumns = new java.util.HashMap<Long,GroundSupportLeases.Cell>();
        for (int index = 0; index < count; index++) {
            String prefix = "ground" + index;
            BlockPos pos = new BlockPos(BuildingRules.coordinate(message, prefix + "X"),
                    BuildingRules.coordinate(message, prefix + "Y"), BuildingRules.coordinate(message, prefix + "Z"));
            BlockState desired = HostGroundBlock.state(surfaceHeight(message, prefix + "H"));
            if (Math.abs(pos.getX() - origin.getX()) > 4 || Math.abs(pos.getZ() - origin.getZ()) > 4
                    || Math.abs(pos.getY() - origin.getY()) > 16 || !level.isInWorldBounds(pos))
                throw new IllegalArgumentException("ground sample outside bounded TNT terrain patch");
            long column = ((long)pos.getX() << 32) ^ (pos.getZ() & 0xffffffffL);
            if (!columns.add(column)) throw new IllegalArgumentException("duplicate TNT ground sample column");
            requested.add(pos);requestedColumns.put(column,cell(pos));
            level.getChunkAt(pos); // Inspect the actual saved chunk, never assume unloaded terrain is air.
            BlockState actual = level.getBlockState(pos);
            if (actual.equals(supports.get(pos))) {
                if (message.has(prefix + "H") && !actual.equals(desired)) additions.put(pos, desired);
                continue;
            }
            PlacedBlock visible = placed.get(pos);
            if (visible != null && actual.equals(visible.state)) {
                if (supports.containsKey(pos) && message.has(prefix + "H")) coveredHeights.put(pos, desired);
                continue;
            }
            if (!actual.isAir() || supports.containsKey(pos) && !pendingSupportRestores.contains(pos))
                throw new IllegalArgumentException("TNT ground patch overlaps original Minecraft terrain at " + pos.toShortString());
            if (!owner.mayInteract(level, pos)) throw new IllegalArgumentException("TNT ground patch is outside editable world");
            additions.put(pos, desired);
        }
        long newSupports = additions.keySet().stream().filter(pos -> !supports.containsKey(pos)).count();
        ensureGroundRoom(requested,requestedColumns,(int)newSupports);
        // Validate every sample first, so one foreign cell cannot leave a partially applied patch.
        for (var addition : additions.entrySet()) {
            BlockPos pos = addition.getKey();
            BlockState state = addition.getValue();
            if (!level.setBlock(pos, state, Block.UPDATE_CLIENTS)) throw new IllegalArgumentException("world rejected TNT ground proxy");
            supports.put(pos, state);
        }
        supports.putAll(coveredHeights);
        for(BlockPos pos:requested)if(supports.containsKey(pos))groundLeases.touch(cell(pos),level.getGameTime(),false);
    }

    private static GroundSupportLeases.Cell cell(BlockPos pos){return new GroundSupportLeases.Cell(pos.getX(),pos.getY(),pos.getZ());}
    private static long column(BlockPos pos){return ((long)pos.getX()<<32)^(pos.getZ()&0xffffffffL);}
    private void ensureGroundRoom(java.util.Set<BlockPos> requested,Map<Long,GroundSupportLeases.Cell> incoming,int additions){
        // All samples were validated before reclamation. The pending real patch is
        // pinned while making room, so no valid sample is accidentally removed.
        reclaimGround(requested,incoming,0);
        if(supports.size()+additions>MAX_SUPPORTS)throw new GroundCapacityDeferred();
    }
    private void resumePendingGround(){
        if(pendingEntityGround.isEmpty())return;
        var first=pendingEntityGround.entrySet().iterator().next();
        PendingGround pending=first.getValue();
        // Rotate fairly: one actual saved chunk and one complete revalidation per
        // tick, never force-load an entire old trail on the server thread.
        pendingEntityGround.remove(first.getKey());
        if(pending.entity.isRemoved()||pending.entity.level()!=level||!NativeWorldGuard.ownsEntity(pending.entity))return;
        if(level.getGameTime()-pending.since>=100){
            BridgeRuntime.log("Native ground deferred request expired entity="+first.getKey()+" activeSupports="+supports.size()+"; original floor request was not applied");
            return;
        }
        reclaimGround(java.util.Set.of(),java.util.Map.of(),1);
        try{prepareEntityGroundChecked(pending.message,pending.entity);}
        catch(GroundCapacityDeferred pressure){pendingEntityGround.put(first.getKey(),pending);}
        catch(IllegalArgumentException changed){
            // Only an already validated snapshot reaches this branch. A moved
            // body or independently changed world does not authorize an old write.
            BridgeRuntime.log("Native ground deferred snapshot retired entity="+first.getKey()+" reason="+changed.getMessage());
        }
    }
    private void reclaimGround(java.util.Set<BlockPos> requested,Map<Long,GroundSupportLeases.Cell> incoming,int unloadedChunkBudget){
        if(level==null||owner==null||NativeWorldGuard.active(level))return;
        var entities=NativeWorldGuard.ownedEntitiesSnapshot();entities.add(owner);
        entities.removeIf(entity->entity.isRemoved()||entity.level()!=level);
        var active=new ArrayList<GroundSupportLeases.Region>();
        for(var entity:entities){
            var bounds=entity.getBoundingBox();var motion=entity.getKnownSpeed();
            if(!Double.isFinite(motion.x)||!Double.isFinite(motion.y)||!Double.isFinite(motion.z)||motion.lengthSqr()>1024)motion=net.minecraft.world.phys.Vec3.ZERO;
            var projected=bounds.expandTowards(motion.scale(8));
            active.add(new GroundSupportLeases.Region(projected.minX-2,projected.minY-16,projected.minZ-2,projected.maxX+2,projected.maxY+2,projected.maxZ+2));
        }
        var attached=new java.util.HashSet<BlockPos>();
        for(BlockPos visible:placed.keySet())for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)for(int dz=-1;dz<=1;dz++)attached.add(visible.offset(dx,dy,dz));
        var unloaded=new java.util.LinkedHashSet<net.minecraft.world.level.ChunkPos>();
        long now=level.getGameTime();
        for(var entry:new ArrayList<>(supports.entrySet())){
            BlockPos pos=entry.getKey();if(requested.contains(pos)||placed.containsKey(pos))continue;
            BlockState recorded=entry.getValue();
            boolean physical=false;
            var surface=new net.minecraft.world.phys.AABB(pos.getX(),pos.getY(),pos.getZ(),pos.getX()+1,pos.getY()+HostGroundBlock.height(recorded),pos.getZ()+1).inflate(.15,.4,.15);
            for(var entity:entities)if(entity.getBoundingBox().intersects(surface)){physical=true;break;}
            if(groundLeases.keep(cell(pos),now,attached.contains(pos),physical,active,incoming))continue;
            if(!level.isLoaded(pos)){unloaded.add(net.minecraft.world.level.ChunkPos.containing(pos));continue;}
            releaseSupport(pos,recorded);
        }
        // Deferred capacity pressure can inspect at most one previously registered
        // saved chunk per tick; normal cleanup never loads a distant trail.
        int loaded=0;
        for(var chunk:unloaded){
            if(loaded++>=unloadedChunkBudget)break;
            level.getChunkAt(new BlockPos(chunk.x()*16,owner.blockPosition().getY(),chunk.z()*16));
            // Re-run with zero loading after each bounded promotion and recheck
            // exact ownership/physical retention against the current real world.
            reclaimGround(requested,incoming,0);
        }
    }
    private void releaseSupport(BlockPos pos,BlockState recorded){
        if(!level.getBlockState(pos).equals(recorded)||level.setBlock(pos,Blocks.AIR.defaultBlockState(),Block.UPDATE_CLIENTS)){
            supports.remove(pos);groundLeases.forget(cell(pos));pendingSupportRestores.remove(pos);
            if(!placed.containsKey(pos)){nativeOwned.remove(pos);dirty.remove(pos);}
        }
    }
    int supportCount(){return supports.size();}
    int groundLeaseCount(){return groundLeases.size();}
    int pendingGroundCount(){return pendingEntityGround.size();}

    static double surfaceHeight(JsonObject message, String key) {
        double height = message.has(key) ? Proto.number(message, key) : 1;
        if (height < 0 || height > 1) throw new IllegalArgumentException("surface height must be 0..1: " + key);
        return height;
    }

    void flushNativeChanges() {
        for (BlockPos pos : dirty) emitBlock(new BuildingRules.Request("world-" + java.util.UUID.randomUUID(),
                pos.getX(), pos.getY(), pos.getZ(), "place"));
        dirty.clear();
        if (owner != null) { owner.getInventory().setChanged(); owner.inventoryMenu.broadcastChanges(); }
    }

    private void remember(String id, Outcome outcome) {
        requests.put(id, outcome);
    }

    private boolean refreshInventory() {
        var stacks = new ArrayList<ItemStack>(41);
        var items = new ArrayList<HotbarState.Slot>(36);
        var armor = new ArrayList<HotbarState.Slot>(4);
        for (int i = 0; i < 36; i++) {
            ItemStack stack = owner.getInventory().getItem(i);
            stacks.add(stack.copy());
            items.add(display(stack));
        }
        for (EquipmentSlot slot : ARMOR) {
            ItemStack stack = owner.getItemBySlot(slot);
            stacks.add(stack.copy());
            armor.add(display(stack));
        }
        stacks.add(owner.getOffhandItem().copy());
        InventoryState current = new InventoryState(owner.gameMode() == GameType.CREATIVE,
                owner.getInventory().getSelectedSlot(), items, armor, display(owner.getOffhandItem()));
        if (previousStacks != null && ItemStack.listMatches(previousStacks, stacks) && current.equals(snapshot)) return false;
        previousStacks = stacks;
        snapshot = current;
        revision = revision == Integer.MAX_VALUE ? 1 : revision + 1;
        return true;
    }

    private void publishInventory(boolean force) {
        if (owner == null) return;
        if (refreshInventory() || force) BridgeClient.send(snapshot.message(sid, revision));
    }

    private static HotbarState.Slot display(ItemStack stack) {
        return new HotbarState.Slot(itemId(stack), stack.isEmpty() ? 0 : stack.getCount());
    }

    private ItemStack getMutableSlot(int slot) {
        return slot == 40 ? owner.getOffhandItem() : owner.getInventory().getItem(slot);
    }

    private void setMutableSlot(int slot, ItemStack stack) {
        if (slot == 40) owner.setItemSlot(EquipmentSlot.OFFHAND, stack);
        else owner.getInventory().setItem(slot, stack);
    }

    private static String itemId(ItemStack stack) {
        return stack.isEmpty() ? "minecraft:air" : BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
    }

    private void refund(ItemStack debit) {
        if (debit.isEmpty()) return;
        ItemStack remaining = debit.copy();
        owner.getInventory().add(remaining);
        if (!remaining.isEmpty()) owner.drop(remaining, false, net.minecraft.util.Prediction.SERVER_ONLY);
    }

    void clear() {
        NativeWorldGuard.clear();
        if (level != null) {
            for (var entry : placed.entrySet()) {
                // Construction may have scrolled out of the loaded radius during battle.
                // Load only the at-most-256 owned positions before restoring them.
                level.getChunkAt(entry.getKey());
                // A replaced block belongs to another system and must never be removed or refunded.
                if (!level.getBlockState(entry.getKey()).equals(entry.getValue().state)) continue;
                if (level.setBlock(entry.getKey(), Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS)
                        && owner != null && owner.isAlive() && !owner.isRemoved()) refund(entry.getValue().debit);
            }
            for (var entry : supports.entrySet()) {
                level.getChunkAt(entry.getKey());
                if (level.getBlockState(entry.getKey()).equals(entry.getValue()))
                    level.setBlock(entry.getKey(), Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS);
            }
        }
        if (owner != null) owner.inventoryMenu.broadcastChanges();
        placed.clear();
        supports.clear();
        groundLeases.clear();nextGroundCleanup=0;
        pendingEntityGround.clear();
        pendingSupportRestores.clear();
        nativeOwned.clear();
        dirty.clear();
        requests.clear();
        previousStacks = null;
        snapshot = null;
        owner = null;
        level = null;
        sid = "";
        revision = 0;
    }
}
