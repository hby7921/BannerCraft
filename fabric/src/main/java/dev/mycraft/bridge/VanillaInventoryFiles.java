package dev.mycraft.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

/** Session-local transport. Never accepts a path supplied by the host or a packet. */
final class VanillaInventoryFiles {
    static final int MAGIC = 0x3149434d, HEADER = 72, MAX_WIDTH = 1280, MAX_HEIGHT = 720;
    static final int MAX_INPUT_BYTES = 131072, MAX_EVENTS = 256;
    static final long LEASE_MS = 3000;
    record Packet(String token, boolean open, long time, JsonArray events) {}

    static boolean validId(String id) { return id != null && id.matches("[0-9a-f]{32}"); }
    static Path directory(String session) {
        if (!validId(session)) throw new IllegalArgumentException("Invalid inventory session");
        String local = System.getenv("LOCALAPPDATA");
        if (local == null || local.isBlank()) throw new IllegalStateException("Local inventory transport requires LOCALAPPDATA");
        return Path.of(local, "Mycraft", "vanilla-ui", session);
    }
    static Packet read(Path directory, String session, long now) throws IOException {
        Path input = directory.resolve("input.json");
        if (!Files.isRegularFile(input) || Files.size(input) > MAX_INPUT_BYTES) return null;
        // Read through a bounded stream, including if a file is replaced between stat and open.
        byte[] bytes;
        try (var stream = Files.newInputStream(input)) { bytes = stream.readNBytes(MAX_INPUT_BYTES + 1); }
        if (bytes.length > MAX_INPUT_BYTES) return null;
        JsonObject root = JsonParser.parseString(new String(bytes, StandardCharsets.UTF_8)).getAsJsonObject();
        if (!session.equals(root.get("sid").getAsString())) return null;
        String token = root.get("token").getAsString();
        long time = root.get("time").getAsLong();
        if (!validId(token) || time < now - LEASE_MS || time > now + 1000) return null;
        JsonArray events = root.getAsJsonArray("events");
        if (events == null || events.size() > MAX_EVENTS) return null;
        return new Packet(token, root.get("open").getAsBoolean(), time, events);
    }
    static void publish(Path path, byte[] bytes) throws IOException {
        Files.createDirectories(path.getParent());
        Path temp = path.resolveSibling(path.getFileName() + ".tmp");
        Files.write(temp, bytes);
        Files.move(temp, path, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
    }
    private VanillaInventoryFiles() {}
}
