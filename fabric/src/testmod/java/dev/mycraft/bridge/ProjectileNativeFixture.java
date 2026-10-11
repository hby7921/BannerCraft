package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import com.mojang.authlib.GameProfile;
import io.netty.channel.embedded.EmbeddedChannel;
import net.minecraft.core.BlockPos;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.network.Connection;
import net.minecraft.network.protocol.PacketFlow;
import net.minecraft.server.level.ClientInformation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.level.TicketType;
import net.minecraft.server.network.CommonListenerCookie;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;

/** An isolated native world fixture; only the source chunk receives a fixture ticking ticket. */
final class ProjectileNativeFixture {
    private static final TicketType SOURCE_TICKET = new TicketType(40L, TicketType.FLAG_LOADING | TicketType.FLAG_SIMULATION);
    static final String SID = "556677889900aabbccddeeff00112233";
    final GameTestHelper helper;
    final ServerPlayer player;
    final EmbeddedChannel connectionChannel;
    final BuildingRuntime building = new BuildingRuntime();
    final NativeInteractionRuntime interaction = new NativeInteractionRuntime(building);
    final ArrayList<JsonObject> reliable = new ArrayList<>(), states = new ArrayList<>();
    final ArrayList<BridgeTargetEntity> targets = new ArrayList<>();
    final Set<Long> fixtureTickets = new HashSet<>();
    final Vec3 origin;
    final Villager foreign;
    final float foreignHealth;
    boolean held, finished;
    float pitch;

    ProjectileNativeFixture(GameTestHelper helper) {
        this.helper = helper;
        // Keep the corridor away from other GameTest structures and their ticking tickets.
        BlockPos base = helper.absolutePos(new BlockPos(260, 4, 260));
        origin = new Vec3(base.getX() + .5, base.getY(), base.getZ() + .5);
        for (int cx = (base.getX() - 5) >> 4; cx <= (base.getX() + 6) >> 4; cx++)
            for (int cz = (base.getZ() - 3) >> 4; cz <= (base.getZ() + 90) >> 4; cz++)
                helper.getLevel().getChunkAt(new BlockPos(cx * 16, base.getY(), cz * 16));
        // These chunk loads/terrain edits belong to the disposable test world, never production flight.
        for (int x = -5; x <= 6; x++) for (int z = -3; z <= 90; z++) {
            helper.getLevel().setBlock(base.offset(x, -1, z), Blocks.STONE.defaultBlockState(), Block.UPDATE_CLIENTS);
            for (int y = 0; y <= 18; y++)
                helper.getLevel().setBlock(base.offset(x, y, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_CLIENTS);
        }
        int sourceX = base.getX() >> 4, sourceZ = base.getZ() >> 4;
        helper.getLevel().setChunkForced(sourceX, sourceZ, true);
        fixtureTickets.add(net.minecraft.world.level.ChunkPos.pack(sourceX, sourceZ));
        var sourceChunk = new net.minecraft.world.level.ChunkPos(sourceX, sourceZ);
        // A mock client's chunk ACKs never arrive. Hold this one real source centre
        // at native ENTITY_TICKING, without preparing or forcing the distant corridor.
        helper.getLevel().getChunkSource().addTicketAndLoadWithRadius(SOURCE_TICKET, sourceChunk, 2);
        var profile = new GameProfile(UUID.randomUUID(), "projectile-native-test");
        player = new ServerPlayer(helper.getLevel().getServer(), helper.getLevel(), profile, ClientInformation.createDefault());
        var connection = new Connection(PacketFlow.SERVERBOUND);
        connectionChannel = new EmbeddedChannel(connection);
        helper.getLevel().getServer().getPlayerList().placeNewPlayer(connection, player,
                CommonListenerCookie.createInitial(profile, false));
        player.setGameMode(GameType.SURVIVAL);
        player.setNoGravity(true);
        player.getInventory().clearContent();
        player.getInventory().setSelectedSlot(0);
        applyPose();
        helper.assertTrue(!player.isCreative(), "real native player uses Survival ammunition rules");
        foreign = EntityTypes.VILLAGER.create(helper.getLevel(), EntitySpawnReason.TRIGGERED);
        helper.assertTrue(foreign != null, "foreign native villager exists");
        foreign.setNoAi(true);
        foreign.setNoGravity(true);
        foreign.setPos(origin.x, origin.y, origin.z + 3.3);
        helper.assertTrue(helper.getLevel().addFreshEntity(foreign), "foreign villager added outside the owned session scope");
        foreignHealth = foreign.getHealth();
        building.begin(player, SID);
        WeaponRuntime.begin(SID, player, message -> reliable.add(message.deepCopy()), message -> states.add(message.deepCopy()));
        helper.onEachTick(() -> {
            if (finished) return;
            helper.getLevel().getChunkSource().addTicketWithRadius(SOURCE_TICKET, sourceChunk, 2);
            applyPose();
            if (held) interaction.interact(player, input(true), SID);
            player.doTick();
            interaction.tick(player);
            building.tick();
            WeaponRuntime.tick();
            helper.assertTrue(NativeProjectileTicks.centreCount() <= 32 && NativeProjectileTicks.fullChunkCount() <= 96,
                    "production projectile loading/simulation tickets stay inside their hard budgets");
        });
        NativeTestCleanup.register(helper, () -> {
            finished = true;
            helper.getLevel().getChunkSource().removeTicketWithRadius(SOURCE_TICKET, sourceChunk, 2);
            interaction.clear(player);
            discardTargets();
            WeaponRuntime.clear();
            helper.assertTrue(NativeProjectileTicks.centreCount() == 0 && NativeProjectileTicks.fullChunkCount() == 0,
                    "session cleanup releases every transient projectile ticket reference");
            building.clear();
            foreign.discard();
            try { helper.getLevel().getServer().getPlayerList().remove(player); }
            finally { connectionChannel.finishAndReleaseAll(); }
            for (long packed : fixtureTickets) {
                var chunk = net.minecraft.world.level.ChunkPos.unpack(packed);
                helper.getLevel().setChunkForced(chunk.x(), chunk.z(), false);
            }
        });
    }

    void placeOwned(BlockPos pos, BlockState state) {
        NativeWorldGuard.enter(helper.getLevel(), pos);
        try { helper.assertTrue(helper.getLevel().setBlock(pos, state, Block.UPDATE_ALL), "native owned block is placed"); }
        finally { NativeWorldGuard.exit(); }
        helper.assertTrue(building.ownsVisible(pos), "actual block placement registers session ownership");
    }
    void press() { held = true; interaction.interact(player, input(true), SID); }
    void release() { held = false; interaction.interact(player, input(false), SID); }
    void applyPose() {
        player.connection.teleport(origin.x, origin.y, origin.z, 0, pitch);
        player.setYHeadRot(0);
        player.setYBodyRot(0);
        player.setDeltaMovement(Vec3.ZERO);
        player.setKnownMovement(Vec3.ZERO);
        player.fallDistance = 0;
        // The embedded client sends no teleport ACK. Run the same native chunk-map move that
        // handleAcceptTeleportPacket performs, so natural player simulation tickets follow it.
        helper.getLevel().getChunkSource().move(player);
    }
    List<BridgeTargetEntity> targetBank(int firstId, double distance) {
        discardTargets();
        // Real adjacent actor proxies accommodate native random spread at 80 m. All feet remain at ground Y.
        for (int column = -3; column <= 3; column++) {
            var target = BridgeTargetEntity.spawn(helper.getLevel().getServer(), firstId + column + 3,
                    origin.x + column * .5, origin.y, origin.z + distance);
            helper.assertTrue(target != null, "real native proxy exists at " + distance + " m");
            targets.add(target);
        }
        return List.copyOf(targets);
    }
    void discardTargets() { for (var target : targets) target.discardProxy(); targets.clear(); }
    void assertForeignPreserved() {
        helper.assertTrue(foreign.isAlive() && foreign.getHealth() == foreignHealth && !NativeWorldGuard.ownsEntity(foreign),
                "native session arrows preserve an unrelated villager");
    }
    Set<String> projectileIds() {
        var ids = new HashSet<String>();
        for (var state : states) if (Proto.type(state).equals("projectile_state")) ids.add(Proto.string(state, "entityId"));
        return ids;
    }
    private static JsonObject input(boolean pressed) {
        var input = Proto.msg(SID, 1, "interact");
        input.addProperty("requestId", UUID.randomUUID().toString());
        input.addProperty("pressed", pressed);
        input.addProperty("target", false);
        return input;
    }
}

