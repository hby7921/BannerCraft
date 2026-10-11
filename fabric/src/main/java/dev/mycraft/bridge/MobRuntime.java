package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageTypes;
import net.minecraft.world.effect.MobEffectInstance;
import net.minecraft.world.effect.MobEffects;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.NeutralMob;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.entity.monster.Enemy;
import net.minecraft.world.entity.monster.piglin.Piglin;
import net.minecraft.world.entity.monster.spider.Spider;
import net.minecraft.core.BlockPos;
import net.minecraft.resources.Identifier;
import net.minecraft.world.entity.WalkAnimationState;
import net.minecraft.util.Mth;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.SpawnEggItem;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.Consumer;

/** Actual vanilla mobs own AI, attacks, status effects, HP, death and loot. */
public final class MobRuntime {
    private static final int CAPACITY = 16, REMOVED_CAPACITY = 4096;
    private static final Map<UUID, Mob> MOBS = new LinkedHashMap<>();
    public record ArmyAssignment(int slot, boolean playerSide) { }
    private static final Map<UUID, ArmyAssignment> ARMY = new LinkedHashMap<>();
    private static final Map<UUID, VisualWalk> WALK = new LinkedHashMap<>();
    private static final Map<UUID, Boolean> REMOVED = new LinkedHashMap<>();
    private static final Map<Integer, UUID> WITHER_SOURCES = new LinkedHashMap<>();
    private static final Map<Integer, UUID> POISON_SOURCES = new LinkedHashMap<>();
    private static final Map<Integer, UUID> EFFECT_TARGETS = new LinkedHashMap<>();
    private record HurtReceipt(UUID target, int source, double damage, String kind, long received) { }
    private static final Map<String, HurtReceipt> HURT_SEEN = new LinkedHashMap<>();
    private static ServerPlayer owner;
    private static String sid = "";
    private static Consumer<JsonObject> reliable = message -> { }, states = message -> { };
    private static int diagnostics;

    private static final class VisualWalk {
        final WalkAnimationState animation = new WalkAnimationState();
        int nativeTick;
        VisualWalk(Mob mob) { nativeTick = mob.tickCount; }
        void advance(Mob mob) {
            if (nativeTick == mob.tickCount) return;
            nativeTick = mob.tickCount;
            // LivingEntity.aiStep calls calculateEntityAnimation only on the client.
            // Reproduce that exact 26.3 visual calculation after each genuine server
            // tick, without ticking the mob or changing its AI/movement state.
            if (mob.isPassenger() || !mob.isAlive()) animation.stop();
            else {
                float distance = (float) Mth.length(mob.getX() - mob.xo, 0, mob.getZ() - mob.zo);
                animation.update(Math.min(distance * 4.0F, 1.0F), 0.4F, mob.isBaby() ? 3.0F : 1.0F);
            }
        }
    }

    private MobRuntime() { }
    public static void begin(String session, ServerPlayer player, Consumer<JsonObject> reliableSink, Consumer<JsonObject> stateSink) {
        clear(); sid = session; owner = player; reliable = reliableSink; states = stateSink;
        ArmySpawnRuntime.begin(session, player, reliableSink);
        OwnedEntityTicks.begin(player.level());
        MobProjectileRuntime.begin(session, player, reliableSink, stateSink);
    }
    public static boolean owns(Entity entity) {
        return entity != null && owner != null && entity.level() == owner.level()
                && MOBS.get(entity.getUUID()) == entity && NativeWorldGuard.ownsEntity(entity);
    }
    public static boolean isActor(BridgeTargetEntity entity) {
        return owner != null && entity != null && entity.level() == owner.level() && !entity.isRemoved()
                && entity.isAlive() && BridgeRuntime.findActor(entity.actorId()) == entity;
    }
    public static boolean isOwnerPlayer(Entity entity) { return entity == owner && owner != null && owner.isAlive(); }
    public static boolean allowedTarget(Entity entity) {
        return entity instanceof BridgeTargetEntity target ? isActor(target) : entity != null && NativeWorldGuard.ownsEntity(entity);
    }
    /** Army membership filters acquisition, never vanilla blast/stray-projectile friendly fire. */
    public static boolean canTarget(Mob source, Entity target) {
        if (!allowedTarget(target)) return false;
        ArmyAssignment army = ARMY.get(source.getUUID());
        if (army == null) return true;
        if (target instanceof BridgeTargetEntity actor)
            return isActor(actor) && actor.isHostile() == army.playerSide();
        if (isOwnerPlayer(target)) return !army.playerSide();
        if (target instanceof Mob other) {
            ArmyAssignment otherArmy = ARMY.get(other.getUUID());
            return otherArmy != null && otherArmy.playerSide() != army.playerSide();
        }
        return false;
    }
    static void assignArmy(Mob mob, int slot, boolean playerSide) { ARMY.put(mob.getUUID(), new ArmyAssignment(slot, playerSide)); }
    static void unassignFailedArmy(Mob mob) { ARMY.remove(mob.getUUID()); }
    static int activeMobCount() { return activeCount(); }
    public static ArmyAssignment armyAssignment(Entity entity) { return entity == null ? null : ARMY.get(entity.getUUID()); }
    public record ArmySummonResult(boolean accepted, boolean complete, boolean success, String message) { }
    public static ArmySummonResult summonArmy(String session, ServerPlayer player, JsonObject message) {
        return ArmySpawnRuntime.submit(session, player, message);
    }
    /** Genuine living attack candidates for the native player ray; never returns ambient world mobs. */
    static Iterable<Mob> attackTargets() {
        var result = new ArrayList<Mob>();
        for (var mob : MOBS.values()) if (owns(mob) && mob.isAlive() && !mob.isRemoved()) result.add(mob);
        return result;
    }
    public static boolean canAdd(Entity entity) {
        if (!(entity instanceof Mob)) return true;
        return owner != null && entity.level() == owner.level() && (MOBS.containsKey(entity.getUUID())
                || activeCount() < CAPACITY && (ARMY.containsKey(entity.getUUID())
                    || activeCount() + ArmySpawnRuntime.reservedCount() < CAPACITY));
    }
    public static boolean canUseSpawnEgg(ServerPlayer player, ItemStack stack) {
        if (!(stack.getItem() instanceof SpawnEggItem)) return true;
        return player == owner && hasCapacity(player.level());
    }
    public static boolean hasCapacity(ServerLevel level) {
        return owner != null && owner.level() == level && activeCount() + ArmySpawnRuntime.reservedCount() < CAPACITY
                && NativeWorldGuard.hasOwnedEntityCapacity(level);
    }
    public record SummonResult(boolean success, String message, String entityId) { }
    /** The bridge command's ACTIVE check remains at its caller; this gate proves the same selected player. */
    public static SummonResult summonOwned(ServerPlayer player, String name) {
        if (player != owner || !player.isAlive()) return new SummonResult(false, "尚未连接当前战斗", "");
        Identifier id = Identifier.tryParse(name.contains(":") ? name : "minecraft:" + name);
        if (id == null || !id.getNamespace().equals("minecraft")) return new SummonResult(false, "未知原版生物", "");
        var type = BuiltInRegistries.ENTITY_TYPE.getOptional(id).orElse(null);
        if (type == null || !type.canSummon() || type != EntityTypes.WITHER && SpawnEggItem.byId(type).isEmpty())
            return new SummonResult(false, "该类型不是支持的原版刷怪蛋生物或凋零", "");
        if (!hasCapacity(player.level())) return new SummonResult(false, "本场生物或实体数量已达上限", "");
        // Native EntityType.create supplies original equipment/attributes through finalizeSpawn.
        Vec3 look = Vec3.directionFromRotation(0, player.getYRot());
        BlockPos pos = BlockPos.containing(player.position().add(look.scale(4)));
        NativeWorldGuard.enter(player.level(), pos);
        try {
            Entity created = type.create(player.level(), null, pos, EntitySpawnReason.COMMAND, true, false);
            if (!(created instanceof Mob mob)) return new SummonResult(false, "该实体不是生物", "");
            if (mob instanceof WitherBoss wither) wither.makeInvulnerable();
            if (!player.level().noCollision(mob, mob.getBoundingBox())) return new SummonResult(false, "前方没有足够生成空间", "");
            if (!player.level().addFreshEntity(mob)) return new SummonResult(false, "原版世界拒绝生成该生物", "");
            return new SummonResult(true, "已召唤 " + mob.getDisplayName().getString(), mob.getUUID().toString());
        } finally { NativeWorldGuard.exit(); }
    }
    private static int activeCount() {
        int result = 0;
        for (var mob : MOBS.values()) if (!mob.isRemoved()) result++;
        return result;
    }
    public static void added(ServerLevel level, Entity entity) {
        if (!(entity instanceof Mob mob) || owner == null || level != owner.level()
                || !NativeWorldGuard.ownsEntity(mob) || !canAdd(mob)) return;
        if (MOBS.putIfAbsent(mob.getUUID(), mob) != null) return;
        WALK.put(mob.getUUID(), new VisualWalk(mob));
        // Wither already targets LivingEntity in its original goals and all three heads.
        // Other original hostile mobs need the real host proxy included alongside player targets.
        // Neutral/passive mobs keep their native goals and retaliation rules.
        if (targetsHostActors(mob) && !(mob instanceof WitherBoss))
            ((MobTargetSelectorAccess) mob).mycraft$targetSelector().addGoal(0, new OwnedMobTargetGoal(mob));
        if (mob.getTarget() != null && !canTarget(mob, mob.getTarget())) mob.setTarget(null);
        OwnedEntityTicks.added(mob);
        publish(mob);
        if (diagnostics++ < 32) BridgeRuntime.log("Native owned mob kind=" + BuiltInRegistries.ENTITY_TYPE.getKey(mob.getType())
                + " id=" + mob.getUUID() + " hp=" + mob.getHealth());
    }
    static boolean targetsHostActors(Mob mob) {
        return mob instanceof Enemy && !(mob instanceof NeutralMob) && !(mob instanceof Piglin);
    }
    static boolean hostile(Mob mob) {
        if (mob instanceof NeutralMob neutral) return neutral.isAngry();
        if (mob instanceof Piglin) return mob.getTarget() != null;
        if (mob instanceof Spider && mob.getLightLevelDependentMagicValue() >= .5F) return mob.getTarget() != null;
        return mob instanceof Enemy;
    }
    public static UUID damageSourceId(DamageSource source) {
        if (owns(source.getEntity())) return source.getEntity().getUUID();
        UUID projectile = MobProjectileRuntime.sourceId(source.getDirectEntity());
        if (projectile != null) return projectile;
        UUID explosion = MobProjectileRuntime.explosionSourceId(source);
        return explosion != null ? explosion : MobProjectileRuntime.impactSourceId();
    }
    public static boolean captures(int actorId, DamageSource source) {
        BridgeTargetEntity target = BridgeRuntime.findActor(actorId);
        if (!isActor(target)) return false;
        return damageSourceId(source) != null || source.is(DamageTypes.WITHER) && target.hasEffect(MobEffects.WITHER)
                && WITHER_SOURCES.containsKey(actorId) || source.is(DamageTypes.MAGIC) && target.hasEffect(MobEffects.POISON)
                && POISON_SOURCES.containsKey(actorId);
    }
    public static void captureDamage(int actorId, DamageSource source, float amount) {
        if (!captures(actorId, source) || !Float.isFinite(amount) || amount <= 0) return;
        UUID sourceId = damageSourceId(source);
        if (sourceId == null) sourceId = source.is(DamageTypes.WITHER) ? WITHER_SOURCES.get(actorId) : POISON_SOURCES.get(actorId);
        if (sourceId == null) return;
        float converted = amount * (float) MycraftBridge.CONFIG.mcToBlDamageFactor;
        if (!Float.isFinite(converted) || converted <= 0) return;
        JsonObject hit = Proto.msg(sid, 0, "mob_hit");
        hit.addProperty("eventId", UUID.randomUUID().toString()); hit.addProperty("sourceId", sourceId.toString());
        hit.addProperty("eid", actorId); hit.addProperty("damage", converted);
        hit.addProperty("kind", source.is(DamageTypes.WITHER) ? "wither"
                : source.is(net.minecraft.tags.DamageTypeTags.IS_EXPLOSION) ? "explosion"
                : source.getDirectEntity() instanceof net.minecraft.world.entity.projectile.Projectile || source.is(DamageTypes.MAGIC)
                        ? "projectile" : "melee");
        reliable.accept(hit);
        if (diagnostics++ < 32) BridgeRuntime.log("Native mob damage sourceId=" + sourceId + " actor=" + actorId
                + " nativeDamage=" + amount + " hostDamage=" + converted + " kind=" + Proto.string(hit, "kind"));
    }
    static void effectApplied(BridgeTargetEntity target, MobEffectInstance effect, Entity source) {
        if (!effect.is(MobEffects.WITHER) && !effect.is(MobEffects.POISON)) return;
        Map<Integer, UUID> sources = effect.is(MobEffects.WITHER) ? WITHER_SOURCES : POISON_SOURCES;
        UUID sourceId = owns(source) ? source.getUUID() : MobProjectileRuntime.impactSourceId();
        if (sourceId != null && isActor(target)) {
            sources.put(target.actorId(), sourceId); EFFECT_TARGETS.put(target.actorId(), target.getUUID());
            OwnedEntityTicks.added(target);
        } else {
            sources.remove(target.actorId());
            retireEffectRouteIfEmpty(target.actorId());
        }
    }
    static boolean keepsEffectTicking(BridgeTargetEntity target) {
        return isActor(target) && (target.hasEffect(MobEffects.WITHER) && WITHER_SOURCES.containsKey(target.actorId())
                || target.hasEffect(MobEffects.POISON) && POISON_SOURCES.containsKey(target.actorId()));
    }
    static void retireEffect(int actorId) {
        WITHER_SOURCES.remove(actorId);
        retireEffectRouteIfEmpty(actorId);
    }
    private static void retireEffectRouteIfEmpty(int actorId) {
        if (WITHER_SOURCES.containsKey(actorId) || POISON_SOURCES.containsKey(actorId)) return;
        UUID target = EFFECT_TARGETS.remove(actorId); if (target != null) OwnedEntityTicks.retire(target);
    }
    public static void retireActor(int actorId) { WITHER_SOURCES.remove(actorId); POISON_SOURCES.remove(actorId); retireEffectRouteIfEmpty(actorId); }
    public static void prepareGround(JsonObject message, BuildingRuntime building) {
        Mob mob = MOBS.get(UUID.fromString(Proto.string(message, "entityId")));
        if (mob == null || !owns(mob) || mob.isRemoved()) return;
        building.prepareOwnedEntityGround(message, mob);
    }
    /** The caller ACKs true, including retries and a previously-owned mob that has already died. */
    public static boolean hurt(JsonObject message, BridgeTargetEntity source) {
        if (owner == null || !sid.equals(Proto.string(message, "sid"))) return false;
        String eventId = Proto.string(message, "eventId");
        UUID.fromString(eventId);
        UUID id = UUID.fromString(Proto.string(message, "entityId"));
        int sourceId = ActorRegistry.checkedId(Proto.integer(message, "sourceEid"));
        double damage = Proto.number(message, "damage");
        String kind = message.has("sourceKind") ? Proto.string(message, "sourceKind") : "melee";
        if (!kind.equals("melee") && !kind.equals("arrow") && !kind.equals("projectile")) throw new IllegalArgumentException("invalid native mob hurt source kind");
        if (!Double.isFinite(damage) || damage <= 0 || damage > 10000) throw new IllegalArgumentException("invalid mob damage");
        long now = System.nanoTime();
        HURT_SEEN.entrySet().removeIf(entry -> now - entry.getValue().received() >= 10_000_000_000L);
        HurtReceipt previous = HURT_SEEN.get(eventId);
        if (previous != null) {
            // A soldier may die or leave the interest set while the original receipt is retried.
            // ACK only the exact original event; never apply again or accept a changed payload.
            return previous.target().equals(id) && previous.source() == sourceId && previous.damage() == damage && previous.kind().equals(kind);
        }
        if (!isActor(source) || source.actorId() != sourceId) return false;
        Mob target = MOBS.get(id);
        if (target == null && !REMOVED.containsKey(id)) return false;
        if (target != null && !owns(target)) return false;
        if (HURT_SEEN.size() >= 4096) throw new IllegalArgumentException("mob retry cache capacity exceeded");
        HURT_SEEN.put(eventId, new HurtReceipt(id, sourceId, damage, kind, now));
        if (target == null || !target.isAlive() || target.isRemoved()) return true;
        // The host already converted this amount. Native armor/immunity/death/drop math runs once.
        NativeWorldGuard.enterEntity(owner.level(), target);
        try {
            DamageSource nativeSource = switch (kind) {
                // These direct entities are never added/ticked/rendered. Their exact original
                // Java classes preserve vanilla category tests (notably powered Wither arrow immunity).
                case "arrow" -> owner.level().damageSources().arrow(new net.minecraft.world.entity.projectile.arrow.Arrow(EntityTypes.ARROW, owner.level()), source);
                case "projectile" -> owner.level().damageSources().thrown(EntityTypes.SNOWBALL.create(owner.level(), EntitySpawnReason.TRIGGERED), source);
                default -> owner.level().damageSources().mobAttack(source);
            };
            target.hurtServer(owner.level(), nativeSource, (float) damage);
        }
        finally { NativeWorldGuard.exit(); }
        publish(target);
        return true;
    }
    public static void tick() {
        if (owner == null) return;
        ArmySpawnRuntime.tick();
        MobProjectileRuntime.tick();
        for (int actor : new ArrayList<>(EFFECT_TARGETS.keySet())) {
            BridgeTargetEntity target = BridgeRuntime.findActor(actor);
            if (!isActor(target)) { retireActor(actor); continue; }
            if (!target.hasEffect(MobEffects.WITHER)) WITHER_SOURCES.remove(actor);
            if (!target.hasEffect(MobEffects.POISON)) POISON_SOURCES.remove(actor);
            retireEffectRouteIfEmpty(actor);
        }
        for (var mob : new ArrayList<>(MOBS.values())) {
            if (mob.isRemoved() || mob.level() != owner.level()) {
                JsonObject removed = Proto.msg(sid, 0, "mob_remove");
                removed.addProperty("eventId", UUID.randomUUID().toString()); removed.addProperty("entityId", mob.getUUID().toString());
                removed.addProperty("reason", mob.getHealth() <= 0 ? "death" : "native_removed"); reliable.accept(removed);
                MOBS.remove(mob.getUUID()); WALK.remove(mob.getUUID()); OwnedEntityTicks.retire(mob.getUUID());
                NativeMobModels.retire(mob.getUUID().toString());
                REMOVED.put(mob.getUUID(), true);
                if (REMOVED.size() > REMOVED_CAPACITY) REMOVED.remove(REMOVED.keySet().iterator().next());
            } else {
                if (mob.getTarget() != null && !canTarget(mob, mob.getTarget())) mob.setTarget(null);
                publish(mob);
            }
        }
    }
    private static void publish(Mob mob) {
        String kind = BuiltInRegistries.ENTITY_TYPE.getKey(mob.getType()).getPath();
        String visualKey = NativeMobModels.request(sid, mob.getId(), mob.getUUID().toString());
        if (!visualKey.matches("[a-f0-9]{32}") && !kind.equals("wither_skeleton")) return;
        VisualWalk walk = WALK.get(mob.getUUID());
        if (walk != null) walk.advance(mob);
        JsonObject state = Proto.msg(sid, 0, "mob_state");
        state.addProperty("entityId", mob.getUUID().toString()); state.addProperty("kind", kind);
        ArmyAssignment army = ARMY.get(mob.getUUID());
        if (army != null) {
            state.addProperty("armySlot", army.slot()); state.addProperty("armyPlayerSide", army.playerSide());
        }
        state.addProperty("width", mob.getBbWidth()); state.addProperty("height", mob.getBbHeight());
        state.addProperty("hostile", hostile(mob)); state.addProperty("baby", mob.isBaby());
        if (visualKey.matches("[a-f0-9]{32}")) state.addProperty("visualKey", visualKey);
        boolean boss = mob instanceof WitherBoss;
        state.addProperty("boss", boss); state.addProperty("bossName", boss ? mob.getDisplayName().getString().substring(0,
                Math.min(128, mob.getDisplayName().getString().length())) : "");
        state.addProperty("invulnerableTicks", boss ? ((WitherBoss)mob).getInvulnerableTicks() : 0);
        if (mob instanceof WitherBoss wither) for (int head = 0; head < 2; head++) {
            state.addProperty("sideHead" + head + "Yaw", wither.getHeadYRots()[head]);
            state.addProperty("sideHead" + head + "Pitch", wither.getHeadXRots()[head]);
        }
        state.addProperty("x", mob.getX()); state.addProperty("y", mob.getY()); state.addProperty("z", mob.getZ());
        state.addProperty("yaw", mob.getYRot()); state.addProperty("bodyYaw", mob.yBodyRot);
        state.addProperty("headYaw", mob.yHeadRot); state.addProperty("pitch", mob.getXRot());
        Vec3 motion = mob.getDeltaMovement(); state.addProperty("vx", motion.x); state.addProperty("vy", motion.y); state.addProperty("vz", motion.z);
        state.addProperty("hp", mob.getHealth()); state.addProperty("maxHp", mob.getMaxHealth());
        state.addProperty("alive", mob.isAlive() && !mob.isRemoved()); state.addProperty("nativeTick", mob.tickCount);
        state.addProperty("targetEid", mob.getTarget() instanceof BridgeTargetEntity target && isActor(target) ? target.actorId() : 0);
        state.addProperty("mainItem", BuiltInRegistries.ITEM.getKey(mob.getMainHandItem().getItem()).toString());
        state.addProperty("aggressive", mob.isAggressive()); state.addProperty("walkPosition", walk == null ? 0 : walk.animation.position());
        state.addProperty("walkSpeed", walk == null ? 0 : walk.animation.speed()); state.addProperty("attackProgress", Math.clamp(mob.getSwingAnimation(1), 0, 1));
        state.addProperty("hurtTime", Math.clamp(mob.hurtTime, 0, 200)); state.addProperty("deathTime", Math.clamp(mob.deathTime, 0, 200));
        states.accept(state);
    }
    /** Native pickup marks real equipment as preserved; return it before session despawn. */
    public static void preservePickedEquipment() {
        for (var mob : new ArrayList<>(MOBS.values())) {
            // Death already ran native loot synchronously; never duplicate it.
            if (!NativeWorldGuard.ownsEntity(mob) || !mob.isAlive() || mob.isRemoved()
                    || !(mob.level() instanceof ServerLevel level)) continue;
            NativeWorldGuard.enterEntity(level, mob); OwnedMobLoot.enter();
            try { mob.dropPreservedEquipment(level); }
            finally { OwnedMobLoot.exit(); NativeWorldGuard.exit(); }
        }
    }
    public static void clear() {
        ArmySpawnRuntime.clear();
        MobProjectileRuntime.clear();
        for (UUID id : MOBS.keySet()) { OwnedEntityTicks.retire(id); NativeMobModels.retire(id.toString()); }
        for (UUID id : EFFECT_TARGETS.values()) OwnedEntityTicks.retire(id);
        MOBS.clear(); ARMY.clear(); WALK.clear(); REMOVED.clear(); WITHER_SOURCES.clear(); POISON_SOURCES.clear(); EFFECT_TARGETS.clear(); HURT_SEEN.clear(); owner = null; sid = ""; diagnostics = 0;
        reliable = message -> { }; states = message -> { };
    }
}
