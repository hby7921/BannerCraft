package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.projectile.arrow.Arrow;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.DispenserBlock;
import net.minecraft.world.level.block.LeverBlock;
import net.minecraft.world.level.block.entity.DispenserBlockEntity;
import net.minecraft.world.level.block.state.properties.AttachFace;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;

/** Runs first within the serial projectile GameTest so global session ownership never overlaps. */
final class DispenserProjectileGameTest {
    static void run(ProjectileNativeFixture fixture, Runnable next) {
        var helper = fixture.helper;
        var level = helper.getLevel();
        BlockPos source = BlockPos.containing(fixture.origin);
        // The fixture owns one source ticking chunk. Random GameTest coordinates
        // may put +3 across its edge; use the other real floor cell in that case.
        int offsetX = (source.getX() & 15) <= 12 ? 3 : -3;
        BlockPos dispenser = source.offset(offsetX, 0, 0);
        BlockPos lever = dispenser.above();
        fixture.placeOwned(dispenser, Blocks.DISPENSER.defaultBlockState().setValue(DispenserBlock.FACING, Direction.SOUTH));
        fixture.placeOwned(lever, Blocks.LEVER.defaultBlockState().setValue(LeverBlock.FACE, AttachFace.FLOOR)
                .setValue(LeverBlock.FACING, Direction.SOUTH));
        var inventory = (DispenserBlockEntity) level.getBlockEntity(dispenser);
        helper.assertTrue(inventory != null, "dispenser owns its real native container");
        inventory.setItem(0, new ItemStack(Items.ARROW, 2));
        inventory.setChanged();
        var target = BridgeTargetEntity.spawn(level.getServer(), 20001,
                dispenser.getX() + .5, fixture.origin.y, dispenser.getZ() + 4.5);
        helper.assertTrue(target != null, "dispenser has a real native living target");
        fixture.targets.add(target);
        fixture.foreign.setPos(dispenser.getX() + .5, fixture.origin.y, dispenser.getZ() + 2.5);
        var foreignArrow = new Arrow(level, fixture.origin.x - 3, fixture.origin.y + 1, fixture.origin.z,
                new ItemStack(Items.ARROW), null);
        helper.assertTrue(level.addFreshEntity(foreignArrow) && !WeaponRuntime.ownsProjectile(foreignArrow)
                && !NativeWorldGuard.ownsEntity(foreignArrow), "an arbitrary ownerless world arrow is never adopted");
        helper.assertTrue(!WeaponRuntime.captures(target.actorId(), level.damageSources().arrow(foreignArrow, null)),
                "foreign ownerless arrow damage is not forwarded as session damage");
        NativeTestCleanup.register(helper, foreignArrow::discard);
        fixture.player.setItemSlot(EquipmentSlot.MAINHAND, ItemStack.EMPTY);
        final Runnable[] verify = { null };
        long[] activatedAt = { -1 };
        verify[0] = () -> {
            var hits = fixture.reliable.stream().filter(message -> Proto.type(message).equals("weapon_hit")
                    && Proto.integer(message, "eid") == target.actorId()).toList();
            if (hits.isEmpty() && helper.getTick() - activatedAt[0] < 160) {
                // The source block tick and genuine arrow horizon are native async
                // promotions. Advance neither one from this fixture and never retrigger.
                helper.runAfterDelay(2, () -> verify[0].run());
                return;
            }
            if (hits.isEmpty()) BridgeRuntime.log("DISPENSER_FLIGHT_DIAGNOSTIC ammo=" + inventory.getItem(0)
                    + " powered=" + level.getBlockState(lever).getValue(LeverBlock.POWERED)
                    + " triggered=" + level.getBlockState(dispenser).getValue(DispenserBlock.TRIGGERED)
                    + " target=" + target.getBoundingBox() + " states=" + fixture.states);
            helper.assertTrue(hits.size() == 1 && Proto.number(hits.getFirst(), "damage") > 0,
                    "one actual dispenser arrow publishes one positive native damage receipt");
            helper.assertTrue(inventory.getItem(0).getCount() == 1, "one powered dispenser activation consumes exactly one real arrow");
            String sourceId = Proto.string(hits.getFirst(), "sourceId");
            helper.assertTrue(fixture.projectileIds().contains(sourceId)
                    && !sourceId.equals(foreignArrow.getUUID().toString()), "dispenser arrow state and hit share their native source UUID");
            helper.assertTrue(fixture.states.stream().filter(message -> Proto.type(message).equals("projectile_state")
                    && Proto.string(message, "entityId").equals(sourceId))
                    .map(message -> Proto.number(message, "z")).distinct().count() > 2,
                    "dispenser fires a visible moving native projectile rather than only playing its sound");
            helper.assertTrue(!foreignArrow.isRemoved() && !WeaponRuntime.ownsProjectile(foreignArrow),
                    "session tracking preserves the original foreign arrow");
            fixture.assertForeignPreserved();
            BridgeRuntime.log("DISPENSER_PROJECTILE_PASS sourceId=" + sourceId + " nativeDamage="
                    + Proto.number(hits.getFirst(), "damage") / MycraftBridge.CONFIG.mcToBlDamageFactor
                    + " ammo=" + inventory.getItem(0).getCount());
            fixture.discardTargets();
            next.run();
        };
        final Runnable[] awaitSource = { null };
        awaitSource[0] = () -> {
            if (!level.isPositionTickingWithEntitiesLoaded(net.minecraft.world.level.ChunkPos.pack(dispenser.getX() >> 4, dispenser.getZ() >> 4))) {
                if (helper.getTick() >= 600) BridgeRuntime.log("DISPENSER_SOURCE_TIMEOUT source=" + source
                        + " dispenser=" + dispenser + " baseTicking=" + level.isPositionEntityTicking(source)
                        + " blockTicking=" + level.shouldTickBlocksAt(net.minecraft.world.level.ChunkPos.pack(dispenser.getX() >> 4, dispenser.getZ() >> 4)));
                helper.assertTrue(helper.getTick() < 600, "actual dispenser source reaches native loaded block ticking before its hard timeout");
                helper.runAfterDelay(2, () -> awaitSource[0].run());
                return;
            }
            NativeWorldGuard.enter(level, lever);
            try {
                var result = fixture.player.gameMode.useItemOn(fixture.player, level, ItemStack.EMPTY, InteractionHand.MAIN_HAND,
                        new BlockHitResult(Vec3.atCenterOf(lever), Direction.UP, lever, false));
                helper.assertTrue(result.consumesAction(), "actual native block use toggles the lever exactly once after source readiness");
            } finally { NativeWorldGuard.exit(); }
            helper.assertTrue(level.getBlockState(lever).getValue(LeverBlock.POWERED), "native lever supplies real redstone power");
            activatedAt[0] = helper.getTick();
            BridgeRuntime.log("DISPENSER_NATIVE_SOURCE_READY helperTick=" + activatedAt[0] + " source=" + dispenser);
            helper.runAfterDelay(24, () -> verify[0].run());
        };
        awaitSource[0].run();
    }
}

