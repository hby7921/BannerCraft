package dev.mycraft.bridge;

import java.util.LinkedHashMap;

/** Bounded retry outcomes shared by inventory and construction, scoped to one bridge session. */
final class RequestHistory<T> {
    private final int capacity;
    private final LinkedHashMap<String, T> outcomes = new LinkedHashMap<>();
    RequestHistory(int capacity) {
        if (capacity < 1) throw new IllegalArgumentException("positive history capacity required");
        this.capacity = capacity;
    }
    T get(String id) { return outcomes.get(id); }
    boolean canAccept(String id) { return outcomes.containsKey(id) || outcomes.size() < capacity; }
    void put(String id, T outcome) {
        if (id == null || id.isBlank() || outcome == null) throw new IllegalArgumentException("request outcome required");
        if (!canAccept(id)) throw new IllegalArgumentException("request history full");
        outcomes.putIfAbsent(id, outcome);
    }
    void clear() { outcomes.clear(); }
}
