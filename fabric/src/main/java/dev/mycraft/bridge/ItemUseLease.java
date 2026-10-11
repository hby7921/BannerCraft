package dev.mycraft.bridge;

/** A held input is renewed without restarting vanilla's shield warm-up timer. */
final class ItemUseLease {
    static final long TIMEOUT_NS = 750_000_000L;
    private boolean pressed;
    private boolean requireRelease;
    private long refreshed;

    boolean update(boolean value, long now) {
        if (!value) { clear(); return false; }
        if (requireRelease) return false;
        boolean start = !pressed;
        pressed = true;
        refreshed = now;
        return start;
    }

    boolean expired(long now) { return pressed && now - refreshed >= TIMEOUT_NS; }
    boolean held() { return pressed; }
    void cancelUntilRelease() { pressed = false; requireRelease = true; }
    void clear() { pressed = false; requireRelease = false; refreshed = 0; }
}
