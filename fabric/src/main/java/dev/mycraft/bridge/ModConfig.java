package dev.mycraft.bridge;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import net.fabricmc.loader.api.FabricLoader;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * Bridge config: config/mycraft-bridge.json. Loopback only by contract.
 */
public final class ModConfig {
	private static final Gson GSON = new GsonBuilder().setPrettyPrinting().create();
	private static final Path PATH = FabricLoader.getInstance().getConfigDir().resolve("mycraft-bridge.json");

	public String host = "127.0.0.1";
	public int port = 38765;
	public double blToMcDamageFactor = 1.0;
	public double mcToBlDamageFactor = 1.0;

	public static ModConfig load() {
		try {
			if (Files.exists(PATH)) {
				ModConfig config = GSON.fromJson(Files.readString(PATH, StandardCharsets.UTF_8), ModConfig.class);
				if (config == null || config.port < 1 || config.port > 65535
						|| !java.net.InetAddress.getByName(config.host).isLoopbackAddress()
						|| !Double.isFinite(config.blToMcDamageFactor) || config.blToMcDamageFactor <= 0 || config.blToMcDamageFactor > 100
						|| !Double.isFinite(config.mcToBlDamageFactor) || config.mcToBlDamageFactor <= 0 || config.mcToBlDamageFactor > 100)
					throw new IllegalArgumentException("invalid loopback/port/damage config");
				return config;
			}
		} catch (IOException | RuntimeException e) {
			BridgeRuntime.log("config load failed (" + e.getMessage() + "); using defaults");
		}
		var config = new ModConfig();
		config.save();
		return config;
	}

	public void save() {
		try {
			Files.createDirectories(PATH.getParent());
			Files.writeString(PATH, GSON.toJson(this), StandardCharsets.UTF_8);
		} catch (IOException e) {
			BridgeRuntime.log("config save failed: " + e.getMessage());
		}
	}
}
