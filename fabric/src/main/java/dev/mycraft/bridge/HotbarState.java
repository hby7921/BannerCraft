package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.util.List;

/** Immutable server inventory snapshot; the wire extension stays flat for demo-v1 readers. */
public record HotbarState(int selectedSlot, int food, int armor, List<Slot> slots,
                          String offhandItem, int offhandCount) {
    public record Slot(String item, int count) {
        public Slot {
            if (item == null || !item.matches("[a-z0-9_.-]+:[a-z0-9/._-]+") || count < 0)
                throw new IllegalArgumentException("invalid hotbar item");
            if (item.equals("minecraft:air") != (count == 0))
                throw new IllegalArgumentException("empty hotbar slot must have air and zero count");
        }
    }

    public HotbarState {
        if (selectedSlot < 0 || selectedSlot > 8 || food < 0 || food > 20 || armor < 0 || armor > 20)
            throw new IllegalArgumentException("invalid player display state");
        slots = List.copyOf(slots);
        if (slots.size() != 9) throw new IllegalArgumentException("nine hotbar slots required");
        new Slot(offhandItem, offhandCount);
    }

    public HotbarState(int selectedSlot, int food, int armor, List<Slot> slots) {
        this(selectedSlot, food, armor, slots, "minecraft:air", 0);
    }

    public void appendTo(JsonObject message) {
        message.addProperty("selectedSlot", selectedSlot);
        message.addProperty("food", food);
        message.addProperty("armor", armor);
        message.addProperty("offhandItem", offhandItem);
        message.addProperty("offhandCount", offhandCount);
        for (int i = 0; i < slots.size(); i++) {
            message.addProperty("slot" + i + "Item", slots.get(i).item());
            message.addProperty("slot" + i + "Count", slots.get(i).count());
        }
    }
}
