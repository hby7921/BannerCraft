package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.component.DataComponents;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.entity.projectile.arrow.ThrownTrident;
import net.minecraft.world.entity.projectile.FireworkRocketEntity;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.Vec3;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.Consumer;

/** Native weapon rules own damage, ammunition, enchantments and flight. This class only mirrors results. */
public final class WeaponRuntime {
    private static final Map<UUID, Projectile> PROJECTILES = new LinkedHashMap<>();
    private static final Map<UUID, net.minecraft.world.entity.LightningBolt> BOLTS = new LinkedHashMap<>();
    private static final Map<net.minecraft.core.BlockPos, Attribution> FIRE = new LinkedHashMap<>();
    private record Snapshot(Vec3 position, float yaw, float pitch, long tick) { }
    private static final Map<UUID, Snapshot> SNAPSHOTS = new LinkedHashMap<>();
    private static long ticks;
    private static int diagnosticBudget;
    private static boolean sentSpin;
    private static final ThreadLocal<java.util.ArrayDeque<net.minecraft.world.entity.LightningBolt>> LIGHTNING =
            ThreadLocal.withInitial(java.util.ArrayDeque::new);
    private record Attribution(String id, String kind) { }
    private static final Map<Integer, Attribution> EFFECT_SOURCES = new LinkedHashMap<>();
    private static ServerPlayer owner;
    private static String sid = "";
    private static Consumer<JsonObject> reliable = message -> { };
    private static Consumer<JsonObject> states = message -> { };

    private WeaponRuntime() { }
    public static void begin(String session, ServerPlayer player, Consumer<JsonObject> reliableSink, Consumer<JsonObject> stateSink) {
        clear(); sid = session; owner = player; reliable = reliableSink; states = stateSink;
        NativeProjectileTicks.begin(player.level());
    }
    public static String kind(ItemStack stack) {
        if (stack.isEmpty()) return "none";
        if (stack.is(Items.BOW)) return "bow";
        if (stack.is(Items.CROSSBOW)) return "crossbow";
        if (stack.is(Items.TRIDENT)) return "trident";
        if (stack.has(DataComponents.PIERCING_WEAPON) || stack.has(DataComponents.KINETIC_WEAPON)) return "spear";
        return "melee";
    }
    private static String kind(Projectile projectile) {
        return projectile instanceof ThrownTrident ? "trident" : projectile instanceof FireworkRocketEntity ? "firework"
                : projectile instanceof net.minecraft.world.entity.projectile.arrow.SpectralArrow ? "spectral_arrow"
                : projectile instanceof net.minecraft.world.entity.projectile.arrow.Arrow arrow && arrow.getColor()!=-1 ? "tipped_arrow" : "arrow";
    }
    public static boolean ownsProjectile(Entity entity) { return PROJECTILES.get(entity.getUUID()) == entity; }
    static boolean isOwnedProjectileCandidate(Entity entity) {
        // Only genuine player-owned weapons bypass the regular native-entity capacity,
        // preserving their real thrown stacks even when a session already owns 1024 entities.
        return owner != null && owner.level() == entity.level() && entity instanceof Projectile projectile
                && projectile.getOwner() == owner
                && (projectile instanceof AbstractArrow || projectile instanceof FireworkRocketEntity);
    }
    private static boolean isOwnedDispenserProjectile(Entity entity) {
        // A null owner is vanilla dispenser behavior, but is never sufficient ownership evidence.
        return owner != null && owner.level() == entity.level() && entity instanceof AbstractArrow arrow
                && arrow.getOwner() == null && entity.level() instanceof ServerLevel level
                && NativeWorldGuard.isOwnedBlockSpawn(level);
    }
    static boolean canLaunch(ItemStack stack) {
        String kind = kind(stack);
        return !kind.equals("bow") && !kind.equals("crossbow") && !kind.equals("trident") || PROJECTILES.size() < 240;
    }
    public static boolean ownsAttacker(Entity entity) { return owner != null && entity == owner; }
    public static boolean ownsLightning(net.minecraft.world.entity.LightningBolt bolt) {
        return owner != null && bolt.getCause() == owner && NativeWorldGuard.ownsEntity(bolt);
    }
    public static void beginLightning(net.minecraft.world.entity.LightningBolt bolt) { LIGHTNING.get().push(bolt); }
    public static void endLightning() { LIGHTNING.get().pop(); }
    public static boolean lightningActive() { return !LIGHTNING.get().isEmpty() && ownsLightning(LIGHTNING.get().peek()); }
    public static void observeLightning(net.minecraft.world.entity.LightningBolt bolt) {
        if(ownsLightning(bolt) && BOLTS.putIfAbsent(bolt.getUUID(),bolt)==null)publishLightning(bolt,true);
    }
    public static boolean ownsFire(net.minecraft.world.level.Level level,net.minecraft.core.BlockPos pos) {
        return owner!=null&&level==owner.level()&&FIRE.containsKey(pos);
    }
    static void fireChanged(net.minecraft.world.level.Level level,net.minecraft.core.BlockPos pos,
                            net.minecraft.world.level.block.state.BlockState state,net.minecraft.core.BlockPos origin) {
        if(owner==null||level!=owner.level())return;
        if(!state.is(net.minecraft.world.level.block.Blocks.FIRE)&&!state.is(net.minecraft.world.level.block.Blocks.SOUL_FIRE)){FIRE.remove(pos);return;}
        Attribution source=lightningActive()?new Attribution(LIGHTNING.get().peek().getUUID().toString(),"trident"):FIRE.get(origin);
        if(source!=null)FIRE.put(pos.immutable(),source);
    }
    public static void fireContact(Entity entity,net.minecraft.core.BlockPos pos) {
        if(entity instanceof BridgeTargetEntity target) {
            Attribution source=FIRE.get(pos);if(source!=null)EFFECT_SOURCES.put(target.actorId(),source);
        }
    }
    public static boolean allowedTarget(Entity entity) { return entity instanceof BridgeTargetEntity || NativeWorldGuard.ownsEntity(entity); }
    static void retireActor(int actorId) { EFFECT_SOURCES.remove(actorId); }
    static void added(ServerLevel level, Entity entity) {
        if(owner != null && owner.level()==level && entity instanceof net.minecraft.world.entity.LightningBolt bolt && bolt.getCause()==owner) {
            BOLTS.put(bolt.getUUID(),bolt);publishLightning(bolt,true);return;
        }
        if ((!isOwnedProjectileCandidate(entity) && !isOwnedDispenserProjectile(entity))
                || !NativeWorldGuard.ownsEntity(entity)) return;
        Projectile projectile = (Projectile) entity;
        if (PROJECTILES.putIfAbsent(projectile.getUUID(), projectile) == null) {
            NativeProjectileTicks.added(projectile);
            publish(projectile, true);
            if (diagnosticBudget++ < 32) BridgeRuntime.log("Native projectile spawn source="
                    + (projectile.getOwner() == owner ? "shot" : "dispenser") + " id=" + projectile.getUUID()
                    + " kind=" + kind(projectile) + " position=" + projectile.position()
                    + " velocity=" + projectile.getDeltaMovement());
        }
    }
    static boolean captures(int actorId, DamageSource source) {
        if (owner == null) return false;
        if(source.is(net.minecraft.world.damagesource.DamageTypes.LIGHTNING_BOLT)
                && !LIGHTNING.get().isEmpty() && ownsLightning(LIGHTNING.get().peek())) return true;
        Entity direct = source.getDirectEntity();
        // Actual session-owned dispenser arrows carry no player as their damage-source entity.
        if (direct instanceof Projectile projectile && ownsProjectile(projectile)) return true;
        if (source.getEntity() == owner) {
            if (direct instanceof Projectile) return false;
            ItemStack weapon = source.getWeaponItem();
            return "spear".equals(kind(weapon == null ? owner.getMainHandItem() : weapon)) || owner.isAutoSpinAttack();
        }
        // Native Flame/tipped-arrow effects keep ticking on the living proxy after the projectile hit.
        return EFFECT_SOURCES.containsKey(actorId) && (source.is(net.minecraft.tags.DamageTypeTags.IS_FIRE)
                || source.is(net.minecraft.world.damagesource.DamageTypes.MAGIC)
                || source.is(net.minecraft.world.damagesource.DamageTypes.WITHER));
    }
    static void captureDamage(int actorId, DamageSource source, float damage) {
        if (!captures(actorId, source) || !Float.isFinite(damage) || damage <= 0) return;
        Entity direct = source.getDirectEntity();
        Attribution attribution = source.is(net.minecraft.world.damagesource.DamageTypes.LIGHTNING_BOLT) && !LIGHTNING.get().isEmpty()
                ? new Attribution(LIGHTNING.get().peek().getUUID().toString(), "trident")
                : direct instanceof Projectile projectile ? new Attribution(direct.getUUID().toString(), kind(projectile))
                : source.getEntity() == owner ? new Attribution(owner.getUUID().toString(), owner.isAutoSpinAttack() ? "trident" : "spear")
                : EFFECT_SOURCES.get(actorId);
        if (attribution == null) return;
        if (direct instanceof Projectile || source.is(net.minecraft.world.damagesource.DamageTypes.LIGHTNING_BOLT)) EFFECT_SOURCES.put(actorId, attribution);
        float converted = damage * (float)MycraftBridge.CONFIG.mcToBlDamageFactor;
        if (!Float.isFinite(converted) || converted <= 0) return;
        JsonObject hit = Proto.msg(sid, 0, "weapon_hit");
        hit.addProperty("eventId", UUID.randomUUID().toString()); hit.addProperty("sourceId", attribution.id());
        hit.addProperty("eid", actorId); hit.addProperty("damage", converted);
        hit.addProperty("kind", attribution.kind());
        reliable.accept(hit);
        if (diagnosticBudget++ < 32) BridgeRuntime.log("Native projectile/weapon hit sourceId=" + attribution.id()
                + " actor=" + actorId + " kind=" + attribution.kind() + " nativeDamage=" + damage
                + " hostDamage=" + converted);
    }
    public static void tick() {
        ticks++;
        if (owner != null) NativeProjectileTicks.tick(PROJECTILES.values(), owner.position());
        var bolts=BOLTS.values().iterator();
        while(bolts.hasNext()) {
            var bolt=bolts.next();boolean alive=!bolt.isRemoved();publishLightning(bolt,alive);if(!alive)bolts.remove();
        }
        if(owner != null && (owner.isAutoSpinAttack() || sentSpin)) {
            sentSpin=owner.isAutoSpinAttack();
            Vec3 motion=sentSpin?owner.getDeltaMovement():Vec3.ZERO;
            JsonObject state=Proto.msg(sid,0,"player_motion");state.addProperty("spinning",sentSpin);
            state.addProperty("vx",motion.x);state.addProperty("vy",motion.y);state.addProperty("vz",motion.z);
            state.addProperty("nativeTick",owner.tickCount);states.accept(state);
        }
        var updates = new java.util.ArrayList<>(PROJECTILES.values());
        updates.sort(java.util.Comparator.comparingLong(projectile -> {
            Snapshot previous=SNAPSHOTS.get(projectile.getUUID());
            return previous==null ? Long.MIN_VALUE : previous.tick();
        }));
        int budget=64;
        for (Projectile projectile : updates) {
            boolean alive = !projectile.isRemoved() && owner != null && projectile.level() == owner.level();
            Snapshot previous = SNAPSHOTS.get(projectile.getUUID());
            boolean changed = previous == null || previous.position().distanceToSqr(projectile.position()) > 1e-8
                    || previous.yaw() != projectile.getYRot() || previous.pitch() != projectile.getXRot();
            if (alive && !changed && ticks-previous.tick()<20) continue;
            if(budget--<=0)break;
            publish(projectile, alive);
            if (!alive) {
                PROJECTILES.remove(projectile.getUUID()); SNAPSHOTS.remove(projectile.getUUID());
                NativeProjectileTicks.retire(projectile.getUUID());
            }
        }
    }
    private static void publish(Projectile projectile, boolean alive) {
        JsonObject state = Proto.msg(sid, 0, "projectile_state");
        state.addProperty("entityId", projectile.getUUID().toString()); state.addProperty("kind", kind(projectile));
        state.addProperty("x", projectile.getX()); state.addProperty("y", projectile.getY()); state.addProperty("z", projectile.getZ());
        Vec3 velocity = projectile.getDeltaMovement();
        state.addProperty("vx", velocity.x); state.addProperty("vy", velocity.y); state.addProperty("vz", velocity.z);
        state.addProperty("nativeTick", projectile.tickCount);
        state.addProperty("yaw", projectile.getYRot()); state.addProperty("pitch", projectile.getXRot()); state.addProperty("alive", alive);
        state.addProperty("color",projectile instanceof net.minecraft.world.entity.projectile.arrow.Arrow arrow?arrow.getColor():-1);
        states.accept(state);
        if(alive)SNAPSHOTS.put(projectile.getUUID(),new Snapshot(projectile.position(),projectile.getYRot(),projectile.getXRot(),ticks));
    }
    private static void publishLightning(net.minecraft.world.entity.LightningBolt bolt,boolean alive) {
        JsonObject state=Proto.msg(sid,0,"lightning_state");state.addProperty("entityId",bolt.getUUID().toString());
        state.addProperty("x",bolt.getX());state.addProperty("y",bolt.getY());state.addProperty("z",bolt.getZ());
        state.addProperty("seed",String.format(java.util.Locale.ROOT,"%016x",bolt.seed));
        state.addProperty("visible",alive && ((LightningStateAccess)bolt).mycraft$lightningLife()>=0);
        state.addProperty("alive",alive);states.accept(state);
    }
    static void addPlayerState(JsonObject state, ServerPlayer player) {
        ItemStack main = player.getMainHandItem(), off = player.getOffhandItem();
        state.addProperty("eyeHeight", player.getEyeHeight());
        state.addProperty("weaponKind", kind(main)); state.addProperty("offhandWeaponKind", kind(off));
        int useTicks = player.isUsingItem() ? player.getTicksUsingItem() : 0;
        state.addProperty("useTicks", Math.clamp(useTicks, 0, 72000));
        ItemStack active = player.isUsingItem() ? player.getUseItem() : main.is(Items.CROSSBOW) ? main : off;
        state.addProperty("crossbowCharge", active.is(Items.CROSSBOW) && player.isUsingItem()
                ? Math.clamp((float)useTicks / Math.max(1, CrossbowItem.getChargeDuration(active, player)), 0, 1) : 0);
        addCrossbowState(state, main, "crossbow"); addCrossbowState(state, off, "offhandCrossbow");
    }
    private static void addCrossbowState(JsonObject state, ItemStack stack, String prefix) {
        boolean loaded = stack.is(Items.CROSSBOW) && CrossbowItem.isCharged(stack);
        state.addProperty(prefix + "Loaded", loaded);
        var ammunition = stack.get(DataComponents.CHARGED_PROJECTILES);
        state.addProperty(prefix + "Firework", loaded && ammunition != null && ammunition.contains(Items.FIREWORK_ROCKET));
    }
    /** Preserve the original thrown stack/entity when returning from the staging region. */
    public static void preservePickupProjectiles(ServerLevel returnLevel, Vec3 position) {
        if (owner == null) return;
        var iterator = PROJECTILES.values().iterator();
        while (iterator.hasNext()) {
            Projectile projectile = iterator.next();
            if (!(projectile instanceof AbstractArrow arrow) || arrow.isRemoved()
                    || arrow.pickup != AbstractArrow.Pickup.ALLOWED) continue;
            try {
                if (returnLevel == owner.level() && position != null && Double.isFinite(position.x)
                        && Double.isFinite(position.y) && Double.isFinite(position.z)) {
                    returnLevel.getChunkAt(net.minecraft.core.BlockPos.containing(position));
                    projectile.teleportTo(position.x, position.y, position.z);
                } else BridgeRuntime.log("Native pickup projectile retained at original position: return world unavailable");
            } catch (RuntimeException failure) {
                BridgeRuntime.log("Native pickup projectile retained after return failure: " + failure.getMessage());
            }
            // A failed native move never authorizes deleting the genuine thrown weapon/arrow.
            publish(projectile, false); NativeWorldGuard.preserveEntity(projectile); iterator.remove();
            SNAPSHOTS.remove(projectile.getUUID());
            NativeProjectileTicks.retire(projectile.getUUID());
        }
    }
    public static void clear() {
        NativeProjectileTicks.clear();
        for(var bolt:BOLTS.values()){publishLightning(bolt,false);bolt.discard();}BOLTS.clear();
        for (Projectile projectile : PROJECTILES.values()) { publish(projectile, false); projectile.discard(); }
        PROJECTILES.clear(); SNAPSHOTS.clear(); FIRE.clear(); EFFECT_SOURCES.clear(); LIGHTNING.remove(); ticks=0; diagnosticBudget=0; sentSpin=false; owner = null; sid = "";
        reliable = message -> { }; states = message -> { };
    }
}
