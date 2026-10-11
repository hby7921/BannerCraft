package dev.mycraft.bridge;

import java.util.LinkedHashMap;
import java.util.function.LongSupplier;

final class BoundedIds {
    private final int capacity;
    private final long retentionNanos;
    private final LongSupplier clock;
    private final LinkedHashMap<String, Long> ids = new LinkedHashMap<>();
    BoundedIds(int capacity) { this(capacity, 10_000_000_000L, System::nanoTime); }
    BoundedIds(int capacity, long retentionNanos, LongSupplier clock) {
        if (capacity < 1 || retentionNanos <= 0) throw new IllegalArgumentException("positive retry-cache limits required");
        this.capacity = capacity;
        this.retentionNanos = retentionNanos;
        this.clock = clock;
    }
    boolean add(String id) {
        if (id == null || id.isBlank()) return false;
        long now = clock.getAsLong();
        ids.entrySet().removeIf(entry -> now - entry.getValue() >= retentionNanos);
        if (ids.containsKey(id)) return false;
        // Never evict a still-retryable event merely to accept another event.
        if (ids.size() >= capacity) throw new IllegalArgumentException("retry cache capacity exceeded");
        ids.put(id, now);
        return true;
    }
    void clear() { ids.clear(); }
}
