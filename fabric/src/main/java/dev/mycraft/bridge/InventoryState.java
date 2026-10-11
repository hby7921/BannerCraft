package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.util.List;

/** Detached display data; items with their components remain in Minecraft. */
record InventoryState(boolean creative, int selectedSlot, List<HotbarState.Slot> inventory,
                      List<HotbarState.Slot> armor, HotbarState.Slot offhand) {
    InventoryState {
        inventory = List.copyOf(inventory);
        armor = List.copyOf(armor);
        if (selectedSlot < 0 || selectedSlot > 8 || inventory.size() != 36 || armor.size() != 4)
            throw new IllegalArgumentException("invalid inventory snapshot");
        java.util.Objects.requireNonNull(offhand);
    }

    JsonObject message(String sid, int revision) {
        if (revision < 1) throw new IllegalArgumentException("positive inventory revision required");
        JsonObject message = Proto.msg(sid, 0, "inventory_state");
        message.addProperty("rev", revision);
        message.addProperty("creative", creative);
        message.addProperty("selectedSlot", selectedSlot);
        for (int slot = 0; slot < 36; slot++) append(message, "inv" + slot, inventory.get(slot));
        for (int slot = 0; slot < 4; slot++) append(message, "armor" + slot, armor.get(slot));
        append(message, "offhand", offhand);
        return message;
    }

    private static void append(JsonObject message, String key, HotbarState.Slot slot) {
        message.addProperty(key + "Item", slot.item());
        message.addProperty(key + "Count", slot.count());
    }
}
