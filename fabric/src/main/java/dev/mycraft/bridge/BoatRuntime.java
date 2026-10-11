package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.vehicle.boat.AbstractBoat;
import net.minecraft.world.entity.vehicle.boat.AbstractChestBoat;
import net.minecraft.world.item.BoatItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.Consumer;

/** Mirrors actual boats accepted by owned vanilla item use, including rafts and chest boats. */
public final class BoatRuntime {
    private static final int CAPACITY = 16;
    private static final Map<UUID, AbstractBoat> BOATS = new LinkedHashMap<>();
    private static ServerPlayer owner;
    private static String sid = "";
    private static Consumer<JsonObject> reliable = message -> { }, states = message -> { };
    private BoatRuntime() { }
    public static void begin(String session, ServerPlayer player, Consumer<JsonObject> reliableSink, Consumer<JsonObject> stateSink) {
        clear(); sid = session; owner = player; reliable = reliableSink; states = stateSink;
        OwnedEntityTicks.begin(player.level());
    }
    public static boolean canAdd(Entity entity) {
        if (!(entity instanceof AbstractBoat)) return true;
        return owner != null && entity.level() == owner.level() && (BOATS.containsKey(entity.getUUID()) || activeCount() < CAPACITY);
    }
    public static boolean canUseBoat(ServerPlayer player, ItemStack stack) {
        return !(stack.getItem() instanceof BoatItem) || player == owner && activeCount() < CAPACITY
                && NativeWorldGuard.hasOwnedEntityCapacity(player.level());
    }
    public static boolean owns(Entity entity) {
        return entity instanceof AbstractBoat && owner != null && entity.level() == owner.level()
                && !entity.isRemoved() && BOATS.get(entity.getUUID()) == entity && NativeWorldGuard.ownsEntity(entity);
    }
    public static boolean canUseItem(ServerPlayer player, ItemStack stack) { return canUseBoat(player, stack); }
    private static int activeCount() { int count = 0; for (var boat : BOATS.values()) if (!boat.isRemoved()) count++; return count; }
    public static void added(ServerLevel level, Entity entity) {
        if (!(entity instanceof AbstractBoat boat) || owner == null || owner.level() != level
                || !NativeWorldGuard.ownsEntity(boat) || !canAdd(boat)) return;
        if (BOATS.putIfAbsent(boat.getUUID(), boat) != null) return;
        OwnedEntityTicks.added(boat); publish(boat);
        BridgeRuntime.log("Native owned boat id=" + boat.getUUID() + " kind=" + BuiltInRegistries.ENTITY_TYPE.getKey(boat.getType()));
    }
    public static void tick() {
        if (owner == null) return;
        for (var boat : new ArrayList<>(BOATS.values())) {
            if (boat.isRemoved() || boat.level() != owner.level()) {
                JsonObject removed = Proto.msg(sid, 0, "boat_remove");
                removed.addProperty("eventId", UUID.randomUUID().toString()); removed.addProperty("entityId", boat.getUUID().toString());
                removed.addProperty("reason", "native_removed"); reliable.accept(removed);
                BOATS.remove(boat.getUUID()); OwnedEntityTicks.retire(boat.getUUID());
            } else publish(boat);
        }
    }
    private static void publish(AbstractBoat boat) {
        JsonObject state = Proto.msg(sid, 0, "boat_state");
        state.addProperty("entityId", boat.getUUID().toString());
        state.addProperty("kind", BuiltInRegistries.ENTITY_TYPE.getKey(boat.getType()).getPath());
        state.addProperty("x", boat.getX()); state.addProperty("y", boat.getY()); state.addProperty("z", boat.getZ());
        state.addProperty("yaw", boat.getYRot()); state.addProperty("pitch", boat.getXRot());
        state.addProperty("nativeTick", boat.tickCount); state.addProperty("alive", !boat.isRemoved());
        state.addProperty("leftPaddle", boat.getRowingTime(AbstractBoat.PADDLE_LEFT, 1.0F));
        state.addProperty("rightPaddle", boat.getRowingTime(AbstractBoat.PADDLE_RIGHT, 1.0F));
        state.addProperty("hurtTime", Math.clamp(boat.getHurtTime(), 0, 200)); state.addProperty("hurtDir", boat.getHurtDir());
        state.addProperty("damage", boat.getDamage()); state.addProperty("bubbleAngle", boat.getBubbleAngle(1));
        state.addProperty("underWater", boat.isUnderWater()); states.accept(state);
    }
    /** Moves the original entity and chest contents; no replacement inventory or boat item is fabricated. */
    public static void preserve(ServerLevel returnLevel, Vec3 returnPosition) {
        for (var boat : BOATS.values()) {
            if (boat.isRemoved()) continue;
            if (returnLevel == boat.level() && returnPosition != null && finite(returnPosition)) {
                try {
                    returnLevel.getChunkAt(BlockPos.containing(returnPosition));
                    boat.ejectPassengers();
                    boat.teleportTo(returnPosition.x, returnPosition.y, returnPosition.z);
                } catch (RuntimeException failure) { BridgeRuntime.log("Native boat retained after return movement failed: " + boat.getUUID()); }
            }
            NativeWorldGuard.preserveEntity(boat);
            OwnedEntityTicks.retire(boat.getUUID());
        }
    }
    private static boolean finite(Vec3 pos) { return Double.isFinite(pos.x) && Double.isFinite(pos.y) && Double.isFinite(pos.z); }
    public static void clear() {
        for (var boat : BOATS.values()) {
            OwnedEntityTicks.retire(boat.getUUID());
            // Missing return-world information must never delete the player's real chest inventory.
            if (!boat.isRemoved() && boat instanceof AbstractChestBoat) NativeWorldGuard.preserveEntity(boat);
        }
        BOATS.clear(); owner = null; sid = ""; reliable = message -> { }; states = message -> { };
    }
}
