package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.Difficulty;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Blocks;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;
import java.util.UUID;

/** Host-produced bounded ground messages consumed by an actual naturally falling vanilla Wither. */
public final class EntityGroundNativeGameTest {
    @GameTest(maxTicks = 1000, setupTicks = 7600, skyAccess = true, padding = 16)
    public void hostFilteredGroundKeepsNativeFlyingMobSession(GameTestHelper helper) {
        var f = new ProjectileNativeFixture(helper);
        var level = helper.getLevel();
        Difficulty oldDifficulty = level.getDifficulty();
        level.getServer().setDifficulty(Difficulty.NORMAL, true);
        f.player.setGameMode(GameType.CREATIVE);
        MobRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        ActorRegistry<BridgeTargetEntity> actors = actors();
        helper.assertTrue(actors.values().isEmpty(), "ground regression starts after all previous real native sessions ended");
        var opponent = actors.getOrCreate(32701, () -> BridgeTargetEntity.spawn(level.getServer(), 32701,
                f.origin.x + 3, f.origin.y, f.origin.z + 5));
        opponent.setHostile(false); opponent.setHostHealth(5000, 5000);
        JsonObject producer = producer();
        helper.assertTrue(Proto.integer(producer, "schema") == 1, "host ground fixture schema is explicit");
        JsonObject request = Proto.msg(ProjectileNativeFixture.SID, 1, "mc_army_spawn");
        request.addProperty("requestId", UUID.randomUUID().toString()); request.addProperty("count", 1);
        request.addProperty("playerSide", false); request.addProperty("mob0Kind", "minecraft:wither");
        request.addProperty("mob0Slot", 32702); request.addProperty("mob0X", f.origin.x);
        request.addProperty("mob0Y", f.origin.y + 20.25); request.addProperty("mob0Z", f.origin.z);
        helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, request).accepted(),
                "actual airborne Wither uses the production asynchronous owned army spawn");
        WitherBoss[] boss = { null }; JsonObject[] near = { null };
        int[] phase = { 0 }; double[] highestY = { Double.NEGATIVE_INFINITY }, lowestY = { Double.POSITIVE_INFINITY };
        BlockPos sample = BlockPos.containing(f.origin);
        helper.assertTrue(level.getBlockState(sample).isAir() && level.getBlockState(sample.below()).is(Blocks.STONE),
                "real support destination starts as air above the unchanged original fixture floor");
        NativeTestCleanup.register(helper, () -> {
            MobRuntime.clear(); OwnedEntityTicks.clear(); actors.clear();
            if (boss[0] != null) boss[0].discard();
            level.getServer().setDifficulty(oldDifficulty, true);
        });
        helper.onEachTick(() -> {
            if (f.finished) return;
            MobRuntime.tick(); OwnedEntityTicks.tick();
            if (phase[0] == 0 && ArmySpawnRuntime.pendingCount() == 0) {
                JsonObject result = f.reliable.stream().filter(m -> Proto.type(m).equals("mc_army_spawn_result")
                        && Proto.string(m, "requestId").equals(Proto.string(request, "requestId"))).findFirst().orElseThrow();
                helper.assertTrue(Proto.bool(result, "ok"), "real airborne native army spawn completed");
                boss[0] = assignedBoss(32702);
                helper.assertTrue(boss[0] != null && MobRuntime.owns(boss[0]) && boss[0].getInvulnerableTicks() >= 218
                        && boss[0].getHealth() == 100 && Math.abs(boss[0].getY() - (f.origin.y + 20.25)) < .05,
                        "real original Wither starts airborne with its original 220-tick birth and 100 HP");
                JsonObject raw = relocate(producer.getAsJsonObject("airborneRaw"), f, boss[0]);
                JsonObject filtered = relocate(producer.getAsJsonObject("airborneFiltered"), f, boss[0]);
                near[0] = relocate(producer.getAsJsonObject("nearFiltered"), f, boss[0]);
                helper.assertTrue(Proto.integer(raw, "groundCount") == 1 && Proto.integer(filtered, "groundCount") == 0
                        && Proto.integer(near[0], "groundCount") == 1 && Proto.number(near[0], "ground0H") == .25,
                        "native regression consumes actual host raw/filtered/fractional support fixture output");
                long writesBefore = NativeWorldGuard.observedMutationCount();
                boolean rejected = false;
                try { MobRuntime.prepareGround(raw, f.building); }
                catch (IllegalArgumentException expected) {
                    helper.assertTrue(expected.getMessage().equals("ground sample outside bounded TNT terrain patch"),
                            "unfiltered remote floor fails the original precise receiver bound");
                    rejected = true;
                }
                helper.assertTrue(rejected && NativeWorldGuard.observedMutationCount() == writesBefore
                        && level.getBlockState(sample).isAir() && !f.building.ownsNative(sample),
                        "raw out-of-range host floor remains rejected atomically without an invented support");
                var before = boss[0].position(); float hpBefore = boss[0].getHealth();
                MobRuntime.prepareGround(filtered, f.building);
                helper.assertTrue(MobRuntime.owns(boss[0]) && boss[0].position().equals(before) && boss[0].getHealth() == hpBefore
                        && NativeWorldGuard.observedMutationCount() == writesBefore && level.getBlockState(sample).isAir(),
                        "actual filtered empty message preserves native position/HP/ownership and does not stop the owned runtime");
                highestY[0] = lowestY[0] = boss[0].getY();
                phase[0] = 1;
                BridgeRuntime.log("ENTITY_GROUND_RAW_FILTER_PASS realBossY=" + boss[0].getY() + " sampleY=" + sample.getY());
            } else if (phase[0] == 1) {
                highestY[0] = Math.max(highestY[0], boss[0].getY()); lowestY[0] = Math.min(lowestY[0], boss[0].getY());
                if (boss[0].getY() <= f.origin.y + 16 && boss[0].tickCount > 0) {
                    helper.assertTrue(lowestY[0] < highestY[0] - 3 && boss[0].getInvulnerableTicks() > 0,
                            "the original airborne birth naturally descends under genuine server gravity without manual mob ticks");
                    var positionBefore = boss[0].position(); float hpBefore = boss[0].getHealth();
                    MobRuntime.prepareGround(near[0], f.building);
                    helper.assertTrue(f.building.matchesSupport(sample, HostGroundBlock.state(.25)) && !f.building.ownsVisible(sample)
                            && level.getBlockState(sample.below()).is(Blocks.STONE) && boss[0].position().equals(positionBefore)
                            && boss[0].getHealth() == hpBefore && MobRuntime.owns(boss[0]),
                            "the actual host near-floor message installs quarter-height hidden support while retaining native boss and foreign floor");
                    phase[0] = 2;
                    BridgeRuntime.log("ENTITY_GROUND_NEAR_FILTER_PASS nativeTick=" + boss[0].tickCount + " realY=" + boss[0].getY());
                }
            } else if (phase[0] == 2 && boss[0].tickCount >= 280 && boss[0].getInvulnerableTicks() == 0) {
                helper.assertTrue(boss[0].isAlive() && MobRuntime.owns(boss[0]) && boss[0].getHealth() == 300,
                        "original native birth finishes and heals to 300 HP after bounded ground feedback");
                helper.assertTrue(boss[0].getTarget() == opponent || boss[0].getAlternativeTarget(1) == opponent.getId()
                        || boss[0].getAlternativeTarget(2) == opponent.getId(),
                        "actual original post-birth boss AI still acquires its opposing host soldier after filtered ground");
                helper.assertTrue(level.getBlockState(sample.below()).is(Blocks.STONE),
                        "high-altitude feedback and the original birth blast retain the original foreign floor");
                BridgeRuntime.log("ENTITY_GROUND_NATIVE_PASS rawRejected=true hostEmptyAccepted=true hostNearAccepted=true originalBirthAndAI=true");
                helper.succeed();
            }
        });
    }
    private static JsonObject producer() {
        try {
            String fixture = System.getProperty("bannercraft.entityGroundFixture");
            if (fixture == null || fixture.isBlank()) {
                throw new IllegalStateException("set bannercraft.entityGroundFixture to the host producer fixture path");
            }
            return JsonParser.parseString(Files.readString(Path.of(fixture))).getAsJsonObject();
        } catch (java.io.IOException failure) { throw new IllegalStateException("host producer ground fixture is required", failure); }
    }
    private static JsonObject relocate(JsonObject source, ProjectileNativeFixture f, WitherBoss boss) {
        JsonObject result = source.deepCopy();
        double dx = Math.floor(f.origin.x), dy = f.origin.y - 128, dz = Math.floor(f.origin.z);
        result.addProperty("sid", ProjectileNativeFixture.SID); result.addProperty("entityId", boss.getUUID().toString());
        result.addProperty("x", Proto.number(source, "x") + dx); result.addProperty("y", Proto.number(source, "y") + dy);
        result.addProperty("z", Proto.number(source, "z") + dz);
        for (int index = 0; index < Proto.integer(source, "groundCount"); index++) {
            String prefix = "ground" + index;
            result.addProperty(prefix + "X", Proto.number(source, prefix + "X") + dx);
            result.addProperty(prefix + "Y", Proto.number(source, prefix + "Y") + dy);
            result.addProperty(prefix + "Z", Proto.number(source, prefix + "Z") + dz);
        }
        return result;
    }
    @SuppressWarnings("unchecked")
    private static WitherBoss assignedBoss(int slot) {
        try {
            var field = NativeWorldGuard.class.getDeclaredField("ENTITY_REFS"); field.setAccessible(true);
            return ((Map<UUID, Entity>)field.get(null)).values().stream().filter(e -> e instanceof WitherBoss && MobRuntime.owns(e)
                    && MobRuntime.armyAssignment(e) != null && MobRuntime.armyAssignment(e).slot() == slot)
                    .map(e -> (WitherBoss)e).findFirst().orElse(null);
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
    @SuppressWarnings("unchecked")
    private static ActorRegistry<BridgeTargetEntity> actors() {
        try {
            var field = BridgeRuntime.class.getDeclaredField("PROXIES"); field.setAccessible(true);
            return (ActorRegistry<BridgeTargetEntity>)field.get(null);
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
}
