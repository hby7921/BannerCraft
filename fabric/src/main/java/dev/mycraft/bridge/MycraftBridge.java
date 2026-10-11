package dev.mycraft.bridge;

import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.arguments.StringArgumentType;
import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.command.v2.CommandRegistrationCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.minecraft.commands.Commands;
import net.minecraft.network.chat.Component;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Mycraft Bridge entrypoint. Real Minecraft combat rules and player health for
 * the Mycraft custom battle demo (protocol/demo-v1.md).
 *
 * Commands:
 *   /mycraft connect [port]   connect to the Bannerlord bridge server (loopback)
 *   /mycraft disconnect       end the bridge session
 *   /mycraft status           print bridge state
 */
public final class MycraftBridge implements ModInitializer {
	public static final String MOD_ID = "mycraft-bridge";
	public static final Logger LOGGER = LoggerFactory.getLogger(MOD_ID);
	public static ModConfig CONFIG;

	@Override
	public void onInitialize() {
		CONFIG = ModConfig.load();
		HostGroundBlock.register();
		BridgeTargetEntity.register();
		ServerTickEvents.END_SERVER_TICK.register(BridgeRuntime::serverTick);
		ServerLifecycleEvents.SERVER_STOPPING.register(BridgeRuntime::serverStopped);
		ServerPlayConnectionEvents.DISCONNECT.register((handler, server) -> {
			if (server.isSameThread()) BridgeRuntime.playerDisconnected(handler.player);
			else server.execute(() -> BridgeRuntime.playerDisconnected(handler.player));
		});
		registerCommands();
		BridgeRuntime.log("initialized (mc 26.3, protocol v1)");
	}

	private static void registerCommands() {
		CommandRegistrationCallback.EVENT.register((dispatcher, registryAccess, environment) -> dispatcher.register(
				Commands.literal("mycraft")
                        .then(Commands.literal("summon")
                            .then(Commands.argument("kind",StringArgumentType.word()).executes(ctx -> {
                                var result=BridgeRuntime.summonOwned(ctx.getSource().getPlayerOrException(),StringArgumentType.getString(ctx,"kind"));
                                ctx.getSource().sendSuccess(()->Component.literal("[BannerCraft] "+result.message()),false);
                                return result.success()?1:0;
                            })))
						.then(Commands.literal("connect")
								.executes(ctx -> doConnect(ctx.getSource().getPlayerOrException(), CONFIG.port))
								.then(Commands.argument("port", IntegerArgumentType.integer(1, 65535))
										.executes(ctx -> doConnect(ctx.getSource().getPlayerOrException(), IntegerArgumentType.getInteger(ctx, "port")))))
						.then(Commands.literal("disconnect")
								.executes(ctx -> {
									BridgeClient.disconnect("user command");
									ctx.getSource().sendSuccess(() -> Component.literal("[BannerCraft] disconnected"), false);
									return 1;
								}))
						.then(Commands.literal("status")
								.executes(ctx -> {
									ctx.getSource().sendSuccess(() -> Component.literal(
											"[BannerCraft] " + BridgeClient.statusText()), false);
									return 1;
								}))));
	}

	private static int doConnect(net.minecraft.server.level.ServerPlayer player, int port) {
		if (!player.isAlive() || !BridgeRuntime.supportedMode(player)
				|| player.level() != player.level().getServer().overworld()) {
			player.sendSystemMessage(Component.literal("[BannerCraft] Use an alive Survival or Creative player in the Overworld."), false);
			return 0;
		}
		BridgeRuntime.selectPlayer(player);
		boolean ok = BridgeClient.connect(CONFIG.host, port);
		player.sendSystemMessage(Component.literal(ok
				? "[BannerCraft] connected to " + CONFIG.host + ":" + port
				: "[BannerCraft] connection failed"), false);
		return ok ? 1 : 0;
	}
}
