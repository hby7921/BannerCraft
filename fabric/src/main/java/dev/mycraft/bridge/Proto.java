package dev.mycraft.bridge;

import com.google.gson.JsonObject;

/**
 * demo-v1 message builders/parsers (authority: protocol/demo-v1.md).
 * Gson is provided by the Minecraft runtime.
 */
public final class Proto {
	public static final int PROTOCOL_VERSION = 1;
	public static final int MAX_LINE_BYTES = 65536;
	public static final int HEARTBEAT_MS = 1000;
	public static final int TIMEOUT_MS = 3000;

	private Proto() {
	}

	public static JsonObject msg(String sid, long seq, String type) {
		JsonObject m = new JsonObject();
		m.addProperty("type", type);
		m.addProperty("pv", PROTOCOL_VERSION);
		m.addProperty("sid", sid);
		m.addProperty("seq", seq);
		return m;
	}

	public static JsonObject helloAck(String playerName) {
		JsonObject m = msg("", 0, "hello_ack");
		m.addProperty("agent", "mycraft-fabric");
		m.addProperty("mcVersion", "26.3");
		m.addProperty("playerName", playerName);
		return m;
	}

	public static JsonObject playerState(String sid, long seq, float hp, float maxHp, boolean alive, String item) {
		JsonObject m = msg(sid, seq, "player_state");
		m.addProperty("hp", hp);
		m.addProperty("maxHp", maxHp);
		m.addProperty("alive", alive);
		m.addProperty("item", item);
		return m;
	}

	public static JsonObject playerState(String sid, long seq, float hp, float maxHp, boolean alive, String item, HotbarState hotbar) {
		JsonObject m = playerState(sid, seq, hp, maxHp, alive, item);
		hotbar.appendTo(m);
		return m;
	}

	public static JsonObject hitActor(String sid, long seq, String eventId, String attackId, int eid, float damage, boolean killed) {
		JsonObject m = msg(sid, seq, "hit_actor");
		m.addProperty("eventId", eventId);
		m.addProperty("attackId", attackId);
		m.addProperty("eid", eid);
		m.addProperty("damage", damage);
		m.addProperty("killed", killed);
		return m;
	}

    public static JsonObject playerState(String sid, long seq, float hp, float maxHp, boolean alive,
                                         String item, HotbarState hotbar, boolean usingItem, boolean blocking) {
        return playerState(sid, seq, hp, maxHp, alive, item, hotbar, usingItem, blocking,
                usingItem ? "offhand" : "none");
    }

    public static JsonObject playerState(String sid, long seq, float hp, float maxHp, boolean alive,
                                         String item, HotbarState hotbar, boolean usingItem, boolean blocking, String usedHand) {
        if (blocking && !usingItem) throw new IllegalArgumentException("blocking requires item use");
        if (usingItem ? !usedHand.equals("mainhand") && !usedHand.equals("offhand") : !usedHand.equals("none"))
            throw new IllegalArgumentException("used hand must match active item use");
        JsonObject message = playerState(sid, seq, hp, maxHp, alive, item, hotbar);
        message.addProperty("usingItem", usingItem);
        message.addProperty("blocking", blocking);
        message.addProperty("usedHand", usedHand);
        return message;
    }

    static boolean useItemPressed(JsonObject message) {
        if (!string(message, "hand").equals("offhand"))
            throw new IllegalArgumentException("only offhand item use is supported");
        if (!message.has("pressed") || !message.get("pressed").isJsonPrimitive()
                || !message.getAsJsonPrimitive("pressed").isBoolean())
            throw new IllegalArgumentException("boolean required: pressed");
        return message.get("pressed").getAsBoolean();
    }

	public static JsonObject ack(String sid, long seq, String ackEventId) {
		JsonObject m = msg(sid, seq, "ack");
		m.addProperty("ackEventId", ackEventId);
		return m;
	}

    static JsonObject operationResult(String sid, String type, String requestId, boolean ok, String message) {
        if (!type.equals("build_result") && !type.equals("inventory_result") && !type.equals("interaction_result"))
            throw new IllegalArgumentException("invalid operation result type");
        if (requestId == null || requestId.isBlank() || requestId.length() > 128
                || message == null || message.isBlank())
            throw new IllegalArgumentException("operation result needs requestId and message");
        JsonObject result = msg(sid, 0, type);
        result.addProperty("requestId", requestId);
        result.addProperty("ok", ok);
        result.addProperty("message", message);
        return result;
    }

    static JsonObject blockState(String sid, BuildingRules.Request request, String item) {
        if (request.id() == null || request.id().isBlank() || request.id().length() > 128)
            throw new IllegalArgumentException("block state needs requestId");
        if (!item.matches("[a-z0-9_.-]+:[a-z0-9_./-]+"))
            throw new IllegalArgumentException("invalid block registry id");
        JsonObject result = msg(sid, 0, "block_state");
        result.addProperty("requestId", request.id());
        result.addProperty("x", request.x());
        result.addProperty("y", request.y());
        result.addProperty("z", request.z());
        result.addProperty("item", item);
        return result;
    }

    public static int selectedSlot(JsonObject message) {
        long slot=integer(message,"slot");
        if(slot>8)throw new IllegalArgumentException("hotbar slot must be 0..8");
        return (int)slot;
    }

    public static boolean bool(JsonObject message, String key) {
        if (!message.has(key) || !message.get(key).isJsonPrimitive()
                || !message.getAsJsonPrimitive(key).isBoolean())
            throw new IllegalArgumentException("boolean required: " + key);
        return message.get(key).getAsBoolean();
    }

    static float pitch(JsonObject message) {
        double value = message.has("pitch") ? number(message, "pitch") : 0;
        if (value < -90 || value > 90) throw new IllegalArgumentException("pitch must be -90..90");
        return (float)value;
    }

	public static boolean validatePv(JsonObject m) {
		try { return integer(m, "pv") == PROTOCOL_VERSION; }
		catch (RuntimeException e) { return false; }
	}

	public static boolean validateEnvelope(JsonObject m) {
		try {
			return validatePv(m) && !string(m, "type").isBlank() && m.has("sid")
					&& string(m, "sid").length() <= 128 && integer(m, "seq") >= 1;
		} catch (RuntimeException e) { return false; }
	}

	public static String string(JsonObject m, String key) {
		if (!m.has(key) || !m.get(key).isJsonPrimitive() || !m.getAsJsonPrimitive(key).isString())
			throw new IllegalArgumentException("string required: " + key);
		return m.get(key).getAsString();
	}

	public static double number(JsonObject m, String key) {
		if (!m.has(key) || !m.get(key).isJsonPrimitive() || !m.getAsJsonPrimitive(key).isNumber())
			throw new IllegalArgumentException("number required: " + key);
		double value = m.get(key).getAsDouble();
		if (!Double.isFinite(value)) throw new IllegalArgumentException("finite number required: " + key);
		return value;
	}

	public static long integer(JsonObject m, String key) {
		double value = number(m, key);
		if (value != Math.rint(value) || value < 0 || value > 9_007_199_254_740_991D)
			throw new IllegalArgumentException("unsigned integer required: " + key);
		return (long) value;
	}

	public static String sid(JsonObject m) {
		return m.has("sid") ? m.get("sid").getAsString() : "";
	}

	public static String type(JsonObject m) {
		return m.has("type") ? m.get("type").getAsString() : "";
	}
}
