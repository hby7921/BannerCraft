package dev.mycraft.bridge;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.rendering.v1.EntityRendererRegistry;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.EntityRendererProvider;
import net.minecraft.client.renderer.entity.state.EntityRenderState;

public final class MycraftBridgeClient implements ClientModInitializer {
    private Boolean previousPauseOnLostFocus;

    @Override
    public void onInitializeClient() {
        VanillaInventoryBridge.initialize();
        EntityRendererRegistry.register(BridgeTargetEntity.TYPE, InvisibleRenderer::new);
        ClientTickEvents.END_CLIENT_TICK.register(client -> {
            NativeBlockModels.clientTick(client);
            NativeMobModels.clientTick(client);
            VanillaInventoryBridge.tick(client);
            if (BridgeClient.isConnected()) {
                if (previousPauseOnLostFocus == null) previousPauseOnLostFocus = client.options.pauseOnLostFocus;
                client.options.pauseOnLostFocus = false;
                if (client.isPaused() || client.level == null) BridgeClient.close("MC paused or world closed");
            } else if (previousPauseOnLostFocus != null) {
                client.options.pauseOnLostFocus = previousPauseOnLostFocus;
                previousPauseOnLostFocus = null;
            }
        });
        ClientLifecycleEvents.CLIENT_STOPPING.register(client -> {
            VanillaInventoryBridge.shutdown(client);
            BridgeClient.disconnect("MC client stopped");
            if (previousPauseOnLostFocus != null) {
                client.options.pauseOnLostFocus = previousPauseOnLostFocus;
                previousPauseOnLostFocus = null;
            }
        });
    }

    private static final class InvisibleRenderer extends EntityRenderer<BridgeTargetEntity, EntityRenderState> {
        InvisibleRenderer(EntityRendererProvider.Context context) { super(context); }
        @Override public EntityRenderState createRenderState() { return new EntityRenderState(); }
    }
}
