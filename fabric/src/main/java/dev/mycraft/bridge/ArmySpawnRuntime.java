package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.level.TicketType;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.item.SpawnEggItem;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/** Exact host army batches prepare native chunks asynchronously and retain original vanilla spawning. */
final class ArmySpawnRuntime {
    private static final TicketType TYPE = new TicketType(40L, TicketType.FLAG_LOADING | TicketType.FLAG_SIMULATION);
    private static final int RADIUS = 2, CAPACITY = 16, MAX_RECEIPTS = 256, TIMEOUT_TICKS = 600;
    private record Entry(String kind, EntityType<?> type, int slot, Vec3 position) { }
    private record Plan(UUID requestId, boolean playerSide, List<Entry> entries) { }
    private static final class Receipt {
        final Plan plan;
        final UUID eventId = UUID.randomUUID();
        final Set<ChunkPos> chunks;
        final List<Mob> created = new ArrayList<>();
        final long started;
        MobRuntime.ArmySummonResult result;
        boolean slotsReserved, leased;
        long releaseAt = Long.MAX_VALUE;
        Receipt(Plan plan, Set<ChunkPos> chunks) { this.plan = plan; this.chunks = chunks; started = ticks; }
        boolean pending() { return result == null; }
    }
    private static final class Lease {
        final CompletableFuture<?> loading;
        int references;
        Lease(ChunkPos pos) { loading = level.getChunkSource().addTicketAndLoadWithRadius(TYPE, pos, RADIUS); }
    }
    private static final Map<UUID, Receipt> RECEIPTS = new LinkedHashMap<>();
    private static final Map<ChunkPos, Lease> LEASES = new LinkedHashMap<>();
    private static final Set<Integer> SLOTS = new HashSet<>();
    private static ServerPlayer player;
    private static ServerLevel level;
    private static String sid = "";
    private static long ticks;
    private static Consumer<JsonObject> reliable = message -> { };

    private ArmySpawnRuntime() { }
    static void begin(String session, ServerPlayer selected, Consumer<JsonObject> sink) {
        clear(); sid = session; player = selected; level = selected.level(); reliable = sink;
    }
    static int reservedCount() {
        int count = 0;
        for (Receipt receipt : RECEIPTS.values()) if (receipt.pending()) count += receipt.plan.entries().size();
        return count;
    }
    static MobRuntime.ArmySummonResult submit(String session, ServerPlayer selected, JsonObject message) {
        if (player == null || selected != player || player.level() != level || !player.isAlive() || !sid.equals(session)
                || !sid.equals(Proto.string(message, "sid")))
            return new MobRuntime.ArmySummonResult(false, true, false, "尚未连接当前战斗的玩家");
        Plan plan = parse(message);
        Receipt old = RECEIPTS.get(plan.requestId());
        if (old != null) {
            if (!old.plan.equals(plan)) return new MobRuntime.ArmySummonResult(false, true, false, "同一生成请求不能更改军队参数");
            if (old.pending()) return pending();
            publish(old); return old.result;
        }
        // An accepted request belongs to its original native coordinates. Moving
        // the player later cannot invalidate that exact pending/completed retry.
        for (Entry entry : plan.entries()) if (player.position().distanceToSqr(entry.position()) > 128 * 128)
            throw new IllegalArgumentException("new native army spawn is outside its bounded player corridor");
        if (RECEIPTS.size() >= MAX_RECEIPTS)
            return new MobRuntime.ArmySummonResult(false, true, false, "本场军队生成请求已达上限");
        Set<ChunkPos> chunks = new LinkedHashSet<>();
        for (Entry entry : plan.entries()) chunks.add(ChunkPos.containing(BlockPos.containing(entry.position())));
        Receipt receipt = new Receipt(plan, Set.copyOf(chunks));
        RECEIPTS.put(plan.requestId(), receipt);
        if (MobRuntime.activeMobCount() + reservedCount() > CAPACITY
                || !NativeWorldGuard.hasOwnedEntityCapacity(level, reservedCount()))
            return finish(receipt, false, "无法生成整支军队：本场生物或实体容量不足");
        for (Entry entry : plan.entries()) if (SLOTS.contains(entry.slot()))
            return finish(receipt, false, "该骑砍军队位置已经绑定原生生物");
        Set<ChunkPos> centres = new LinkedHashSet<>(LEASES.keySet()); centres.addAll(chunks);
        if (centres.size() > 16 || footprint(centres).size() > 96)
            return finish(receipt, false, "整支军队超出临时原生区块预算");
        for (Entry entry : plan.entries()) SLOTS.add(entry.slot());
        receipt.slotsReserved = true;
        for (ChunkPos pos : chunks) {
            Lease lease = LEASES.computeIfAbsent(pos, Lease::new); lease.references++;
        }
        receipt.leased = true;
        BridgeRuntime.log("Native army queued request=" + plan.requestId() + " count=" + plan.entries().size()
                + " playerSide=" + plan.playerSide() + " chunkCentres=" + chunks.size());
        return pending();
    }
    private static MobRuntime.ArmySummonResult pending() {
        return new MobRuntime.ArmySummonResult(true, false, false, "正在异步准备原生军队区块");
    }
    private static Plan parse(JsonObject message) {
        UUID requestId = UUID.fromString(Proto.string(message, "requestId"));
        long count = Proto.integer(message, "count");
        if (count < 1 || count > CAPACITY) throw new IllegalArgumentException("native army count must be 1..16");
        boolean playerSide = Proto.bool(message, "playerSide");
        List<Entry> entries = new ArrayList<>(); Set<Integer> slots = new HashSet<>();
        for (int index = 0; index < count; index++) {
            String prefix = "mob" + index;
            Identifier kind = Identifier.tryParse(Proto.string(message, prefix + "Kind"));
            if (kind == null || !kind.getNamespace().equals("minecraft"))
                throw new IllegalArgumentException("army mob must name a vanilla entity");
            EntityType<?> type = BuiltInRegistries.ENTITY_TYPE.getOptional(kind).orElse(null);
            if (type == null || !type.canSummon() || type != EntityTypes.WITHER && SpawnEggItem.byId(type).isEmpty())
                throw new IllegalArgumentException("army entity must be a vanilla spawn-egg mob or Wither");
            int slot = ActorRegistry.checkedId(Proto.integer(message, prefix + "Slot"));
            if (!slots.add(slot)) throw new IllegalArgumentException("native army contains a duplicate host slot");
            Vec3 pos = new Vec3(Proto.number(message, prefix + "X"), Proto.number(message, prefix + "Y"),
                    Proto.number(message, prefix + "Z"));
            if (!level.isInWorldBounds(BlockPos.containing(pos)) || Math.abs(pos.x) > 30_000_000
                    || Math.abs(pos.z) > 30_000_000 || Math.abs(pos.y) > 2048)
                throw new IllegalArgumentException("native army spawn is outside its bounded player corridor");
            entries.add(new Entry(kind.toString(), type, slot, pos));
        }
        return new Plan(requestId, playerSide, List.copyOf(entries));
    }
    static void tick() {
        if (player == null) return;
        ticks++;
        for (ChunkPos pos : LEASES.keySet()) level.getChunkSource().addTicketWithRadius(TYPE, pos, RADIUS);
        for (Receipt receipt : new ArrayList<>(RECEIPTS.values())) {
            if (receipt.pending()) {
                if (!player.isAlive() || player.level() != level) finish(receipt, false, "玩家死亡或离开当前世界，取消原生军队生成");
                else if (ticks - receipt.started > TIMEOUT_TICKS) finish(receipt, false, "原生军队区块准备超时");
                else if (ready(receipt)) spawn(receipt);
            }
            if (!receipt.pending() && ticks >= receipt.releaseAt) release(receipt);
        }
    }
    private static boolean ready(Receipt receipt) {
        for (ChunkPos pos : receipt.chunks) {
            Lease lease = LEASES.get(pos);
            if (lease == null || !lease.loading.isDone() || lease.loading.isCompletedExceptionally()
                    || !level.isPositionEntityTicking(new BlockPos(pos.x() * 16, 0, pos.z() * 16))) return false;
        }
        return true;
    }
    private static void spawn(Receipt receipt) {
        if (MobRuntime.activeMobCount() + reservedCount() > CAPACITY
                || !NativeWorldGuard.hasOwnedEntityCapacity(level, reservedCount())) {
            finish(receipt, false, "区块准备后整支军队容量不足"); return;
        }
        try {
            // Pre-create and inspect every genuine native body before publishing the first army slot.
            for (Entry entry : receipt.plan.entries()) {
                NativeWorldGuard.enter(level, BlockPos.containing(entry.position()));
                Entity created;
                try { created = entry.type().create(level, null, BlockPos.containing(entry.position()), EntitySpawnReason.COMMAND, false, false); }
                finally { NativeWorldGuard.exit(); }
                if (!(created instanceof Mob mob)) throw new IllegalArgumentException("军队实体不是原生生物");
                mob.setPos(entry.position());
                if (mob instanceof WitherBoss boss) boss.makeInvulnerable();
                if (!level.noCollision(mob, mob.getBoundingBox())) throw new IllegalArgumentException("军队生成位置没有足够原生空间");
                for (Mob previous : receipt.created) if (mob.getBoundingBox().intersects(previous.getBoundingBox()))
                    throw new IllegalArgumentException("军队生成位置相互重叠");
                receipt.created.add(mob);
            }
            for (int index = 0; index < receipt.created.size(); index++) {
                Mob mob = receipt.created.get(index); Entry entry = receipt.plan.entries().get(index);
                // addFreshEntity invokes MobRuntime.added synchronously; assignment must precede that first state.
                MobRuntime.assignArmy(mob, entry.slot(), receipt.plan.playerSide());
                NativeWorldGuard.enter(level, mob.blockPosition());
                try {
                    if (!level.addFreshEntity(mob) || !MobRuntime.owns(mob))
                        throw new IllegalArgumentException("原版世界拒绝整支军队中的一个生物");
                } finally { NativeWorldGuard.exit(); }
            }
            finish(receipt, true, "已生成 " + receipt.created.size() + " 个原生 MC 军队生物");
            BridgeRuntime.log("Native army ready request=" + receipt.plan.requestId() + " count=" + receipt.created.size());
        } catch (RuntimeException failure) {
            // Command-spawn has no inventory refund. Preserve genuine acquired equipment/loot,
            // then remove only this request's actual owned bodies, never unrelated world mobs.
            for (Mob mob : receipt.created) {
                if (MobRuntime.owns(mob) && mob.isAlive()) {
                    NativeWorldGuard.enterEntity(level, mob); OwnedMobLoot.enter();
                    try { mob.dropPreservedEquipment(level); }
                    finally { OwnedMobLoot.exit(); NativeWorldGuard.exit(); }
                }
                if (!mob.isRemoved()) mob.discard();
                MobRuntime.unassignFailedArmy(mob);
            }
            finish(receipt, false, failure.getMessage() == null ? "原生军队生成失败" : failure.getMessage());
        }
    }
    private static MobRuntime.ArmySummonResult finish(Receipt receipt, boolean success, String message) {
        receipt.result = new MobRuntime.ArmySummonResult(true, true, success, message);
        if (!success && receipt.slotsReserved) {
            for (Entry entry : receipt.plan.entries()) SLOTS.remove(entry.slot());
            receipt.slotsReserved = false;
        }
        // A successful entity already owns its separate lease. Retain the spawn lease
        // briefly so native asynchronous ownership can finish without a ticking gap.
        receipt.releaseAt = success ? ticks + 40 : ticks;
        publish(receipt); return receipt.result;
    }
    private static void publish(Receipt receipt) {
        JsonObject message = Proto.msg(sid, 0, "mc_army_spawn_result");
        message.addProperty("eventId", receipt.eventId.toString());
        message.addProperty("requestId", receipt.plan.requestId().toString());
        message.addProperty("ok", receipt.result.success()); message.addProperty("message", receipt.result.message());
        reliable.accept(message);
    }
    private static void release(Receipt receipt) {
        if (receipt.releaseAt == Long.MAX_VALUE) return;
        for (ChunkPos pos : receipt.leased ? receipt.chunks : Set.<ChunkPos>of()) {
            Lease lease = LEASES.get(pos);
            if (lease != null && --lease.references == 0) {
                level.getChunkSource().removeTicketWithRadius(TYPE, pos, RADIUS); LEASES.remove(pos);
            }
        }
        receipt.leased = false;
        receipt.releaseAt = Long.MAX_VALUE;
    }
    private static Set<ChunkPos> footprint(Set<ChunkPos> centres) {
        Set<ChunkPos> result = new LinkedHashSet<>();
        for (ChunkPos pos : centres) for (int dx = -2; dx <= 2; dx++) for (int dz = -2; dz <= 2; dz++)
            result.add(new ChunkPos(pos.x() + dx, pos.z() + dz));
        return result;
    }
    static int pendingCount() { return (int) RECEIPTS.values().stream().filter(Receipt::pending).count(); }
    static int centreCount() { return LEASES.size(); }
    static void clear() {
        if (player != null) for (ChunkPos pos : LEASES.keySet()) level.getChunkSource().removeTicketWithRadius(TYPE, pos, RADIUS);
        RECEIPTS.clear(); LEASES.clear(); SLOTS.clear(); player = null; level = null; sid = ""; ticks = 0; reliable = message -> { };
    }
}

