package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.Vec3;
import java.util.List;
import java.util.Set;

/** Vanilla charged shots, native flight/collisions, actual damage receipts and dispenser redstone use. */
public final class ProjectileRangeGameTest {
    private static final int MAX_DISTANT_ATTEMPTS = 4;
    private record Shot(Item item, String kind, double speed, int charge, double distance) { }
    private static final List<Shot> SHOTS = List.of(
            new Shot(Items.BOW, "bow", 3, 22, 20), new Shot(Items.CROSSBOW, "crossbow", 3.15, 30, 20),
            new Shot(Items.TRIDENT, "trident", 2.5, 12, 20),
            new Shot(Items.BOW, "bow", 3, 22, 40), new Shot(Items.CROSSBOW, "crossbow", 3.15, 30, 40),
            new Shot(Items.TRIDENT, "trident", 2.5, 12, 40),
            new Shot(Items.BOW, "bow", 3, 22, 80), new Shot(Items.CROSSBOW, "crossbow", 3.15, 30, 80),
            new Shot(Items.TRIDENT, "trident", 2.5, 12, 80));

    // NativeWorldGuard owns one active session. Existing fixture ends no later than tick 1155.
    @GameTest(maxTicks = 1000, setupTicks = 1200, skyAccess = true, padding = 16)
    public void dispenserAndChargedRangedReach(GameTestHelper helper) {
        var fixture = new ProjectileNativeFixture(helper);
        DispenserProjectileGameTest.run(fixture, () -> shoot(fixture, 0));
    }

    private static void shoot(ProjectileNativeFixture fixture, int index) {
        var helper = fixture.helper;
        if (index == SHOTS.size()) {
            fixture.finished = true;
            helper.assertTrue(fixture.fixtureTickets.size() == 1,
                    "range fixture never masks native route liveness with forced corridor chunks");
            fixture.assertForeignPreserved();
            fixture.discardTargets();
            WeaponRuntime.clear();
            helper.assertTrue(NativeProjectileTicks.centreCount() == 0 && NativeProjectileTicks.fullChunkCount() == 0,
                    "explicit session retirement removes every owned flight ticket reference");
            helper.runAfterDelay(5, () -> {
                helper.assertTrue(!helper.getLevel().isPositionEntityTicking(BlockPos.containing(fixture.origin.add(0, 0, 80))),
                        "native chunk manager releases the distant simulation footprint after session cleanup");
                BridgeRuntime.log("PROJECTILE_RANGE_PASS: native bow/crossbow/trident at20/40/80m; dispenser state/damage/ammo/foreign ownership; native distant ticket cleanup");
                helper.succeed();
            });
            return;
        }
        Shot shot = SHOTS.get(index);
        int initialAmmo = shot.distance() == 80 ? MAX_DISTANT_ATTEMPTS : 2;
        fixture.player.getInventory().setItem(1, new ItemStack(Items.ARROW, initialAmmo));
        if (shot.item() == Items.TRIDENT && shot.distance() == 80)
            for (int slot = 2; slot <= MAX_DISTANT_ATTEMPTS; slot++)
                fixture.player.getInventory().setItem(slot, new ItemStack(Items.TRIDENT));
        shootAttempt(fixture, index, 1, new ItemStack(shot.item()), initialAmmo);
    }

    private static void shootAttempt(ProjectileNativeFixture fixture, int index, int attempt, ItemStack weapon, int initialAmmo) {
        var helper = fixture.helper;
        Shot shot = SHOTS.get(index);
        int firstActor = 21000 + index * 10;
        var bank = fixture.targetBank(firstActor, shot.distance());
        double launchHeight = fixture.player.getEyeHeight() - .1;
        double angle = nativeShotAngle(shot.speed(), shot.distance(), launchHeight, .9);
        fixture.pitch = (float) -Math.toDegrees(angle);
        fixture.applyPose();
        fixture.foreign.setPos(fixture.origin.x, fixture.origin.y, fixture.origin.z + 3.3);
        fixture.player.setItemSlot(EquipmentSlot.MAINHAND, weapon);
        Set<String> previousIds = fixture.projectileIds();
        BlockPos targetPos = BlockPos.containing(fixture.origin.add(0, 0, shot.distance()));
        boolean initialTargetTicking = helper.getLevel().isPositionEntityTicking(targetPos);
        BridgeRuntime.log("PROJECTILE_RANGE_START weapon=" + shot.kind() + " worldDistance=" + shot.distance()
                + " attempt=" + attempt + " pitch=" + fixture.pitch + " eyeHeight=" + fixture.player.getEyeHeight()
                + " targetLoaded=" + helper.getLevel().isLoaded(targetPos) + " targetEntityTicking=" + initialTargetTicking
                + " fixtureForcedChunks=" + fixture.fixtureTickets.size());
        fixture.press();
        helper.assertTrue(fixture.player.isUsingItem(), "real " + shot.kind() + " charge starts at " + shot.distance() + "m");
        helper.runAfterDelay(shot.charge(), () -> {
            int requiredCharge = shot.item() == Items.BOW ? 20 : shot.item() == Items.CROSSBOW
                    ? CrossbowItem.getChargeDuration(weapon, fixture.player) : 10;
            if (!fixture.player.isUsingItem() || fixture.player.getTicksUsingItem() < requiredCharge)
                BridgeRuntime.log("PROJECTILE_CHARGE_DIAGNOSTIC weapon=" + shot.kind() + " worldDistance=" + shot.distance()
                        + " using=" + fixture.player.isUsingItem() + " useTicks=" + fixture.player.getTicksUsingItem()
                        + " required=" + requiredCharge + " main=" + fixture.player.getMainHandItem()
                        + " active=" + fixture.player.getUseItem());
            helper.assertTrue(fixture.player.isUsingItem() && fixture.player.getTicksUsingItem() >= requiredCharge,
                    "actual native player ticks meet the vanilla weapon's full charge requirement");
            if (shot.item() == Items.CROSSBOW)
                helper.assertTrue(CrossbowItem.isCharged(weapon), "native crossbow loads its real charged-projectiles component");
            fixture.release();
            if (shot.item() == Items.CROSSBOW) { fixture.press(); fixture.release(); }
            Set<String> launchedIds = fixture.projectileIds();
            launchedIds.removeAll(previousIds);
            helper.assertTrue(launchedIds.size() == 1, "native charged release spawns exactly one actual projectile");
            String sourceId = launchedIds.iterator().next();
            JsonObject launch = fixture.states.stream().filter(state -> Proto.type(state).equals("projectile_state")
                    && Proto.string(state, "entityId").equals(sourceId)).findFirst().orElseThrow();
            Vec3 velocity = new Vec3(Proto.number(launch, "vx"), Proto.number(launch, "vy"), Proto.number(launch, "vz"));
            helper.assertTrue(Math.abs(velocity.length() - shot.speed()) < .15 && fixture.pitch < 0,
                    "native initial speed and real upward aim are preserved without a flight multiplier");
            int nativeDurability = shot.item() == Items.TRIDENT
                    ? ((net.minecraft.world.entity.projectile.arrow.ThrownTrident) helper.getLevel()
                        .getEntity(java.util.UUID.fromString(sourceId))).getPickupItemStackOrigin().getDamageValue()
                    : weapon.getDamageValue();
            helper.assertTrue(nativeDurability == (shot.item() == Items.TRIDENT ? 1 : attempt),
                    "every real attempt costs exactly one native durability on its genuine stack");
            helper.assertTrue(shot.item() == Items.TRIDENT ? fixture.player.getMainHandItem().isEmpty()
                            : fixture.player.getInventory().getItem(1).getCount() == initialAmmo - attempt,
                    "native shot consumes the actual arrow or removes the genuine thrown trident");
            int nominalFlightTicks = nativeFlightTicks(shot.speed(), shot.distance(), angle);
            int flightTicks = nominalFlightTicks + (shot.distance() == 80 ? 2 : 8);
            long launchedAt = helper.getTick();
            final Runnable[] verifyImpact = { null };
            verifyImpact[0] = () -> {
                var hits = fixture.reliable.stream().filter(message -> Proto.type(message).equals("weapon_hit")
                        && Proto.string(message, "sourceId").equals(sourceId)).toList();
                var flight = fixture.states.stream().filter(state -> Proto.type(state).equals("projectile_state")
                        && Proto.string(state, "entityId").equals(sourceId)).toList();
                var actual = helper.getLevel().getEntity(java.util.UUID.fromString(sourceId));
                if (hits.isEmpty() && actual != null && actual.tickCount < flightTicks
                        && helper.getTick() - launchedAt < flightTicks + 80) {
                    // Async promotion may temporarily pause an entity. Wait for its real native ticks,
                    // never advance it manually or count wall ticks as projectile motion.
                    // GameTest keys scheduled actions by Runnable; a new wrapper must survive
                    // removal of the currently executing action from that native scheduler.
                    helper.runAfterDelay(2, () -> verifyImpact[0].run());
                    return;
                }
                if (hits.isEmpty() && shot.distance() == 80 && attempt < MAX_DISTANT_ATTEMPTS) {
                    double actualProgress = flight.stream().mapToDouble(state -> Proto.number(state, "z") - fixture.origin.z)
                            .max().orElse(0);
                    long actualTicks = flight.stream().mapToLong(state -> Proto.integer(state, "nativeTick")).max().orElse(0);
                    helper.assertTrue(actualProgress >= shot.distance() * .95 && actualTicks >= nominalFlightTicks - 6,
                            "a real distant retry requires native flight to reach the vicinity of the 80m bank");
                    helper.assertTrue(bank.stream().allMatch(target -> target.getHealth() == target.getMaxHealth()),
                            "a native spread miss must never fabricate proxy damage");
                    BridgeRuntime.log("PROJECTILE_RANGE_MISS weapon=" + shot.kind() + " worldDistance=" + shot.distance()
                            + " attempt=" + attempt + " sourceId=" + sourceId + " nativeTicks=" + actualTicks
                            + " actualProgress=" + actualProgress + " pitch=" + fixture.pitch
                            + " launchVelocity=" + velocity + " lastState=" + flight.getLast()
                            + "; firing another genuine fully charged shot with original vanilla spread");
                    ItemStack nextWeapon = weapon;
                    if (shot.item() == Items.TRIDENT) {
                        nextWeapon = fixture.player.getInventory().getItem(attempt + 1);
                        helper.assertTrue(nextWeapon.is(Items.TRIDENT) && nextWeapon.getCount() == 1,
                                "trident retry moves one of the fixture's remaining genuine inventory stacks");
                        fixture.player.getInventory().setItem(attempt + 1, ItemStack.EMPTY);
                    }
                    fixture.assertForeignPreserved();
                    shootAttempt(fixture, index, attempt + 1, nextWeapon, initialAmmo);
                    return;
                }
                if (hits.isEmpty()) BridgeRuntime.log("PROJECTILE_RANGE_DIAGNOSTIC weapon=" + shot.kind()
                        + " worldDistance=" + shot.distance() + " sourceId=" + sourceId + " velocity=" + velocity
                        + " angle=" + fixture.pitch + " targetEntityTicking=" + helper.getLevel().isPositionEntityTicking(targetPos)
                        + " initialTargetEntityTicking=" + initialTargetTicking + " targets="
                        + bank.stream().map(target -> target.getBoundingBox().toString()).toList()
                        + " actualFlight=" + fixture.states.stream().filter(state -> Proto.type(state).equals("projectile_state")
                            && Proto.string(state, "entityId").equals(sourceId)).toList());
                helper.assertTrue(hits.size() == 1 && Proto.number(hits.getFirst(), "damage") > 0
                                && bank.stream().anyMatch(target -> target.actorId() == Proto.integer(hits.getFirst(), "eid")),
                        "real " + shot.kind() + " flight reaches its " + shot.distance() + "m bank and captures one positive native impact");
                helper.assertTrue(bank.stream().allMatch(target -> target.getHealth() == target.getMaxHealth()),
                        "capture-only native actor proxies keep their native health");
                helper.assertTrue(flight.stream().map(state -> Proto.number(state, "z")).distinct().count() > 3,
                        "host states mirror actual flight rather than a fabricated instant hit");
                BridgeRuntime.log("PROJECTILE_RANGE_HIT weapon=" + shot.kind() + " worldDistance=" + shot.distance()
                        + " attempts=" + attempt + " sourceId=" + sourceId + " pitch=" + fixture.pitch + " launchVelocity=" + velocity
                        + " nativeDamage=" + Proto.number(hits.getFirst(), "damage") / MycraftBridge.CONFIG.mcToBlDamageFactor
                        + " flightTicks=" + flightTicks + " lastState=" + flight.getLast());
                fixture.assertForeignPreserved();
                shoot(fixture, index + 1);
            };
            helper.runAfterDelay(flightTicks, () -> verifyImpact[0].run());
        });
    }

    /** Test-only aim solver: advance position, apply the native float air drag, then native gravity. */
    private static double nativeShotAngle(double speed, double distance, double launchY, double targetY) {
        double previous = 0;
        for (double angle = Math.toRadians(.25); angle < Math.toRadians(80); angle += Math.toRadians(.25)) {
            if (heightAtDistance(speed, distance, launchY, angle) >= targetY) {
                double low = previous, high = angle;
                for (int iteration = 0; iteration < 40; iteration++) {
                    double middle = (low + high) / 2;
                    if (heightAtDistance(speed, distance, launchY, middle) < targetY) low = middle; else high = middle;
                }
                return (low + high) / 2;
            }
            previous = angle;
        }
        throw new IllegalArgumentException("native ballistic target is unreachable at " + distance + "m");
    }
    private static double heightAtDistance(double speed, double distance, double launchY, double angle) {
        double z = 0, y = launchY, vz = speed * Math.cos(angle), vy = speed * Math.sin(angle);
        for (int tick = 0; tick < 300 && vz > 1.0e-6; tick++) {
            if (z + vz >= distance) return y + vy * (distance - z) / vz;
            z += vz; y += vy; vz *= (double) .99F; vy = vy * (double) .99F - .05;
        }
        return Double.NEGATIVE_INFINITY;
    }
    private static int nativeFlightTicks(double speed, double distance, double angle) {
        double z = 0, vz = speed * Math.cos(angle);
        for (int tick = 1; tick < 300; tick++) {
            z += vz;
            if (z >= distance) return tick;
            vz *= (double) .99F;
        }
        throw new IllegalArgumentException("native target requires too many flight ticks");
    }
}
