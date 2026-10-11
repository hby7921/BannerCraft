package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.inventory.AbstractContainerScreen;
import net.minecraft.client.gui.screens.inventory.CreativeModeInventoryScreen;
import net.minecraft.client.gui.screens.inventory.InventoryScreen;
import net.minecraft.client.input.CharacterEvent;
import net.minecraft.client.input.MouseButtonEvent;
import net.minecraft.client.input.MouseButtonInfo;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

/** Renders and operates the real local player's vanilla screen; no duplicate inventory model. */
public final class VanillaInventoryBridge {
    private static final ScheduledExecutorService IO = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread thread = new Thread(r, "Mycraft vanilla inventory files"); thread.setDaemon(true); return thread;
    });
    private static volatile String session = "";
    private static volatile VanillaInventoryFiles.Packet incoming;
    private static volatile Screen ownedScreen;
    private static volatile String token = "";
    private static String finishedToken = "";
    private static long acknowledged, frameNumber, lastCapture, lastClickTime;
    private static long firstScreenFrame = Long.MAX_VALUE;
    private static int lastClickButton = -1;
    private static double mouseX = .5, mouseY = .5;
    private static final AtomicBoolean capturing = new AtomicBoolean();
    private static boolean remoteScreenOperation;
    private static boolean loggedFrame;
    private static volatile String captureFailure;

    public static void initialize() { IO.scheduleWithFixedDelay(VanillaInventoryBridge::poll, 0, 30, TimeUnit.MILLISECONDS); }
    private static void poll() {
        String sid = session;
        if (sid.isEmpty()) return;
        try {
            var packet = VanillaInventoryFiles.read(VanillaInventoryFiles.directory(sid), sid, System.currentTimeMillis());
            if (packet != null && sid.equals(session)) incoming = packet;
        } catch (Exception ignored) { /* An atomic replacement may briefly contend on Windows. Keep the existing lease. */ }
    }
    public static void tick(Minecraft client) {
        try { tickInventory(client); }
        catch (RuntimeException failure) {
            MycraftBridge.LOGGER.warn("[Mycraft] Native inventory failure; closing only its overlay", failure);
            try { close(client); }
            catch (RuntimeException cleanupFailure) { MycraftBridge.LOGGER.warn("[Mycraft] Native inventory cleanup failure", cleanupFailure); }
        }
    }
    private static void tickInventory(Minecraft client) {
        if (captureFailure != null) {
            MycraftBridge.LOGGER.warn("[Mycraft] Native inventory closed locally: {}", captureFailure);
            captureFailure = null;
            close(client);
        }
        String next = BridgeRuntime.getState() == BridgeRuntime.State.ACTIVE ? BridgeRuntime.getSessionId() : "";
        if (!VanillaInventoryFiles.validId(next)) next = "";
        if (!next.equals(session)) {
            close(client); session = next; incoming = null; finishedToken = "";
        }
        var packet = incoming;
        if (session.isEmpty() || client.player == null || client.level == null || packet == null ||
                System.currentTimeMillis() - packet.time() > VanillaInventoryFiles.LEASE_MS) {
            if (ownedScreen != null) close(client);
            return;
        }
        if (!packet.open()) { if (packet.token().equals(token)) close(client); return; }
        if (!packet.token().equals(token) && !packet.token().equals(finishedToken)) {
            close(client);
            Screen existing = client.gui.screen();
            // Native use-on-block may already have opened a crafting table, furnace or other container.
            // Adopt only the exact current menu; never replace chat, pause/settings or death screens.
            if (client.player.isSpectator() || !client.player.isAlive() || existing != null && !canAdopt(client, existing)) {
                finishedToken = packet.token(); publishClosed(packet.token()); return;
            }
            // The menu packet can arrive before its Screen. Let that vanilla transition finish.
            if (existing == null && client.player.containerMenu != client.player.inventoryMenu) return;
            token = packet.token(); acknowledged = 0; lastClickButton = -1;
            if (existing == null) {
                Screen screen = client.player.hasInfiniteMaterials()
                        ? new CreativeModeInventoryScreen(client.player, client.player.connection.enabledFeatures(), client.options.operatorItemsTab().get())
                        : new InventoryScreen(client.player);
                remoteScreenOperation = true;
                try { client.gui.setScreen(screen); }
                finally { remoteScreenOperation = false; }
                existing = client.gui.screen();
            }
            if (!canAdopt(client, existing)) { finishedToken = token; publishClosed(token); token = ""; return; }
            adopt(existing);
        }
        if (ownedScreen == null) return;
        if (!followScreen(client)) return;
        for (var element : packet.events()) {
            long seq;
            try {
                JsonObject event = element.getAsJsonObject();
                seq = event.get("seq").getAsLong();
                if (seq <= acknowledged) continue;
                // Missing input may contain a release or a click: never replay a partial gesture.
                if (seq != acknowledged + 1) { close(client); return; }
                long displayedFrame = event.get("frame").getAsLong();
                // Do not apply gestures made against an older container image to a newly opened menu.
                if (VanillaInventoryScreenPolicy.inputMatchesScreen(displayedFrame, firstScreenFrame, frameNumber)) {
                    remoteScreenOperation = true;
                    try { dispatch(ownedScreen, event); }
                    finally { remoteScreenOperation = false; }
                }
                acknowledged = seq;
            } catch (RuntimeException invalid) { close(client); return; }
            if (!followScreen(client)) return;
        }
    }
    private static boolean canAdopt(Minecraft client, Screen screen) {
        boolean live = client.player != null && client.level != null && client.player.isAlive();
        return VanillaInventoryScreenPolicy.mayAdopt(
                BridgeRuntime.getState() == BridgeRuntime.State.ACTIVE && session.equals(BridgeRuntime.getSessionId()),
                live, client.player != null && client.player.isSpectator(), screen instanceof AbstractContainerScreen<?>,
                live && screen instanceof AbstractContainerScreen<?> container && container.getMenu() == client.player.containerMenu);
    }
    private static void adopt(Screen screen) {
        ownedScreen = screen;
        firstScreenFrame = Long.MAX_VALUE;
        lastClickButton = -1;
        lastClickTime = 0;
        lastCapture = 0;
        loggedFrame = false;
        MycraftBridge.LOGGER.info("[Mycraft] Native inventory adopted {} (GUI {}x{}, windowActive={})",
                screen.getClass().getSimpleName(), screen.width, screen.height, Minecraft.getInstance().isWindowActive());
    }
    private static boolean followScreen(Minecraft client) {
        Screen current = client.gui.screen();
        if (!canAdopt(client, current)) { close(client); return false; }
        if (current != ownedScreen) adopt(current);
        return true;
    }
    private static void dispatch(Screen screen, JsonObject e) {
        String type = e.get("type").getAsString();
        int mods = VanillaInventoryInput.modifiers(e.has("mods") ? e.get("mods").getAsInt() : 0);
        if (e.has("x")) {
            double x = e.get("x").getAsDouble(), y = e.get("y").getAsDouble();
            if (!Double.isFinite(x) || !Double.isFinite(y) || x < -.1 || x > 1.1 || y < -.1 || y > 1.1) throw new IllegalArgumentException("mouse position");
            mouseX = x; mouseY = y;
        }
        double x = mouseX * screen.width, y = mouseY * screen.height;
        switch (type) {
            case "move" -> screen.mouseMoved(x, y);
            case "down", "up", "drag" -> {
                int button = e.get("button").getAsInt();
                if (button < 0 || button > 2) throw new IllegalArgumentException("mouse button");
                var event = new MouseButtonEvent(x, y, new MouseButtonInfo(VanillaInventoryInput.button(button), mods));
                if (type.equals("down")) {
                    long now = System.currentTimeMillis();
                    boolean twice = button == lastClickButton && now - lastClickTime < 250;
                    screen.mouseClicked(event, twice); lastClickTime = now; lastClickButton = button;
                } else if (type.equals("up")) screen.mouseReleased(event);
                else {
                    double dx = e.get("dx").getAsDouble(), dy = e.get("dy").getAsDouble();
                    if (!Double.isFinite(dx) || !Double.isFinite(dy) || Math.abs(dx) > 2 || Math.abs(dy) > 2) throw new IllegalArgumentException("drag delta");
                    screen.mouseDragged(event, dx * screen.width, dy * screen.height);
                }
                screen.afterMouseAction();
            }
            case "scroll" -> {
                double amount = e.get("amount").getAsDouble();
                if (!Double.isFinite(amount) || Math.abs(amount) > 32) throw new IllegalArgumentException("scroll");
                screen.mouseScrolled(x, y, 0, amount);
            }
            case "key", "release" -> {
                int key = e.get("key").getAsInt();
                var event = VanillaInventoryInput.key(key, mods);
                if (type.equals("key")) screen.keyPressed(event); else screen.keyReleased(event);
                screen.afterKeyboardAction();
            }
            case "char" -> {
                int point = e.get("codepoint").getAsInt();
                if (!Character.isValidCodePoint(point) || point < 32 || point >= 0xd800 && point <= 0xdfff) throw new IllegalArgumentException("character");
                screen.charTyped(new CharacterEvent(point));
            }
            default -> throw new IllegalArgumentException("input type");
        }
    }
    public static boolean ownsScreen() {
        return ownedScreen != null && Minecraft.getInstance().gui.screen() == ownedScreen;
    }
    public static double mouseX() { return mouseX * ownedScreen.width; }
    public static double mouseY() { return mouseY * ownedScreen.height; }
    public static boolean suppressBackgroundCursorWarp() {
        Minecraft client = Minecraft.getInstance();
        return !client.isWindowActive() && (remoteScreenOperation || ownsScreen() || canAdopt(client, client.gui.screen()));
    }
    public static void capture() {
        if (!ownsScreen()) { if (!capturing.get()) VanillaInventoryCapture.release(); return; }
        if (System.nanoTime() - lastCapture < 125_000_000L || !capturing.compareAndSet(false, true)) return;
        Minecraft client = Minecraft.getInstance();
        var target = client.gameRenderer.mainRenderTarget();
        lastCapture = System.nanoTime();
        String sid = session, captureToken = token;
        Screen captureScreen = ownedScreen;
        long ack = acknowledged, number = ++frameNumber, timestamp = System.currentTimeMillis();
        if (firstScreenFrame == Long.MAX_VALUE) firstScreenFrame = number;
        try {
            VanillaInventoryCapture.request(target, pixels -> {
                try { IO.execute(() -> {
                try {
                    if (!sid.equals(session) || !captureToken.equals(token) || ownedScreen != captureScreen) return;
                    int w = pixels.width(), h = pixels.height();
                    byte[] bytes = new byte[VanillaInventoryFiles.HEADER + w * h * 4];
                    header(bytes, w, h, timestamp, ack, number, captureToken, 1);
                    VanillaInventoryPixels.copyStraightRgba(pixels.bottomUp(),w,h,bytes,VanillaInventoryFiles.HEADER);
                    if (!sid.equals(session) || !captureToken.equals(token) || ownedScreen != captureScreen) return;
                    VanillaInventoryFiles.publish(VanillaInventoryFiles.directory(sid).resolve("frame.bin"), bytes);
                    if (!loggedFrame) {
                        loggedFrame = true;
                        MycraftBridge.LOGGER.info("[Mycraft] Native inventory first transparent frame {}x{}; bounded GPU readback, 8 Hz", w, h);
                    }
                } catch (Exception e) { MycraftBridge.LOGGER.debug("Vanilla inventory frame unavailable", e); }
                finally { capturing.set(false); }
                }); }
                catch (RejectedExecutionException stopped) { capturing.set(false); }
            }, failure -> { captureFailure = failure.toString(); capturing.set(false); });
        } catch (RuntimeException failure) { captureFailure = failure.toString(); capturing.set(false); }
    }
    private static ByteBuffer header(byte[] bytes, int w, int h, long time, long ack, long number, String token, int flags) {
        return ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(VanillaInventoryFiles.MAGIC).putInt(w).putInt(h)
                .putLong(time).putLong(ack).putLong(number).put(token.getBytes(StandardCharsets.US_ASCII)).putInt(flags);
    }
    private static void publishClosed(String oldToken) {
        String sid = session;
        if (!VanillaInventoryFiles.validId(sid) || !VanillaInventoryFiles.validId(oldToken)) return;
        long ack = acknowledged, number = ++frameNumber;
        IO.execute(() -> {
            try {
                byte[] bytes = new byte[VanillaInventoryFiles.HEADER];
                header(bytes, 0, 0, System.currentTimeMillis(), ack, number, oldToken, 0);
                VanillaInventoryFiles.publish(VanillaInventoryFiles.directory(sid).resolve("frame.bin"), bytes);
            } catch (Exception ignored) {}
        });
    }
    private static void close(Minecraft client) {
        Screen screen = ownedScreen;
        ownedScreen = null;
        if (screen != null) {
            MycraftBridge.LOGGER.info("[Mycraft] Native inventory closing locally: {}; session={}", screen.getClass().getSimpleName(), session);
            if (client.gui.screen() == screen) {
                // onClose sends vanilla ContainerClose and returns the carried stack via vanilla menu teardown.
                // A world disconnect may already have removed the LocalPlayer.
                remoteScreenOperation = true;
                try {
                    if (client.player != null && client.level != null) screen.onClose();
                    // During world teardown Gui.setScreen(null) is illegal; the game's teardown owns that path.
                } finally { remoteScreenOperation = false; }
            }
            finishedToken = token; publishClosed(token);
        }
        token = "";
    }
    public static void shutdown(Minecraft client) { close(client); session = ""; IO.shutdown(); }
    private VanillaInventoryBridge() {}
}
