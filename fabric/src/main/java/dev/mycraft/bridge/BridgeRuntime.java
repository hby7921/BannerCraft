package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.level.GameType;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.item.Items;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

/** Game objects and session state are touched only by commands and the server tick. */
public final class BridgeRuntime {
    public enum State { DISCONNECTED, CONNECTING, READY, ACTIVE }
    private static volatile State state = State.DISCONNECTED;
    private static volatile String sessionId = "";
    private static long generation;
    private static volatile UUID playerId;
    
    private static final SnapshotInboundQueue INBOUND = new SnapshotInboundQueue(1024,4096);
    private static final BoundedIds HURT_SEEN = new BoundedIds(4096);
    private static final BoundedIds ATTACK_SEEN = new BoundedIds(256);
    private static final Map<String, PendingHit> PENDING_HITS = new LinkedHashMap<>();
    private static final ActorRegistry<BridgeTargetEntity> PROXIES = new ActorRegistry<>(BridgeTargetEntity::discardProxy);
    private static final SessionRules SESSION_RULES = new SessionRules();
    private static final BuildingRuntime BUILDING = new BuildingRuntime();
    private static final NativeInteractionRuntime INTERACTION = new NativeInteractionRuntime(BUILDING);
    private static boolean deathSignalSent;
    private static long lastStateSent;
    private static float lastSentHp = -1;
    private static String lastSentItem = "";
    private static float lastSentMaxHp;
    private static HotbarState lastSentHotbar;
    private static boolean lastSentUsingItem, lastSentBlocking;
    private static boolean lastSentContainerOpen;
    private static String lastSentUsedHand = "none";
    private static final long DISPLAY_CHANGE_INTERVAL_NS = 200_000_000L;
    private static SavedPose returnPose;
    private record SavedPose(ServerPlayer player, net.minecraft.server.level.ServerLevel level,
                             double x, double y, double z, float yaw, float pitch, boolean noGravity) { }
    private static final class PendingHit {
        final JsonObject message;
        long sentAt = System.nanoTime();
        int retries;
        PendingHit(JsonObject message) { this.message = message; }
    }

    private BridgeRuntime() { }
    public static void log(String message) { MycraftBridge.LOGGER.info("[Mycraft] {}", message); }
    public static State getState() { return state; }
    public static String getSessionId() { return sessionId; }
    public static MobRuntime.SummonResult summonOwned(ServerPlayer player,String kind) {
        if(state!=State.ACTIVE || deathSignalSent || player==null || !player.getUUID().equals(playerId))
            return new MobRuntime.SummonResult(false,"请先用 /mycraft connect 接入自定义野战","");
        return MobRuntime.summonOwned(player,kind);
    }
    static BridgeTargetEntity findActor(int id) { return PROXIES.get(id); }
    public static boolean ownsHostPlayer(UUID player) {
        return BridgeClient.isConnected() && (state==State.READY || state==State.ACTIVE) && player.equals(playerId);
    }
    public static boolean ownsShieldUse(UUID player) {
        return INTERACTION.ownsUse() && BridgeClient.isConnected() && state == State.ACTIVE && player.equals(playerId);
    }
    public static void selectPlayer(ServerPlayer player) { playerId = player.getUUID(); }

    public static void enqueueInbound(long connectionGeneration, JsonObject message) {
        try { INBOUND.offer(connectionGeneration,message); }
        catch(IllegalArgumentException invalid) {
            // No BridgeClient lock is acquired while holding the inbox monitor.
            INBOUND.close(connectionGeneration,invalid.getMessage());
            BridgeClient.rejectProtocol(invalid.getMessage());
        }
    }

    public static void onBridgeConnected(long connectionGeneration) {
        resetSession();
        INBOUND.reset(connectionGeneration);
        generation = connectionGeneration;
        SESSION_RULES.reset();
        state = State.CONNECTING;
    }

    public static void onBridgeClosed(long connectionGeneration, String reason) {
        // A close marker replaces queued damage; old close callbacks cannot clear a reconnect.
        INBOUND.close(connectionGeneration,reason);
    }

    public static void serverStopped(MinecraftServer server) {
        BridgeClient.disconnect("MC server stopped");
        resetSession();
        playerId = null;
        INBOUND.clearPending();
    }

    public static void playerDisconnected(ServerPlayer player) {
        if (!player.getUUID().equals(playerId)) return;
        // Fabric can invoke DISCONNECT from Netty; the entrypoint marshals this to the server thread.
        BridgeClient.disconnect("MC player disconnected");
        resetSession();
        playerId = null;
    }

    public static void serverTick(MinecraftServer server) {
        SnapshotInboundQueue.Inbound event;
        int pendingAtStart=INBOUND.size();
        for(int processed=0;processed<pendingAtStart;processed++) {
            event=INBOUND.poll();
            if (event == null) break;
            if (event.generation() != generation) continue;
            if (event.closeReason() != null) {
                log("bridge closed: " + event.closeReason());
                resetSession();
                SESSION_RULES.reset();
                state = State.DISCONNECTED;
                continue;
            }
            try { handle(server, event.message()); }
            catch (RuntimeException e) {
                log("invalid message: " + e.getMessage());
                ServerPlayer player = findPlayer(server);
                if (player != null) player.sendSystemMessage(Component.literal("[BannerCraft] Session stopped: " + e.getMessage()), false);
                INBOUND.close(generation,e.getMessage());
                BridgeClient.rejectProtocol(e.getMessage());
                resetSession();
                SESSION_RULES.reset();
            }
        }
        if (state == State.READY || state == State.ACTIVE) {
            ServerPlayer player = findPlayer(server);
            if (player == null || player.level() != server.overworld() || !supportedMode(player)) {
                BridgeClient.close("demo requires connected player in overworld Survival or Creative mode");
                return;
            }
            INTERACTION.tick(player);
            if(returnPose!=null)player.setNoGravity(player.isAutoSpinAttack()?returnPose.noGravity:true);
            sendStateIfChanged(player);
            BUILDING.tick();
            TntRuntime.tick();
            WeaponRuntime.tick();
            MobRuntime.tick();
            BoatRuntime.tick();
            OwnedEntityTicks.tick();
            OwnedMobLoot.tick();
            retryHits();
        }
    }

    private static void handle(MinecraftServer server, JsonObject message) {
        if (SESSION_RULES.accept(message, sessionId) == SessionRules.Result.STALE_SESSION) {
            JsonObject error = Proto.msg(sessionId, 0, "error");
            error.addProperty("code", "stale_session");
            error.addProperty("message", "session does not match");
            BridgeClient.send(error);
            return;
        }
        String type = Proto.type(message);
        if (type.equals("hello")) {
            ServerPlayer player = findPlayer(server);
            if (player == null) throw new IllegalArgumentException("no connected player");
            BridgeClient.setSessionOpened(true);
            BridgeClient.send(Proto.helloAck(player.getGameProfile().name()));
            return;
        }
        if (type.equals("begin_session")) {
            ServerPlayer player = findPlayer(server);
            if (player == null || player.level() != server.overworld() || !supportedMode(player) || !player.isAlive()) {
                if (player != null) player.sendSystemMessage(Component.literal("[BannerCraft] Connect from an alive Survival or Creative player in the Overworld."), false);
                throw new IllegalArgumentException("Survival or Creative overworld player required");
            }
            resetSession();
            PROXIES.setCapacity(message.has("actorLimit") ? (int)Proto.integer(message,"actorLimit") : 64);
            returnPose = new SavedPose(player, server.overworld(), player.getX(), player.getY(), player.getZ(),
                    player.getYRot(), player.getXRot(), player.isNoGravity());
            player.setNoGravity(true);
            sessionId = Proto.sid(message);
            state = State.READY;
            var origin=BridgeStaging.choose(player.level(),message.has("anchorX")?Proto.number(message,"anchorX"):0,
                    message.has("anchorZ")?Proto.number(message,"anchorZ"):0,Proto.number(message,"groundY"),player.getX(),player.getZ());
            JsonObject stage=Proto.msg(sessionId,0,"staging_origin");
            stage.addProperty("offsetX",origin.x());stage.addProperty("offsetZ",origin.z());
            BridgeClient.send(stage);
            BUILDING.begin(player, sessionId);
            TntRuntime.begin(sessionId,player,BridgeRuntime::queueReliableEvent,BridgeClient::send);
            WeaponRuntime.begin(sessionId,player,BridgeRuntime::queueReliableEvent,BridgeClient::send);
            MobRuntime.begin(sessionId,player,BridgeRuntime::queueReliableEvent,BridgeClient::send);
            BoatRuntime.begin(sessionId,player,BridgeRuntime::queueReliableEvent,BridgeClient::send);
            sendStateIfChanged(player);
            log("session begun: " + sessionId);
            log("MC staging uses groundY="+Proto.number(message,"groundY")+"; return pose="
                    +returnPose.x+","+returnPose.y+","+returnPose.z+"; original gravity="+returnPose.noGravity);
            player.sendSystemMessage(Component.literal("[BannerCraft] 已进入骑砍同步区；MC 暂时悬空是位置映射，断开后恢复连接前位置。"),false);
            return;
        }
        if (type.equals("heartbeat") || type.equals("error")) return;
        if (BUILDING.handle(message)) {
            ServerPlayer player = findPlayer(server);
            if (player != null) {
                lastStateSent = 0;
                sendStateIfChanged(player);
            }
            return;
        }
        switch (type) {
            case "mc_army_spawn" -> {
                ServerPlayer player=findPlayer(server);
                if(state!=State.ACTIVE || deathSignalSent || player==null)return;
                var result=MobRuntime.summonArmy(sessionId,player,message);
                if(!result.accepted()) {
                    JsonObject reply=Proto.msg(sessionId,0,"mc_army_spawn_result");
                    reply.addProperty("requestId",Proto.string(message,"requestId"));
                    reply.addProperty("eventId",java.util.UUID.nameUUIDFromBytes((sessionId+Proto.string(message,"requestId")).getBytes(java.nio.charset.StandardCharsets.UTF_8)).toString());
                    reply.addProperty("ok",false);reply.addProperty("message",result.message());queueReliableEvent(reply);
                }
            }
            case "mob_summon" -> {
                String requestId=Proto.string(message,"requestId");
                if(requestId.isBlank() || requestId.length()>128)throw new IllegalArgumentException("invalid summon request");
                var result=summonOwned(findPlayer(server),Proto.string(message,"kind"));
                JsonObject response=Proto.msg(sessionId,0,"interaction_result");
                response.addProperty("requestId",requestId);response.addProperty("ok",result.success());response.addProperty("message",result.message());
                BridgeClient.send(response);
            }
            case "mob_hurt" -> {
                if(state!=State.ACTIVE || deathSignalSent)return;
                String eventId=Proto.string(message,"eventId");
                if(eventId.isBlank() || eventId.length()>128)throw new IllegalArgumentException("invalid mob damage event");
                int sourceId=ActorRegistry.checkedId(Proto.integer(message,"sourceEid"));
                double damage=Proto.number(message,"damage");
                if(damage<=0 || damage>10000)throw new IllegalArgumentException("invalid mob damage");
                if(!MobRuntime.hurt(message,findActor(sourceId)))throw new IllegalArgumentException("unowned mob damage target or source");
                BridgeClient.send(Proto.ack(sessionId,0,eventId));
            }
            case "mob_ground" -> {
                if(state==State.ACTIVE && !deathSignalSent)MobRuntime.prepareGround(message,BUILDING);
            }
            case "stop_spin" -> {
                ServerPlayer player=findPlayer(server);
                if(player!=null && state==State.ACTIVE) {
                    player.startAutoSpinAttack(0,0,net.minecraft.world.item.ItemStack.EMPTY);
                    player.setDeltaMovement(Vec3.ZERO);
                }
            }
            case "end_session" -> resetSession();
            case "select_slot" -> {
                int slot=Proto.selectedSlot(message);
                if((state!=State.READY && state!=State.ACTIVE) || deathSignalSent)return;
                ServerPlayer player=findPlayer(server);
                if(player==null || !player.isAlive())throw new IllegalArgumentException("player unavailable for slot change");
                cancelItemUse(player);
                player.getInventory().setSelectedSlot(slot);
                player.connection.send(new net.minecraft.network.protocol.game.ClientboundSetHeldSlotPacket(slot));
                player.inventoryMenu.broadcastChanges();
                lastStateSent=0;
                sendStateIfChanged(player);
                log("hotbar selected from Bannerlord: " + (slot+1));
            }
            case "interact", "mine", "use_item" -> {
                if (state != State.ACTIVE || deathSignalSent) return;
                ServerPlayer player = findPlayer(server);
                if (player == null || !player.isAlive()) return;
                if (type.equals("mine")) INTERACTION.mine(player, message, sessionId);
                else {
                    if (type.equals("use_item")) {
                        JsonObject legacy = message.deepCopy();
                        legacy.addProperty("pressed", Proto.useItemPressed(message));
                        legacy.addProperty("target", false);
                        legacy.addProperty("requestId", "legacy-" + Proto.integer(message, "seq"));
                        message = legacy;
                    }
                    INTERACTION.interact(player, message, sessionId);
                }
                lastStateSent = 0;
                sendStateIfChanged(player);
            }
            case "player_pose" -> {
                if (!deathSignalSent) {
                    applyPose(findPlayer(server), message);
                    // Construction and inventory work even when no enemy proxy is nearby.
                    state = State.ACTIVE;
                }
            }
            case "actor_upsert" -> {
                int eid = ActorRegistry.checkedId(Proto.integer(message, "eid"));
                if (deathSignalSent) return;
                boolean alive = message.get("alive").getAsBoolean();
                if (!alive) {
                    PROXIES.remove(eid);
                    return;
                }
                double x = Proto.number(message, "x"), y = Proto.number(message, "y"), z = Proto.number(message, "z");
                validatePosition(x, y, z);
                BridgeTargetEntity previous = PROXIES.get(eid);
                if (previous != null && previous.isRemoved()) PROXIES.remove(eid);
                BridgeTargetEntity proxy = PROXIES.getOrCreate(eid, () -> BridgeTargetEntity.spawn(server, eid, x, y, z));
                proxy.setHostile(!message.has("hostile") || Proto.bool(message,"hostile"));
                proxy.setHostSpeed(readMotion(message));
                if(message.has("hp")||message.has("maxHp"))proxy.setHostHealth(Proto.number(message,"hp"),Proto.number(message,"maxHp"));
                proxy.reposition(x, y, z);
                state = State.ACTIVE;
            }
            case "actor_remove" -> PROXIES.remove(ActorRegistry.checkedId(Proto.integer(message, "eid")));
            case "attack_input" -> {
                String attackId = Proto.string(message, "attackId");
                if (attackId.isBlank()) throw new IllegalArgumentException("attackId required");
                if (state != State.ACTIVE || deathSignalSent || !ATTACK_SEEN.add(attackId)) return;
                ServerPlayer player = findPlayer(server);
                if (player != null && player.isAlive()) {
                    applyPose(player, message);
                    resolvePlayerAttack(player, attackId);
                }
            }
            case "player_hurt" -> {
                String eventId = Proto.string(message, "eventId");
                float damage = (float) Proto.number(message, "damage");
                ShieldHurtContext impact=ShieldHurtContext.read(message);
                if (eventId.isBlank() || !Float.isFinite(damage) || damage <= 0) throw new IllegalArgumentException("invalid damage event");
                if (!HURT_SEEN.add(eventId)) {
                    BridgeClient.send(Proto.ack(sessionId, 0, eventId));
                    return;
                }
                if (state != State.ACTIVE || deathSignalSent) {
                    BridgeClient.send(Proto.ack(sessionId, 0, eventId));
                    return;
                }
                ServerPlayer player = findPlayer(server);
                if (player == null) throw new IllegalArgumentException("player missing");
                if(impact.pose().isPresent()) {
                    applyPose(player,message);
                    // Native shield defense reads head yaw, and the background client's
                    // movement packets may have supplied a different orientation.
                    float yaw=(float)impact.pose().get().yaw();
                    player.setYRot(yaw);player.setYHeadRot(yaw);player.setYBodyRot(yaw);
                }
                long sourceId = Proto.integer(message, "sourceEid");
                BridgeTargetEntity source = sourceId <= Integer.MAX_VALUE ? PROXIES.get((int) sourceId) : null;
                boolean proxyFound=source != null && !source.isRemoved() && source.isAlive();
                var baseSource = proxyFound
                        ? server.overworld().damageSources().mobAttack(source)
                        : impact.sourcePosition().isPresent()
                            ? server.overworld().damageSources().source(net.minecraft.world.damagesource.DamageTypes.MOB_ATTACK)
                            : server.overworld().damageSources().generic();
                var damageSource=impact.withSourcePosition(baseSource);
                float amount=damage*(float)MycraftBridge.CONFIG.blToMcDamageFactor;
                var defense=ShieldHurtContext.diagnose(player,damageSource,amount);
                float before=player.getHealth();
                ShieldDamageTrace.begin(player);
                ShieldDamageTrace.Result actualBlock;
                try {player.hurtServer(server.overworld(), damageSource, amount);}
                finally {actualBlock=ShieldDamageTrace.end();}
                log("DEFENSE event="+eventId+" sourceEid="+sourceId+" proxy="+proxyFound+" mode="+player.gameMode()
                        +" damage="+amount+" hp="+before+"->"+player.getHealth()+" actual="+actualBlock+" "+defense);
                JsonObject result=Proto.msg(sessionId,0,"player_hurt_result");
                result.addProperty("eventId",eventId);result.addProperty("hpBefore",before);result.addProperty("hpAfter",player.getHealth());
                result.addProperty("expectedBlocked",defense.expectedBlocked());
                result.addProperty("actualBlocked",actualBlock.blocked());result.addProperty("nativeBlockChecked",actualBlock.checked());
                result.addProperty("sourceKnown",damageSource.getSourcePosition()!=null);result.addProperty("headYaw",player.getYHeadRot());
                result.addProperty("hostBlockPredicted",message.has("hostBlockPredicted") && Proto.bool(message,"hostBlockPredicted"));
                BridgeClient.send(result);
                BridgeClient.send(Proto.ack(sessionId, 0, eventId));
                sendStateIfChanged(player);
            }
            case "ack" -> PENDING_HITS.remove(Proto.string(message, "ackEventId"));
            default -> throw new IllegalArgumentException("unknown message: " + type);
        }
    }

    private static void resolvePlayerAttack(ServerPlayer player, String attackId) {
        // Vanilla client input does not attack while an item (including a shield) is in use.
        if (player.isUsingItem()) return;
        var stack = player.getMainHandItem();
        var piercing = stack.get(net.minecraft.core.component.DataComponents.PIERCING_WEAPON);
        if (piercing != null) {
            if (!player.cannotAttackWithItem(stack, 5)) {
                NativeWorldGuard.enter(player.level(), player.blockPosition());
                try { piercing.attack(player, net.minecraft.world.entity.EquipmentSlot.MAINHAND); }
                finally { NativeWorldGuard.exit(); }
            }
            return;
        }
        double reach = player.getAttributeValue(Attributes.ENTITY_INTERACTION_RANGE);
        Vec3 eye = player.getEyePosition();
        Vec3 end = eye.add(player.getLookAngle().scale(reach));
        var attackTargets = new java.util.ArrayList<net.minecraft.world.entity.LivingEntity>(PROXIES.values());
        for(var mob:MobRuntime.attackTargets())attackTargets.add(mob);
        net.minecraft.world.entity.LivingEntity selected = NearestRayTarget.select(attackTargets, eye, end,
                target -> target.getBoundingBox().inflate(0.1),
                target -> !target.isRemoved() && target.isAlive() && player.hasLineOfSight(target) &&
                    (target instanceof BridgeTargetEntity proxy ? proxy.isHostile() : MobRuntime.owns(target)));
        if(selected==null)return;
        if(!(selected instanceof BridgeTargetEntity proxy)) {
            NativeWorldGuard.enterEntity(player.level(),selected);
            try { player.attack(selected); } finally { NativeWorldGuard.exit(); }
            return;
        }
        // Vanilla Player.attack applies its own cooldown and weapon/enchantment rules;
        // LivingEntity.hurtServer also applies damage immunity frames on the proxy.
        proxy.beginDamageCapture();
        float captured;
        try { player.attack(proxy); }
        finally { captured = proxy.takeCapturedDamage(); }
        float damage = captured * (float) MycraftBridge.CONFIG.mcToBlDamageFactor;
        if (damage > 0 && Float.isFinite(damage)) {
            if (PENDING_HITS.size() >= 4096) { BridgeClient.close("pending hits overflow"); return; }
            String eventId = UUID.randomUUID().toString();
            JsonObject hit = Proto.hitActor(sessionId, 0, eventId, attackId, proxy.actorId(), damage, false);
            PENDING_HITS.put(eventId, new PendingHit(hit));
            BridgeClient.send(hit);
        }
    }

    private static void retryHits() {
        long now = System.nanoTime();
        int emitted=0;
        for (PendingHit hit : PENDING_HITS.values()) {
            if(hit.sentAt==0) {
                if(emitted>=64)continue;
                hit.sentAt=now;emitted++;BridgeClient.send(hit.message);continue;
            }
            if (now - hit.sentAt < 500_000_000L) continue;
            if(emitted>=64)continue;
            if (hit.retries >= 3) { BridgeClient.close("hit acknowledgement timeout"); return; }
            hit.retries++;
            hit.sentAt = now;
            emitted++;
            BridgeClient.send(hit.message);
        }
    }

    static void queueReliableEvent(JsonObject message) {
        if(!sessionId.equals(Proto.sid(message)) || state==State.DISCONNECTED)return;
        String id=Proto.string(message,"eventId");
        if(PENDING_HITS.size()>=4096){BridgeClient.close("reliable combat event budget exceeded");return;}
        var pending=new PendingHit(message);
        // Keep visuals prompt; spread a dense blast's damage records across short server ticks
        // instead of overflowing the 256-frame socket queue. Never drop a gameplay event.
        boolean immediate=Proto.type(message).equals("explosion");
        if(!immediate)pending.sentAt=0;
        if(PENDING_HITS.putIfAbsent(id,pending)==null && immediate)BridgeClient.send(message);
    }

    private static void applyPose(ServerPlayer player, JsonObject message) {
        if (player == null) throw new IllegalArgumentException("player missing");
        double x = Proto.number(message, "x"), y = Proto.number(message, "y"), z = Proto.number(message, "z");
        validatePosition(x, y, z);
        BUILDING.validatePose(x, y, z);
        player.connection.teleport(x, y, z, (float) Proto.number(message, "yaw"), Proto.pitch(message));
        // Host owns pose; do the same native tracking update as a teleport ACK,
        // without waiting for the background client's next acknowledgement.
        player.level().getChunkSource().move(player);
        player.setYHeadRot((float)Proto.number(message, "yaw"));
        player.setYBodyRot((float)Proto.number(message, "yaw"));
        player.fallDistance = 0;
        if(!player.isAutoSpinAttack())player.setDeltaMovement(Vec3.ZERO);
        player.setKnownMovement(readMotion(message));
    }

    private static Vec3 readMotion(JsonObject message) {
        if (!message.has("vx") && !message.has("vy") && !message.has("vz")) return Vec3.ZERO;
        double vx = Proto.number(message, "vx"), vy = Proto.number(message, "vy"), vz = Proto.number(message, "vz");
        if (Math.abs(vx) > 10 || Math.abs(vy) > 10 || Math.abs(vz) > 10) throw new IllegalArgumentException("native motion outside bounds");
        return new Vec3(vx, vy, vz);
    }

    private static void validatePosition(double x, double y, double z) {
        if (Math.abs(x) > 29_999_984 || Math.abs(z) > 29_999_984 || y < -2048 || y > 2048)
            throw new IllegalArgumentException("pose outside demo bounds");
    }

    private static void sendStateIfChanged(ServerPlayer player) {
        long now = System.nanoTime();
        float hp = player.getHealth(), maxHp = player.getMaxHealth();
        String item = player.getMainHandItem().isEmpty() ? "minecraft:air"
                : net.minecraft.core.registries.BuiltInRegistries.ITEM.getKey(player.getMainHandItem().getItem()).toString();
        HotbarState hotbar = captureHotbar(player);
        boolean usingItem = player.isUsingItem();
        String usedHand = usingItem ? (player.getUsedItemHand() == InteractionHand.MAIN_HAND ? "mainhand" : "offhand") : "none";
        boolean blocking = usingItem && player.isBlocking();
        boolean containerOpen = player.containerMenu != player.inventoryMenu;
        if (!player.isAlive()) {
            if (deathSignalSent) return;
            deathSignalSent = true;
            cancelItemUse(player);
            PROXIES.clear();
            BridgeClient.send(Proto.playerState(sessionId, 0, 0, maxHp, false, item, hotbar, false, false));
            return;
        }
        if (deathSignalSent) return;
        boolean displayChanged = !item.equals(lastSentItem) || !hotbar.equals(lastSentHotbar);
        if (hp != lastSentHp || maxHp != lastSentMaxHp
                || usingItem != lastSentUsingItem || blocking != lastSentBlocking || !usedHand.equals(lastSentUsedHand) || containerOpen != lastSentContainerOpen
                || displayChanged && now - lastStateSent >= DISPLAY_CHANGE_INTERVAL_NS
                || usingItem && now - lastStateSent >= 50_000_000L
                || now - lastStateSent >= Proto.HEARTBEAT_MS * 1_000_000L) {
            lastSentHp = hp;
            lastSentMaxHp = maxHp;
            lastSentItem = item;
            lastSentHotbar = hotbar;
            lastSentUsingItem = usingItem;
            lastSentUsedHand = usedHand;
            lastSentBlocking = blocking;
            lastSentContainerOpen = containerOpen;
            lastStateSent = now;
            JsonObject stateMessage = Proto.playerState(sessionId, 0, hp, maxHp, true, item, hotbar, usingItem, blocking, usedHand);
            stateMessage.addProperty("mainHandBlock", player.getMainHandItem().getItem() instanceof net.minecraft.world.item.BlockItem);
            stateMessage.addProperty("offHandBlock", player.getOffhandItem().getItem() instanceof net.minecraft.world.item.BlockItem);
            stateMessage.addProperty("containerOpen", containerOpen);
            stateMessage.addProperty("usingShield",usingItem && player.getUseItem().get(net.minecraft.core.component.DataComponents.BLOCKS_ATTACKS)!=null);
            WeaponRuntime.addPlayerState(stateMessage, player);
            BridgeClient.send(stateMessage);
        }
    }

    private static HotbarState captureHotbar(ServerPlayer player) {
        var inventory = player.getInventory();
        var slots = new ArrayList<HotbarState.Slot>(9);
        for (int i = 0; i < 9; i++) {
            var stack = inventory.getItem(i);
            slots.add(new HotbarState.Slot(stack.isEmpty() ? "minecraft:air"
                    : net.minecraft.core.registries.BuiltInRegistries.ITEM.getKey(stack.getItem()).toString(),
                    stack.isEmpty() ? 0 : stack.getCount()));
        }
        var offhand = player.getOffhandItem();
        String offhandItem = offhand.isEmpty() ? "minecraft:air"
                : net.minecraft.core.registries.BuiltInRegistries.ITEM.getKey(offhand.getItem()).toString();
        return new HotbarState(inventory.getSelectedSlot(), player.getFoodData().getFoodLevel(),
                Math.clamp(player.getArmorValue(), 0, 20), slots, offhandItem, offhand.isEmpty() ? 0 : offhand.getCount());
    }

    private static ServerPlayer findPlayer(MinecraftServer server) {
        return playerId == null ? null : server.getPlayerList().getPlayer(playerId);
    }

    static boolean supportedMode(ServerPlayer player) {
        return player.gameMode() == GameType.SURVIVAL || player.gameMode() == GameType.CREATIVE;
    }

    private static void resetSession() {
        MobRuntime.preservePickedEquipment();
        if(returnPose!=null)OwnedMobLoot.preserve(returnPose.level,new Vec3(returnPose.x,returnPose.y,returnPose.z));
        else OwnedMobLoot.preserve(null,null);
        if(returnPose!=null)BoatRuntime.preserve(returnPose.level,new Vec3(returnPose.x,returnPose.y,returnPose.z));
        if(returnPose!=null)WeaponRuntime.preservePickupProjectiles(returnPose.level,new Vec3(returnPose.x,returnPose.y,returnPose.z));
        if(returnPose!=null)NativeWorldGuard.preserveItemDrops(returnPose.level,new Vec3(returnPose.x,returnPose.y,returnPose.z));
        INTERACTION.clear(returnPose == null ? null : returnPose.player);
        TntRuntime.clear();
        WeaponRuntime.clear();
        MobRuntime.clear();
        BoatRuntime.clear();
        OwnedEntityTicks.clear();
        BUILDING.clear();
        restorePlayerPose();
        sessionId = "";
        state = BridgeClient.isConnected() ? State.CONNECTING : State.DISCONNECTED;
        PROXIES.clear();
        PENDING_HITS.clear();
        HURT_SEEN.clear();
        ATTACK_SEEN.clear();
        deathSignalSent = false;
        lastSentHp = -1;
        lastStateSent = 0;
        lastSentItem = "";
        lastSentHotbar = null;
        lastSentUsingItem = false;
        lastSentUsedHand = "none";
        lastSentBlocking = false;
        lastSentContainerOpen = false;
    }

    static void cancelItemUse(ServerPlayer player) {
        INTERACTION.cancel(player);
    }

    private static void restorePlayerPose() {
        SavedPose saved = returnPose;
        returnPose = null;
        if (saved == null) return;
        ServerPlayer player = saved.player;
        // Restore the entity flag even when a dead/removed player cannot be teleported.
        player.setNoGravity(saved.noGravity);
        // Do not move a corpse, a removed player, a replacement after respawn, or a player in another dimension.
        if (deathSignalSent || !player.isAlive() || player.isRemoved() || player.level() != saved.level) return;
        if (player.connection.isAcceptingMessages()) {
            player.connection.teleport(saved.x, saved.y, saved.z, saved.yaw, saved.pitch);
        } else {
            // Logout/shutdown may already have closed the channel; still restore the position before player saving.
            player.snapTo(saved.x, saved.y, saved.z, saved.yaw, saved.pitch);
        }
        player.fallDistance = 0;
        player.setDeltaMovement(Vec3.ZERO);
        log("restored MC player to "+saved.x+","+saved.y+","+saved.z+"; noGravity="+saved.noGravity);
    }
}
