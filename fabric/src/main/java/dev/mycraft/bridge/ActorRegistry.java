package dev.mycraft.bridge;

import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.Consumer;
import java.util.function.Supplier;

/** Session-local actor ownership. All access belongs to the Minecraft server thread. */
final class ActorRegistry<T> {
    static final int CAPACITY = 64;
    private final Map<Integer, T> actors = new LinkedHashMap<>();
    private final Consumer<T> discard;
    private int capacity=CAPACITY;

    ActorRegistry(Consumer<T> discard) { this.discard = discard; }

    static int checkedId(long eid) {
        if (eid < 1 || eid > Integer.MAX_VALUE)
            throw new IllegalArgumentException("actor eid must be a positive int");
        return (int) eid;
    }

    T getOrCreate(int eid, Supplier<T> factory) {
        checkedId(eid);
        T existing = actors.get(eid);
        if (existing != null) return existing;
        if (actors.size() >= capacity) throw new IllegalArgumentException("actor capacity exceeded");
        T created = factory.get();
        if (created == null) throw new IllegalArgumentException("proxy spawn failed");
        actors.put(eid, created);
        return created;
    }

    T get(int eid) { return actors.get(eid); }
    Collection<T> values() { return actors.values(); }
    void setCapacity(int limit) {
        if(limit!=64 && limit!=256 && limit!=640 || !actors.isEmpty())throw new IllegalArgumentException("invalid session actor capacity");
        capacity=limit;
    }

    void remove(int eid) {
        checkedId(eid);
        T removed = actors.remove(eid);
        if (removed != null) discard.accept(removed);
    }

    void clear() {
        for (T actor : actors.values()) discard.accept(actor);
        actors.clear();
    }
}
