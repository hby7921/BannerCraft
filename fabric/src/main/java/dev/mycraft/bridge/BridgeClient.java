package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.BufferedWriter;
import java.io.OutputStreamWriter;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;

/** One connection owns its socket, queue and sequence; an old IO thread cannot close a reconnect. */
public final class BridgeClient {
    private static Connection current;
    private static long generation;

    private static final class Connection {
        final Socket socket;
        final long generation;
        final ArrayDeque<JsonObject> outbound = new ArrayDeque<>();
        long seq;
        volatile boolean sessionOpened;
        volatile String closeAfterFlush;
        Connection(Socket socket, long generation) {
            this.socket = socket;
            this.generation = generation;
        }
    }

    private BridgeClient() { }

    public static synchronized boolean connect(String host, int port) {
        disconnect("reconnect requested");
        Socket socket = new Socket();
        try {
            InetAddress address = InetAddress.getByName(host);
            if (!address.isLoopbackAddress()) throw new IllegalArgumentException("loopback address required");
            socket.connect(new InetSocketAddress(address, port), 3000);
            socket.setTcpNoDelay(true);
            socket.setSoTimeout(10);
            Connection connection = new Connection(socket, ++generation);
            current = connection;
            BridgeRuntime.onBridgeConnected(connection.generation);
            Thread io = new Thread(() -> ioLoop(connection), "MycraftBridge.IO");
            io.setDaemon(true);
            io.start();
            return true;
        } catch (Exception e) {
            try { socket.close(); } catch (Exception ignored) { }
            BridgeRuntime.log("connect failed: " + e.getMessage());
            return false;
        }
    }

    public static synchronized void disconnect(String reason) {
        if (current != null) closeConnection(current, reason);
    }

    private static synchronized void closeConnection(Connection connection, String reason) {
        try { connection.socket.close(); } catch (Exception ignored) { }
        if (current != connection) return;
        current = null;
        connection.outbound.clear();
        BridgeRuntime.onBridgeClosed(connection.generation, reason);
    }

    public static synchronized boolean isConnected() { return current != null; }
    public static synchronized void setSessionOpened(boolean value) {
        if (current != null) current.sessionOpened = value;
    }

    public static synchronized void send(JsonObject message) {
        Connection connection = current;
        if (connection == null || connection.closeAfterFlush != null) return;
        // Six hundred actual actors can publish health states plus reliable ACKs in one volley.
        if (connection.outbound.size() >= 4096) {
            closeConnection(connection, "outbound overflow");
            return;
        }
        JsonObject copy = message.deepCopy();
        copy.addProperty("seq", ++connection.seq);
        connection.outbound.add(copy);
    }

    public static void close(String reason) { disconnect(reason); }

    public static synchronized void rejectProtocol(String reason) {
        Connection connection = current;
        if (connection == null || connection.closeAfterFlush != null) return;
        connection.outbound.clear();
        JsonObject error = Proto.msg(BridgeRuntime.getSessionId(), ++connection.seq, "error");
        error.addProperty("code", "protocol");
        error.addProperty("message", reason);
        connection.outbound.add(error);
        connection.closeAfterFlush = reason;
    }

    private static void ioLoop(Connection connection) {
        try {
            var input = connection.socket.getInputStream();
            var output = new BufferedWriter(new OutputStreamWriter(connection.socket.getOutputStream(), StandardCharsets.UTF_8));
            LineFrames frames = new LineFrames(Proto.MAX_LINE_BYTES);
            long lastRx = System.nanoTime();
            long lastBeat = lastRx;
            byte[] buffer = new byte[8192];
            while (!connection.socket.isClosed()) {
                ArrayList<JsonObject> batch = new ArrayList<>();
                String closeAfterBatch;
                synchronized (BridgeClient.class) {
                    if (current != connection) return;
                    JsonObject message;
                    while ((message = connection.outbound.poll()) != null) batch.add(message);
                    closeAfterBatch = connection.closeAfterFlush;
                }
                // Socket writes may block; disconnect must remain able to close the socket.
                for (JsonObject message : batch) {
                    output.write(message.toString());
                    output.write('\n');
                }
                output.flush();
                if (closeAfterBatch != null) {
                    closeConnection(connection, closeAfterBatch);
                    return;
                }
                try {
                    int count = input.read(buffer);
                    if (count < 0) throw new java.io.EOFException("remote closed");
                    for (String line : frames.accept(buffer, count)) {
                        JsonObject message = JsonParser.parseString(line).getAsJsonObject();
                        lastRx = System.nanoTime();
                        synchronized (BridgeClient.class) {
                            if (current != connection) return;
                            BridgeRuntime.enqueueInbound(connection.generation, message);
                        }
                    }
                } catch (SocketTimeoutException ignored) { }
                long now = System.nanoTime();
                if (connection.sessionOpened && now - lastBeat >= Proto.HEARTBEAT_MS * 1_000_000L) {
                    lastBeat = now;
                    synchronized (BridgeClient.class) {
                        if (current == connection) send(Proto.msg(BridgeRuntime.getSessionId(), 0, "heartbeat"));
                    }
                }
                if (now - lastRx > Proto.TIMEOUT_MS * 1_000_000L) throw new java.io.IOException("heartbeat timeout");
            }
        } catch (Exception e) {
            closeConnection(connection, e.getMessage());
        }
    }

    public static String statusText() {
        return (isConnected() ? "connected" : "disconnected") + ", state=" + BridgeRuntime.getState();
    }
}
