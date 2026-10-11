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
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.piston.PistonBaseBlock;
import net.minecraft.world.level.block.piston.PistonHeadBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;
import java.util.HashSet;
import java.util.UUID;

/** Real Survival input, delayed native piston events, and moving-block finalization. */
public final class PistonPlacementGameTest {
    private static final String SID = "ca123400aabbccddeeff001122334455";

    @GameTest(maxTicks = 100, setupTicks = 2400, skyAccess = true, padding = 16)
    public void endRodsAndOwnedPistonMotion(GameTestHelper helper) {
        var fixture = new Fixture(helper);
        var level = helper.getLevel();
        var player = fixture.player;
        BlockPos piston = fixture.base.offset(2, 0, 2);
        BlockPos head = piston.south();
        BlockPos movedBlock = head.south();
        BlockPos power = piston.north();
        BlockPos foreignPiston = fixture.base.offset(-2, 0, 2);
        BlockPos foreignHead = foreignPiston.south();
        BlockPos protectedStone = foreignHead.south();
        BlockPos blockedPiston = fixture.base.offset(4, 0, 2);
        BlockPos foreignPushBlock = blockedPiston.south();
        BlockPos slimePiston = fixture.base.offset(-4, 0, 4);
        Vec3 slimeContact = Vec3.atBottomCenterOf(slimePiston.south()).add(0, .1, .8);
        var foreignRabbit = EntityTypes.RABBIT.create(level, EntitySpawnReason.TRIGGERED);
        var ownedRabbit = EntityTypes.RABBIT.create(level, EntitySpawnReason.TRIGGERED);
        foreignRabbit.setNoAi(true); foreignRabbit.setNoGravity(true); foreignRabbit.setPos(slimeContact);
        // Both boxes intersect the moving face, but the rabbits do not overlap
        // each other: ordinary animal collision is independent of piston motion.
        ownedRabbit.setNoAi(true); ownedRabbit.setNoGravity(true); ownedRabbit.setPos(slimeContact.add(.65, 0, 0));
        helper.assertTrue(level.addFreshEntity(foreignRabbit), "foreign native slime-contact animal exists");
        NativeWorldGuard.enter(level, slimePiston);
        try { helper.assertTrue(level.addFreshEntity(ownedRabbit), "owned native slime-contact animal exists"); }
        finally { NativeWorldGuard.exit(); }
        NativeTestCleanup.register(helper, () -> { foreignRabbit.discard(); ownedRabbit.discard(); });
        fixture.placeOwned(piston.below(), Blocks.STONE.defaultBlockState());

        player.setYRot(180); // Native placement faces south, the same face as the live report.
        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.STICKY_PISTON, 2));
        fixture.use(piston.below(), Direction.UP, true);
        helper.assertTrue(level.getBlockState(piston).is(Blocks.STICKY_PISTON)
                && level.getBlockState(piston).getValue(PistonBaseBlock.FACING) == Direction.SOUTH
                && player.getMainHandItem().getCount() == 1 && fixture.building.ownsNative(piston),
                "real native Survival input places one owned sticky piston with its original facing");

        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.END_ROD, 8));
        fixture.use(piston, Direction.UP, true);
        fixture.use(piston, Direction.WEST, true);
        fixture.use(piston, Direction.SOUTH, true);
        helper.assertTrue(level.getBlockState(piston.above()).is(Blocks.END_ROD)
                && level.getBlockState(piston.west()).is(Blocks.END_ROD)
                && level.getBlockState(head).is(Blocks.END_ROD) && player.getMainHandItem().getCount() == 5,
                "end rod attaches to the empty top, side, and unextended front of the native sticky piston");
        fixture.use(piston, Direction.EAST, false);
        helper.assertTrue(level.getBlockState(piston.east()).isAir() && player.getMainHandItem().getCount() == 5,
                "a genuine host placement denial does not consume an end rod or turn FAIL into SUCCESS");

        fixture.placeOwned(head, Blocks.AIR.defaultBlockState());
        fixture.placeOwned(head, Blocks.STONE.defaultBlockState());
        fixture.placeOwned(power, Blocks.REDSTONE_BLOCK.defaultBlockState());
        // Original-world pistons and heads are never adopted merely because an owned source powers nearby cells.
        BlockState foreignBase = Blocks.STICKY_PISTON.defaultBlockState().setValue(PistonBaseBlock.FACING, Direction.SOUTH);
        BlockState foreignHeadState = Blocks.PISTON_HEAD.defaultBlockState().setValue(PistonHeadBlock.FACING, Direction.SOUTH);
        level.setBlock(foreignPiston, foreignBase, Block.UPDATE_CLIENTS);
        level.setBlock(foreignHead, foreignHeadState, Block.UPDATE_CLIENTS);
        level.setBlock(protectedStone, Blocks.STONE.defaultBlockState(), Block.UPDATE_CLIENTS);
        fixture.placeOwned(foreignPiston.north(), Blocks.REDSTONE_BLOCK.defaultBlockState());
        level.setBlock(foreignPushBlock, Blocks.STONE.defaultBlockState(), Block.UPDATE_CLIENTS);
        fixture.placeOwned(blockedPiston, Blocks.STICKY_PISTON.defaultBlockState().setValue(PistonBaseBlock.FACING, Direction.SOUTH));
        fixture.placeOwned(blockedPiston.north(), Blocks.REDSTONE_BLOCK.defaultBlockState());
        fixture.placeOwned(slimePiston, Blocks.STICKY_PISTON.defaultBlockState().setValue(PistonBaseBlock.FACING, Direction.SOUTH));
        fixture.placeOwned(slimePiston.south(), Blocks.SLIME_BLOCK.defaultBlockState());
        fixture.placeOwned(slimePiston.north(), Blocks.REDSTONE_BLOCK.defaultBlockState());

        helper.runAtTickTime(12, () -> {
            helper.assertTrue(level.getBlockState(slimePiston.south(2)).is(Blocks.SLIME_BLOCK)
                    && ownedRabbit.position().z > slimeContact.z + .8,
                    "the real moving slime block pushes a genuine owned contact animal");
            helper.assertTrue(foreignRabbit.position().distanceToSqr(slimeContact) < 1.0e-8
                    && foreignRabbit.getDeltaMovement().lengthSqr() < 1.0e-8,
                    "native slime velocity writes and piston displacement both preserve a foreign contact animal: position="
                            + foreignRabbit.position() + " original=" + slimeContact + " velocity=" + foreignRabbit.getDeltaMovement());
            helper.assertTrue(level.getBlockState(piston).getValue(PistonBaseBlock.EXTENDED)
                    && level.getBlockState(head).is(Blocks.PISTON_HEAD)
                    && level.getBlockState(movedBlock).is(Blocks.STONE),
                    "normal delayed vanilla piston event extends and pushes the actual stone");
            helper.assertTrue(fixture.building.ownsNative(head) && fixture.building.ownsVisible(head)
                    && fixture.building.ownsNative(movedBlock) && fixture.building.ownsVisible(movedBlock),
                    "moving-block finalization retains exact ownership for the native head and pushed block");
            fixture.building.validatePose(head.getX() + .5, head.getY(), head.getZ() + .5);
            boolean foreignCollisionRejected = false;
            try { fixture.building.validatePose(foreignHead.getX() + .5, foreignHead.getY(), foreignHead.getZ() + .5); }
            catch (IllegalArgumentException expected) { foreignCollisionRejected = expected.getMessage().contains("original Minecraft terrain is protected"); }
            helper.assertTrue(foreignCollisionRejected, "an actual original-world piston head remains a fatal foreign collision");
            helper.assertTrue(level.getBlockState(foreignPiston).equals(foreignBase)
                    && level.getBlockState(foreignHead).equals(foreignHeadState)
                    && level.getBlockState(protectedStone).is(Blocks.STONE)
                    && !fixture.building.ownsNative(foreignPiston) && !fixture.building.ownsNative(foreignHead),
                    "owned power never authorizes foreign piston/head motion or claims original-world terrain");
            helper.assertTrue(!level.getBlockState(blockedPiston).getValue(PistonBaseBlock.EXTENDED)
                    && level.getBlockState(foreignPushBlock).is(Blocks.STONE)
                    && !fixture.building.ownsNative(foreignPushBlock),
                    "an owned piston cannot push or claim a foreign original-world stone");

            fixture.use(piston, Direction.SOUTH, true);
            helper.assertTrue(level.getBlockState(head).is(Blocks.PISTON_HEAD)
                    && player.getMainHandItem().getCount() == 5,
                    "an extended piston head is actual occupation: native end-rod placement still fails without debit");
            fixture.placeOwned(power, Blocks.AIR.defaultBlockState());
        });
        helper.runAtTickTime(24, () -> {
            helper.assertTrue(level.getBlockState(piston).is(Blocks.STICKY_PISTON)
                    && !level.getBlockState(piston).getValue(PistonBaseBlock.EXTENDED)
                    && level.getBlockState(head).is(Blocks.STONE) && level.getBlockState(movedBlock).isAir(),
                    "normal sticky retraction pulls the exact owned block and removes its temporary head");
            helper.assertTrue(fixture.building.ownsNative(piston) && fixture.building.ownsNative(head),
                    "retraction finalizes the original base and pulled block within their exact owned scope");
            fixture.finished = true;
            fixture.building.clear();
            helper.assertTrue(level.getBlockState(piston).isAir() && level.getBlockState(head).isAir()
                    && level.getBlockState(piston.above()).isAir() && level.getBlockState(piston.west()).isAir(),
                    "session cleanup removes only the real tracked piston/head/rod construction");
            helper.assertTrue(level.getBlockState(foreignPiston).equals(foreignBase)
                    && level.getBlockState(foreignHead).equals(foreignHeadState)
                    && level.getBlockState(protectedStone).is(Blocks.STONE),
                    "session cleanup retains original-world piston/head and stone unchanged");
            BridgeRuntime.log("PISTON_PLACEMENT_PASS: native top/side end rods, true occupied-head FAIL, extension/push/retraction ownership, pose safety and foreign-world cleanup");
            helper.succeed();
        });
    }

    private static final class Fixture {
        final GameTestHelper helper;
        final ServerPlayer player;
        final EmbeddedChannel channel;
        final BuildingRuntime building = new BuildingRuntime();
        final NativeInteractionRuntime interaction = new NativeInteractionRuntime(building);
        final HashSet<Long> forced = new HashSet<>();
        final BlockPos base;
        boolean finished;

        Fixture(GameTestHelper helper) {
            this.helper = helper;
            base = helper.absolutePos(new BlockPos(4, 4, 4));
            var level = helper.getLevel();
            for (int cx = (base.getX() - 5) >> 4; cx <= (base.getX() + 6) >> 4; cx++)
                for (int cz = (base.getZ() - 4) >> 4; cz <= (base.getZ() + 7) >> 4; cz++) {
                    long packed = net.minecraft.world.level.ChunkPos.pack(cx, cz);
                    if (!level.getForceLoadedChunks().contains(packed)) {
                        forced.add(packed);
                        level.setChunkForced(cx, cz, true);
                    }
                    level.getChunkAt(new BlockPos(cx * 16, base.getY(), cz * 16));
                }
            for (int x = -5; x <= 6; x++) for (int z = -4; z <= 7; z++) for (int y = -1; y <= 3; y++)
                level.setBlock(base.offset(x, y, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
            var profile = new GameProfile(UUID.randomUUID(), "piston-native-test");
            player = new ServerPlayer(level.getServer(), level, profile, ClientInformation.createDefault());
            var connection = new Connection(PacketFlow.SERVERBOUND);
            channel = new EmbeddedChannel(connection);
            level.getServer().getPlayerList().placeNewPlayer(connection, player, CommonListenerCookie.createInitial(profile, false));
            player.setGameMode(GameType.SURVIVAL);
            player.setNoGravity(true);
            player.connection.teleport(base.getX() + .5, base.getY(), base.getZ() + .5, 180, 0);
            level.getChunkSource().move(player);
            player.setDeltaMovement(Vec3.ZERO);
            player.getInventory().clearContent();
            player.getInventory().setSelectedSlot(0);
            building.begin(player, SID);
            MobRuntime.begin(SID,player,message->{},message->{});
            helper.onEachTick(() -> { if (!finished) { interaction.tick(player); building.tick(); } });
            NativeTestCleanup.register(helper, () -> {
                interaction.clear(player);
                MobRuntime.clear(); OwnedEntityTicks.clear();
                building.clear();
                level.getServer().getPlayerList().remove(player);
                channel.finishAndReleaseAll();
                for (long packed : forced) {
                    var chunk = net.minecraft.world.level.ChunkPos.unpack(packed);
                    level.setChunkForced(chunk.x(), chunk.z(), false);
                }
            });
        }

        void placeOwned(BlockPos pos, BlockState state) {
            NativeWorldGuard.enter(helper.getLevel(), pos);
            try { helper.assertTrue(helper.getLevel().setBlock(pos, state, Block.UPDATE_ALL), "owned native mutation accepted at " + pos); }
            finally { NativeWorldGuard.exit(); }
        }
        void use(BlockPos target, Direction face, boolean canPlace) {
            var input = Proto.msg(SID, 1, "interact");
            input.addProperty("requestId", UUID.randomUUID().toString());
            input.addProperty("pressed", true);
            input.addProperty("target", true);
            input.addProperty("x", target.getX()); input.addProperty("y", target.getY()); input.addProperty("z", target.getZ());
            input.addProperty("face", face.get3DDataValue());
            input.addProperty("hitX", target.getX() + .5 + face.getStepX() * .5);
            input.addProperty("hitY", target.getY() + .5 + face.getStepY() * .5);
            input.addProperty("hitZ", target.getZ() + .5 + face.getStepZ() * .5);
            input.addProperty("terrain", false); input.addProperty("canPlace", canPlace);
            interaction.interact(player, input, SID);
            var release = Proto.msg(SID, 1, "interact");
            release.addProperty("requestId", UUID.randomUUID().toString());
            release.addProperty("pressed", false); release.addProperty("target", false);
            interaction.interact(player, release, SID);
        }
    }
}

