package dev.mycraft.bridge;

import com.google.gson.JsonObject;

/** Wire ordering stays testable without bootstrapping Minecraft registries. */
final class SessionRules {
    enum Result { ACCEPT, STALE_SESSION }
    private long lastSequence;
    private boolean greeted;

    Result accept(JsonObject message, String sessionId) {
        if (!Proto.validateEnvelope(message)) throw new IllegalArgumentException("invalid envelope");
        long sequence = Proto.integer(message, "seq");
        if (sequence <= lastSequence) throw new IllegalArgumentException("sequence must increase");
        lastSequence = sequence;
        String type = Proto.type(message), sid = Proto.sid(message);
        if (type.equals("hello")) {
            if (greeted || !sid.isEmpty()) throw new IllegalArgumentException("unexpected hello");
            greeted = true;
            return Result.ACCEPT;
        }
        if (!greeted) throw new IllegalArgumentException("hello required");
        if (type.equals("begin_session")) {
            if (!sessionId.isEmpty() || sid.isBlank() || !Proto.string(message, "mode").equals("demo_field"))
                throw new IllegalArgumentException("invalid begin_session");
            return Result.ACCEPT;
        }
        if (!sid.equals(sessionId)) return Result.STALE_SESSION;
        if (!type.equals("heartbeat") && !type.equals("error") && sessionId.isEmpty())
            throw new IllegalArgumentException("session required");
        return Result.ACCEPT;
    }

    void reset() { lastSequence = 0; greeted = false; }
}
