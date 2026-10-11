package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.util.Set;

/** The limited demo policy stays independent of world and registry bootstrap. */
final class BuildingRules {
    static final int MAX_BLOCKS = 256;
    static final Set<String> BLOCK_ITEMS = Set.of("minecraft:stone", "minecraft:dirt",
            "minecraft:cobblestone", "minecraft:oak_planks", "minecraft:bricks");
    static final Set<String> CREATIVE_ITEMS = Set.of("minecraft:stone", "minecraft:dirt",
            "minecraft:cobblestone", "minecraft:oak_planks", "minecraft:bricks",
            "minecraft:diamond_sword", "minecraft:shield");

    record Request(String id, int x, int y, int z, String action) { }

    static Request read(JsonObject message) {
        String id = requestId(message);
        int x = coordinate(message, "x"), y = coordinate(message, "y"), z = coordinate(message, "z");
        String action = Proto.string(message, "action");
        if (!action.equals("place") && !action.equals("break"))
            throw new IllegalArgumentException("action must be place or break");
        return new Request(id, x, y, z, action);
    }

    static String requestId(JsonObject message) {
        String id = Proto.string(message, "requestId");
        if (id.isBlank() || id.length() > 128) throw new IllegalArgumentException("invalid requestId");
        return id;
    }

    static int coordinate(JsonObject message, String key) {
        double value = Proto.number(message, key);
        if (value != Math.rint(value) || Math.abs(value) > 29_999_984)
            throw new IllegalArgumentException("block coordinate outside bounds: " + key);
        if (key.equals("y") && (value < -2048 || value > 2048))
            throw new IllegalArgumentException("block height outside demo bounds");
        return (int) value;
    }

    static boolean inRange(Request request, double x, double y, double z) {
        double dx = request.x + 0.5 - x, dy = request.y + 0.5 - y, dz = request.z + 0.5 - z;
        return Double.isFinite(dx) && Double.isFinite(dy) && Double.isFinite(dz)
                && dx * dx + dy * dy + dz * dz <= 36;
    }

    static int inventorySlot(JsonObject message, String key) {
        long slot = Proto.integer(message, key);
        if (slot > 35 && slot != 40) throw new IllegalArgumentException("slot must be main inventory or offhand");
        return (int) slot;
    }
}
