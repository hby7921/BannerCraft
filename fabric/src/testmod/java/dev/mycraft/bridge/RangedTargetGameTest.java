package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import com.mojang.authlib.GameProfile;
import io.netty.channel.embedded.EmbeddedChannel;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.network.Connection;
import net.minecraft.network.protocol.PacketFlow;
import net.minecraft.server.level.ClientInformation;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.network.CommonListenerCookie;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.inventory.ChestMenu;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

/** Host target=true rays, actual vanilla ammo/use ticks, and native right-button edges. */
public final class RangedTargetGameTest {
    private static final String SID = "77889900aabbccddeeff00112233445566";

    // Building/weapon ownership is global: run after TNT and the 450+450 weapon fixture.
    @GameTest(maxTicks = 205, setupTicks = 950, skyAccess = true, padding = 16)
    public void terrainTargetsKeepNativeRangedUse(GameTestHelper helper) {
        var fixture = new Fixture(helper);
        var player = fixture.player;
        var bow = new ItemStack(Items.BOW);
        var crossbow = new ItemStack(Items.CROSSBOW);
        player.setItemSlot(EquipmentSlot.MAINHAND, bow);

        // Missing Survival ammo still follows the real item result, and Creative remains native.
        fixture.selectTarget(fixture.farTerrain, true);
        fixture.press();
        helper.assertTrue(!player.isUsingItem() && fixture.projectiles().isEmpty(),
                "a beyond-reach target cannot fabricate ammo or start an empty Survival bow");
        fixture.release();
        player.setGameMode(GameType.CREATIVE);
        fixture.press();
        helper.assertTrue(player.isUsingItem(), "native Creative bow draws without a fabricated inventory arrow");
        fixture.cancel();
        fixture.release();
        player.setGameMode(GameType.SURVIVAL);
        player.getInventory().setItem(1, new ItemStack(Items.ARROW, 12));

        fixture.assertFarBlockSafety();
        player.setItemSlot(EquipmentSlot.MAINHAND, bow);
        fixture.selectTarget(fixture.foreignStone, true);
        fixture.press();
        helper.assertTrue(!player.isUsingItem() && helper.getLevel().getBlockState(fixture.foreignStone).is(Blocks.STONE),
                "reachable original-world overlap remains a genuine error, even with bow ammunition");
        fixture.release();
        fixture.selectTarget(fixture.foreignStone, false);
        fixture.press();
        helper.assertTrue(!player.isUsingItem(), "reachable unowned blocks are not silently converted into air use");
        fixture.release();

        fixture.selectTarget(fixture.chest, false);
        fixture.press();
        helper.assertTrue(player.containerMenu != player.inventoryMenu && !player.isUsingItem(),
                "reachable session-owned chest keeps native block-use priority over bow draw");
        fixture.release();
        player.closeContainer();
        fixture.selectTarget(fixture.nearTerrain, true);
        fixture.press();
        helper.assertTrue(player.isUsingItem() && fixture.interaction.ownsUse(),
                "target=true reachable terrain allows vanilla bow draw");

        helper.runAtTickTime(25, () -> {
            fixture.assertBowDraw(bow, 12, "reachable terrain");
            fixture.release();
            fixture.assertShot(bow, 11, 1, 1, "reachable terrain bow release");
            fixture.selectTarget(fixture.farTerrain, true);
            fixture.press();
            helper.assertTrue(player.isUsingItem(), "8m host terrain ray becomes MISS and still begins native bow draw");
        });
        helper.runAtTickTime(50, () -> {
            fixture.assertBowDraw(bow, 11, "beyond-reach terrain");
            fixture.release();
            fixture.assertShot(bow, 10, 2, 2, "beyond-reach terrain bow release");
            fixture.assertFarTerrainUntouched();
            player.setItemSlot(EquipmentSlot.MAINHAND, crossbow);
            fixture.selectTarget(fixture.nearTerrain, true);
            fixture.press();
            helper.assertTrue(player.isUsingItem(), "reachable terrain allows native crossbow load");
        });
        helper.runAtTickTime(86, () -> {
            fixture.assertLoaded(crossbow, 9, 2, "reachable terrain");
            fixture.release();
            fixture.press();
            fixture.release();
            fixture.assertShot(crossbow, 9, 1, 3, "fresh button press fires reachable loaded crossbow exactly once");
            fixture.selectTarget(fixture.farTerrain, true);
            fixture.press();
            helper.assertTrue(player.isUsingItem(), "8m host terrain ray still begins native crossbow load");
        });
        helper.runAtTickTime(122, () -> {
            fixture.assertLoaded(crossbow, 8, 3, "beyond-reach terrain");
            fixture.assertFarTerrainUntouched();
            fixture.release();
            fixture.press();
            fixture.release();
            fixture.assertShot(crossbow, 8, 2, 4, "fresh button press fires beyond-reach loaded crossbow exactly once");
            player.setItemSlot(EquipmentSlot.MAINHAND, ItemStack.EMPTY);
            player.setItemSlot(EquipmentSlot.OFFHAND, bow);
            fixture.press();
            helper.assertTrue(player.isUsingItem() && player.getUseItem() == bow,
                    "native MISS also preserves offhand bow use after main hand PASS");
        });
        helper.runAtTickTime(147, () -> {
            fixture.assertBowDraw(bow, 8, "offhand beyond-reach terrain");
            fixture.release();
            fixture.assertShot(bow, 7, 3, 5, "offhand bow releases a real arrow");
            player.setItemSlot(EquipmentSlot.OFFHAND, crossbow);
            fixture.press();
            helper.assertTrue(player.isUsingItem() && player.getUseItem() == crossbow,
                    "native MISS also preserves offhand crossbow loading");
        });
        helper.runAtTickTime(183, () -> {
            fixture.assertLoaded(crossbow, 6, 5, "offhand beyond-reach terrain");
            fixture.release();
            fixture.press();
            fixture.release();
            fixture.assertShot(crossbow, 6, 3, 6, "offhand loaded crossbow fires only on a fresh press");
            player.setItemSlot(EquipmentSlot.OFFHAND, ItemStack.EMPTY);
            player.setItemSlot(EquipmentSlot.MAINHAND, bow);
            fixture.press();
        });
        helper.runAtTickTime(186, () -> {
            player.getInventory().setSelectedSlot(2);
            fixture.interaction.tick(player);
            helper.assertTrue(!player.isUsingItem() && fixture.projectiles().size() == 6
                    && player.getInventory().getItem(1).getCount() == 6,
                    "switching slots cancels the held draw without shooting or consuming ammo");
            fixture.release();
            player.getInventory().setSelectedSlot(0);
            fixture.press();
        });
        helper.runAtTickTime(189, () -> {
            player.containerMenu = ChestMenu.oneRow(10, player.getInventory());
            fixture.interaction.tick(player);
            helper.assertTrue(!player.isUsingItem() && fixture.projectiles().size() == 6
                    && player.getInventory().getItem(1).getCount() == 6,
                    "opening a menu cancels the held draw without shooting or consuming ammo");
            fixture.release();
            player.closeContainer();
            fixture.assertFarTerrainUntouched();
            helper.assertTrue(bow.getDamageValue() == 3 && crossbow.getDamageValue() == 3,
                    "all six shots retain actual native durability and no cancellation debit");
            fixture.finished = true;
            BridgeRuntime.log("RANGED_TARGET_PASS: near/far terrain bow and crossbow, both hands, native charge/ammo, fresh press, ownership and cancellation");
            helper.succeed();
        });
    }

    private static final class Fixture {
        final GameTestHelper helper;
        final ServerPlayer player;
        final EmbeddedChannel connectionChannel;
        final BuildingRuntime building = new BuildingRuntime();
        final NativeInteractionRuntime interaction = new NativeInteractionRuntime(building);
        final ArrayList<JsonObject> states = new ArrayList<>();
        final Set<Long> forcedChunks = new HashSet<>();
        final Vec3 origin;
        final BlockPos nearTerrain, farTerrain, extraFarGround, foreignStone, chest, farTnt;
        final AABB bounds;
        BlockPos target;
        boolean terrain, held, finished;

        Fixture(GameTestHelper helper) {
            this.helper = helper;
            BlockPos base = helper.absolutePos(new BlockPos(4, 4, 4));
            origin = new Vec3(base.getX() + .5, base.getY(), base.getZ() + .5);
            bounds = new AABB(base).inflate(24);
            for (int cx = (base.getX() - 4) >> 4; cx <= (base.getX() + 8) >> 4; cx++)
                for (int cz = (base.getZ() - 4) >> 4; cz <= (base.getZ() + 24) >> 4; cz++) {
                    long packed = net.minecraft.world.level.ChunkPos.pack(cx, cz);
                    forcedChunks.add(packed);
                    helper.getLevel().setChunkForced(cx, cz, true);
                    helper.getLevel().getChunkAt(new BlockPos(cx * 16, base.getY(), cz * 16));
                }
            for (int x = -3; x <= 7; x++) for (int z = -3; z <= 20; z++) for (int y = -1; y <= 4; y++)
                helper.getLevel().setBlock(base.offset(x, y, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
            nearTerrain = base.offset(2, -1, 2);
            farTerrain = base.offset(2, -1, 8);
            extraFarGround = farTerrain.offset(1, 0, 0);
            foreignStone = base.offset(-2, 0, 1);
            chest = base.offset(2, 0, -1);
            farTnt = base.offset(3, 0, 8);
            helper.getLevel().setBlock(foreignStone, Blocks.STONE.defaultBlockState(), Block.UPDATE_ALL);
            // The deprecated GameTest mock always overrides gameMode() to CREATIVE,
            // even after setGameMode(SURVIVAL). Use a genuine ServerPlayer with the
            // helper's native embedded connection setup so isCreative/reach are real.
            var profile = new GameProfile(UUID.randomUUID(), "ranged-native-test");
            player = new ServerPlayer(helper.getLevel().getServer(), helper.getLevel(), profile, ClientInformation.createDefault());
            var connection = new Connection(PacketFlow.SERVERBOUND);
            connectionChannel = new EmbeddedChannel(connection);
            helper.getLevel().getServer().getPlayerList().placeNewPlayer(connection, player,
                    CommonListenerCookie.createInitial(profile, false));
            player.setGameMode(GameType.SURVIVAL);
            helper.assertTrue(!player.isCreative(), "ranged fixture uses real Survival game mode and native block reach");
            player.setNoGravity(true);
            applyHostPose();
            player.getInventory().clearContent();
            player.getInventory().setSelectedSlot(0);
            building.begin(player, SID);
            WeaponRuntime.begin(SID, player, message -> { }, message -> states.add(message.deepCopy()));
            NativeWorldGuard.enter(helper.getLevel(), chest);
            try { helper.getLevel().setBlock(chest, Blocks.CHEST.defaultBlockState(), Block.UPDATE_ALL); }
            finally { NativeWorldGuard.exit(); }
            NativeWorldGuard.enter(helper.getLevel(), farTnt);
            try { helper.getLevel().setBlock(farTnt, Blocks.TNT.defaultBlockState(), Block.UPDATE_ALL); }
            finally { NativeWorldGuard.exit(); }
            helper.assertTrue(building.ownsVisible(chest) && building.ownsVisible(farTnt),
                    "reachable chest and beyond-reach TNT fixtures have real native session ownership");
            helper.onEachTick(() -> {
                if (finished) return;
                applyHostPose();
                if (held) interaction.interact(player, input(true), SID);
                player.doTick(); // Actual server Player/LivingEntity ticks advance native charge timers.
                interaction.tick(player);
                building.tick();
                WeaponRuntime.tick();
            });
            NativeTestCleanup.register(helper, () -> {
                held = false;
                interaction.clear(player);
                WeaponRuntime.clear();
                building.clear();
                player.discard();
                connectionChannel.finishAndReleaseAll();
                for (long packed : forcedChunks) {
                    var chunk = net.minecraft.world.level.ChunkPos.unpack(packed);
                    helper.getLevel().setChunkForced(chunk.x(), chunk.z(), false);
                }
            });
        }

        void selectTarget(BlockPos pos, boolean isTerrain) { target = pos; terrain = isTerrain; }
        void press() { held = true; interaction.interact(player, input(true), SID); }
        void release() { held = false; interaction.interact(player, input(false), SID); }
        void cancel() { held = false; interaction.cancel(player); }
        void applyHostPose() {
            player.connection.teleport(origin.x, origin.y, origin.z, 0, 0);
            player.setYHeadRot(0);
            player.setYBodyRot(0);
            player.setDeltaMovement(Vec3.ZERO);
            player.setKnownMovement(Vec3.ZERO);
            player.fallDistance = 0;
        }
        JsonObject input(boolean pressed) {
            var message = Proto.msg(SID, 1, "interact");
            message.addProperty("requestId", UUID.randomUUID().toString());
            message.addProperty("pressed", pressed);
            message.addProperty("target", pressed);
            if (!pressed) return message;
            message.addProperty("x", target.getX());
            message.addProperty("y", target.getY());
            message.addProperty("z", target.getZ());
            message.addProperty("face", Direction.UP.get3DDataValue());
            message.addProperty("hitX", target.getX() + .5);
            message.addProperty("hitY", target.getY() + 1.0);
            message.addProperty("hitZ", target.getZ() + .5);
            message.addProperty("terrain", terrain);
            message.addProperty("canPlace", true);
            message.addProperty("surfaceH", 1.0);
            if (target.equals(farTerrain)) {
                message.addProperty("groundCount", 1);
                message.addProperty("ground0X", extraFarGround.getX());
                message.addProperty("ground0Y", extraFarGround.getY());
                message.addProperty("ground0Z", extraFarGround.getZ());
                message.addProperty("ground0H", .5);
            }
            return message;
        }
        Set<String> projectiles() {
            var result = new HashSet<String>();
            for (var message : states) if (Proto.type(message).equals("projectile_state"))
                result.add(Proto.string(message, "entityId"));
            return result;
        }
        void assertBowDraw(ItemStack bow, int arrows, String context) {
            helper.assertTrue(player.isUsingItem() && player.getUseItem() == bow && player.getTicksUsingItem() >= 20
                    && player.getInventory().getItem(1).getCount() == arrows,
                    "repeated held leases keep one native bow draw beyond one second without an early shot: " + context);
        }
        void assertLoaded(ItemStack crossbow, int arrows, int shots, String context) {
            BridgeRuntime.log("RANGED_LOAD_DIAGNOSTIC context=" + context + " loaded=" + CrossbowItem.isCharged(crossbow)
                    + " using=" + player.isUsingItem() + " useTicks=" + player.getTicksUsingItem()
                    + " ammo=" + player.getInventory().getItem(1) + " shots=" + projectiles().size()
                    + " stack=" + crossbow + " main=" + player.getMainHandItem() + " off=" + player.getOffhandItem());
            // 26.3 keeps the loaded crossbow in its 72000-tick use until release;
            // loading happens in onUseTick, while firing needs the next press.
            helper.assertTrue(CrossbowItem.isCharged(crossbow) && player.isUsingItem()
                    && player.getUseItem() == crossbow && player.getTicksUsingItem() >= 25
                    && player.getInventory().getItem(1).getCount() == arrows && projectiles().size() == shots,
                    "native crossbow loads one real arrow and repeated held leases never auto-fire it: " + context);
        }
        void assertShot(ItemStack weapon, int arrows, int durability, int shots, String description) {
            helper.assertTrue(!player.isUsingItem() && player.getInventory().getItem(1).getCount() == arrows
                    && weapon.getDamageValue() == durability && projectiles().size() == shots, description);
        }
        void assertFarTerrainUntouched() {
            helper.assertTrue(helper.getLevel().getBlockState(farTerrain).isAir()
                    && helper.getLevel().getBlockState(extraFarGround).isAir(),
                    "beyond-reach MISS never prepares target ground or the supplied ground patch");
        }
        void assertFarBlockSafety() {
            long mutations = NativeWorldGuard.observedMutationCount();
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT, 2));
            selectTarget(farTerrain, true);
            press(); release();
            helper.assertTrue(player.getMainHandItem().getCount() == 2, "far terrain cannot consume or place TNT");
            selectTarget(farTnt, false);
            press(); release();
            helper.assertTrue(player.getMainHandItem().getCount() == 2 && helper.getLevel().getBlockState(farTnt.above()).isAir(),
                    "far owned TNT cannot receive block placement");
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.STONE, 2));
            selectTarget(farTerrain, true);
            press(); release();
            helper.assertTrue(player.getMainHandItem().getCount() == 2, "far block MISS cannot place a general block item");
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
            selectTarget(farTnt, false);
            press(); release();
            helper.assertTrue(helper.getLevel().getBlockState(farTnt).is(Blocks.TNT)
                    && helper.getLevel().getEntitiesOfClass(PrimedTnt.class, bounds).isEmpty()
                    && player.getMainHandItem().getDamageValue() == 0 && NativeWorldGuard.observedMutationCount() == mutations,
                    "far owned TNT cannot ignite or mutate and keeps ignition-tool durability");
            assertFarTerrainUntouched();
        }
    }
}

