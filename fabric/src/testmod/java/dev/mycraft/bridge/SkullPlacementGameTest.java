package dev.mycraft.bridge;

import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.Difficulty;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.SkullBlock;
import net.minecraft.world.level.block.WallSkullBlock;
import net.minecraft.world.level.block.state.properties.RotationSegment;
import net.minecraft.world.phys.AABB;
import java.util.ArrayList;
import java.util.UUID;

/** Real survival placement and original WitherSkullBlock construction, never synthetic block success. */
public final class SkullPlacementGameTest {
    @GameTest(maxTicks = 120, setupTicks = 3900, skyAccess = true, padding = 16)
    public void originalSkullPlacementAndWitherStructure(GameTestHelper helper) {
        var f = new ProjectileNativeFixture(helper);
        var level = helper.getLevel();
        var oldDifficulty = level.getDifficulty();
        level.getServer().setDifficulty(Difficulty.NORMAL, true);
        MobRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        var entities = new ArrayList<Entity>();
        NativeTestCleanup.register(helper, () -> {
            for (var entity : entities) entity.discard();
            MobRuntime.clear(); OwnedEntityTicks.clear();
            level.getServer().setDifficulty(oldDifficulty, true);
        });
        f.player.setItemSlot(EquipmentSlot.OFFHAND, ItemStack.EMPTY);
        BlockPos base = BlockPos.containing(f.origin);
        BlockPos sand = base.offset(-2, 0, 0);
        f.placeOwned(sand, Blocks.SOUL_SAND.defaultBlockState());
        f.player.setYRot(63);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.WITHER_SKELETON_SKULL, 4));
        use(f, sand, Direction.UP, true);
        var skull = level.getBlockState(sand.above());
        helper.assertTrue(skull.is(Blocks.WITHER_SKELETON_SKULL)
                && skull.getValue(SkullBlock.ROTATION) == RotationSegment.convertToSegment(63)
                && f.player.getMainHandItem().getCount() == 3 && f.building.ownsVisible(sand.above()),
                "native survival head placement on soul sand preserves exact yaw, ownership and one-item consumption");
        helper.assertTrue(skull.getShape(level, sand.above()).bounds().equals(new AABB(.25, 0, .25, .75, .5, .75))
                && skull.getCollisionShape(level, sand.above()).bounds().equals(new AABB(.25, 0, .25, .75, .5, .75)),
                "native skull remains its real half-block shape for host rays and collisions");
        use(f, sand, Direction.SOUTH, true);
        var wall = level.getBlockState(sand.south());
        helper.assertTrue(wall.is(Blocks.WITHER_SKELETON_WALL_SKULL)
                && wall.getValue(WallSkullBlock.FACING) == Direction.SOUTH
                && f.player.getMainHandItem().getCount() == 2,
                "native wall skull has original attachment facing and a real inventory debit");
        use(f, sand, Direction.EAST, false);
        helper.assertTrue(level.getBlockState(sand.east()).isAir() && f.player.getMainHandItem().getCount() == 2,
                "genuine rejected placement never turns into a false head or an inventory debit");

        // T-shaped soul sand plus three genuine BlockItem uses must execute the
        // installed client's own WitherSkullBlock.checkSpawn and makeInvulnerable.
        BlockPos stem = base.offset(1, 0, 2);
        structure(f, stem, null);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.WITHER_SKELETON_SKULL, 3));
        for (int x = -1; x <= 1; x++) use(f, stem.offset(x, 1, 0), Direction.UP, true);
        var bosses = level.getEntitiesOfClass(WitherBoss.class, new AABB(stem).inflate(5));
        helper.assertTrue(bosses.size() == 1, "the exact native T structure creates one actual Wither boss");
        WitherBoss boss = bosses.getFirst(); entities.add(boss);
        helper.assertTrue(MobRuntime.owns(boss) && NativeWorldGuard.ownsEntity(boss)
                && boss.getInvulnerableTicks() == 220 && boss.getHealth() == boss.getMaxHealth() / 3
                && f.player.getMainHandItem().isEmpty(),
                "native Wither construction keeps its original spawn phase, initial health and three-head consumption");
        for (BlockPos pos : structureCells(stem)) helper.assertTrue(level.getBlockState(pos).isAir()
                && !f.building.ownsVisible(pos), "native structure cleanup consumes only its own real construction at " + pos);
        boss.discard();

        // A mixed original-world structure must never erase the foreign soul sand
        // merely because its last head is placed inside this session.
        BlockPos foreignStem = base.offset(-2, 0, 5);
        BlockPos foreignArm = foreignStem.offset(-1, 1, 0);
        structure(f, foreignStem, foreignArm);
        f.player.connection.teleport(foreignStem.getX() + .5, foreignStem.getY(), foreignStem.getZ() - 1.5, 0, 0);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.WITHER_SKELETON_SKULL, 3));
        for (int x = -1; x <= 1; x++) {
            if (x == -1) {
                // The original-world support is not interactable from the host;
                // install its preexisting head outside scope to complete the mixed pattern.
                level.setBlock(foreignArm.above(), Blocks.WITHER_SKELETON_SKULL.defaultBlockState(), Block.UPDATE_ALL);
            } else use(f, foreignStem.offset(x, 1, 0), Direction.UP, true);
        }
        helper.assertTrue(level.getEntitiesOfClass(WitherBoss.class, new AABB(foreignStem).inflate(5)).isEmpty()
                && level.getBlockState(foreignArm).is(Blocks.SOUL_SAND)
                && !f.building.ownsNative(foreignArm),
                "native summon guard preserves a foreign body block and refuses mixed-world structure consumption");
        for (BlockPos pos : structureCells(foreignStem)) helper.assertTrue(!level.getBlockState(pos).isAir(),
                "rejected mixed-world structure retains every actual placed component");
        // Fixture-only foreign cells are restored explicitly outside production scope.
        level.setBlock(foreignArm.above(), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        level.setBlock(foreignArm, Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);

        // Capacity refusal must happen before the native method clears its pattern.
        for (int i = 0; i < 16; i++) {
            var filler = EntityTypes.WITHER_SKELETON.create(level, EntitySpawnReason.TRIGGERED);
            filler.setNoAi(true); filler.setPos(f.origin.add(0, 0, 12 + i));
            NativeWorldGuard.enter(level, filler.blockPosition());
            try { helper.assertTrue(level.addFreshEntity(filler), "real native capacity filler is accepted"); }
            finally { NativeWorldGuard.exit(); }
            entities.add(filler);
        }
        f.applyPose();
        BlockPos fullStem = base.offset(1, 0, -2);
        structure(f, fullStem, null);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.WITHER_SKELETON_SKULL, 3));
        for (int x = -1; x <= 1; x++) use(f, fullStem.offset(x, 1, 0), Direction.UP, true);
        helper.assertTrue(level.getEntitiesOfClass(WitherBoss.class, new AABB(fullStem).inflate(5)).isEmpty(),
                "actual mob capacity cannot produce a seventeenth boss");
        for (BlockPos pos : structureCells(fullStem)) helper.assertTrue(!level.getBlockState(pos).isAir(),
                "full mob capacity preserves native structure blocks before clearPatternBlocks");
        BridgeRuntime.log("SKULL_PLACEMENT_PASS: real Survival soul-sand top/wall skull facing and shape; original T-structure Wither phase; foreign structure and full-capacity preservation");
        helper.succeed();
    }

    private static void structure(ProjectileNativeFixture f, BlockPos stem, BlockPos foreignArm) {
        f.placeOwned(stem, Blocks.SOUL_SAND.defaultBlockState());
        for (int x = -1; x <= 1; x++) {
            BlockPos arm = stem.offset(x, 1, 0);
            if (arm.equals(foreignArm)) f.helper.getLevel().setBlock(arm, Blocks.SOUL_SAND.defaultBlockState(), Block.UPDATE_ALL);
            else f.placeOwned(arm, Blocks.SOUL_SAND.defaultBlockState());
        }
    }
    private static java.util.List<BlockPos> structureCells(BlockPos stem) {
        var cells = new ArrayList<BlockPos>(); cells.add(stem);
        for (int x = -1; x <= 1; x++) { cells.add(stem.offset(x, 1, 0)); cells.add(stem.offset(x, 2, 0)); }
        return cells;
    }
    private static void use(ProjectileNativeFixture f, BlockPos target, Direction face, boolean canPlace) {
        var message = Proto.msg(ProjectileNativeFixture.SID, 1, "interact");
        message.addProperty("requestId", UUID.randomUUID().toString());
        message.addProperty("pressed", true); message.addProperty("target", true);
        message.addProperty("x", target.getX()); message.addProperty("y", target.getY()); message.addProperty("z", target.getZ());
        message.addProperty("face", face.get3DDataValue());
        message.addProperty("hitX", target.getX() + .5 + face.getStepX() * .5);
        message.addProperty("hitY", target.getY() + .5 + face.getStepY() * .5);
        message.addProperty("hitZ", target.getZ() + .5 + face.getStepZ() * .5);
        message.addProperty("terrain", false); message.addProperty("canPlace", canPlace);
        f.interaction.interact(f.player, message, ProjectileNativeFixture.SID);
        var release = Proto.msg(ProjectileNativeFixture.SID, 1, "interact");
        release.addProperty("requestId", UUID.randomUUID().toString()); release.addProperty("pressed", false); release.addProperty("target", false);
        f.interaction.interact(f.player, release, ProjectileNativeFixture.SID);
    }
}

