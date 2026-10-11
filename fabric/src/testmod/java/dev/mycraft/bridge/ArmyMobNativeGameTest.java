package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.Difficulty;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** Real assigned vanilla batches retain native birth/combat/death and precise original army slots. */
public final class ArmyMobNativeGameTest {
    @GameTest(maxTicks = 1300, setupTicks = 6000, skyAccess = true, padding = 16)
    public void assignedArmyBatch(GameTestHelper helper) {
        var f = new ProjectileNativeFixture(helper);
        var level = helper.getLevel();
        Difficulty oldDifficulty = level.getDifficulty();
        level.getServer().setDifficulty(Difficulty.NORMAL, true);
        f.player.setGameMode(GameType.CREATIVE);
        MobRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        ActorRegistry<BridgeTargetEntity> actors = actors();
        helper.assertTrue(actors.values().isEmpty(), "army fixture runs after previous native static sessions have ended");
        var playerArmy = actors.getOrCreate(28001, () -> BridgeTargetEntity.spawn(level.getServer(), 28001,
                f.origin.x, f.origin.y, f.origin.z + 34));
        playerArmy.setHostile(false); playerArmy.setHostHealth(5000, 5000);
        var blastAlly = actors.getOrCreate(28002, () -> BridgeTargetEntity.spawn(level.getServer(), 28002,
                f.origin.x, f.origin.y, f.origin.z + 39));
        blastAlly.setHostile(true); blastAlly.setHostHealth(5000, 5000);
        var enemyArmy = actors.getOrCreate(28003, () -> BridgeTargetEntity.spawn(level.getServer(), 28003,
                f.origin.x, f.origin.y, f.origin.z + 25));
        enemyArmy.setHostile(true); enemyArmy.setHostHealth(5000, 5000);
        JsonObject batch = batch(false, new int[] { 71001, 71002, 71003 },
                List.of(f.origin.add(-3, 1, 40), f.origin.add(0, 1, 40), f.origin.add(3, 1, 40)), "wither");
        MobRuntime.ArmySummonResult submitted = MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, batch);
        helper.assertTrue(submitted.accepted() && !submitted.complete() && ArmySpawnRuntime.pendingCount() == 1,
                "three real Withers queue asynchronously instead of blocking the player on chunk generation");
        helper.assertTrue(f.states.stream().noneMatch(m -> Proto.type(m).equals("mob_state")),
                "a queued batch has no fake or unassigned first-frame creature proxy");
        helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, batch).accepted()
                && ArmySpawnRuntime.pendingCount() == 1 && ArmySpawnRuntime.reservedCount() == 3,
                "an exact pending retry reserves exactly three original slots once");
        f.player.setPos(f.origin.add(0, 0, -160));
        try {
            helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, batch).accepted()
                    && ArmySpawnRuntime.pendingCount() == 1 && ArmySpawnRuntime.reservedCount() == 3,
                    "exact accepted retry remains pending once after the real selected player moves outside its original spawn corridor");
        } finally { f.applyPose(); }
        JsonObject changed = batch.deepCopy(); changed.addProperty("mob0X", Proto.number(batch, "mob0X") + 1);
        helper.assertTrue(!MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, changed).accepted(),
                "same request UUID cannot mutate an army spawn while it is preparing");
        List<WitherBoss> bosses = new ArrayList<>(); Mob[] friendlySkeleton = { null };
        JsonObject[] friendlyBatch = { null }, deathHit = { null };
        int[] phase = { 0 }; long[] beforeDeathReceipts = { 0 };
        boolean[] checkedNativeTargets = { false }, checkedAllyBlast = { false };
        helper.onEachTick(() -> {
            if (f.finished) return;
            MobRuntime.tick(); OwnedEntityTicks.tick();
            helper.assertTrue(ArmySpawnRuntime.centreCount() <= 16 && OwnedEntityTicks.centreCount() <= 32
                    && OwnedEntityTicks.fullChunkCount() <= 96, "army and actual entity leases retain bounded native simulation");
            if (phase[0] == 0 && ArmySpawnRuntime.pendingCount() == 0) {
                helper.assertTrue(result(f, batch).get("ok").getAsBoolean(), "three-Wither batch completes successfully with a reliable result");
                for (int slot : new int[] { 71001, 71002, 71003 }) {
                    Mob mob = assigned(slot);
                    helper.assertTrue(mob instanceof WitherBoss && MobRuntime.owns(mob), "assigned slot contains an actual owned WitherBoss");
                    WitherBoss boss = (WitherBoss)mob; bosses.add(boss);
                    helper.assertTrue(boss.getInvulnerableTicks() >= 218 && boss.getInvulnerableTicks() <= 220
                            && boss.getHealth() == 100 && boss.getMaxHealth() == 300,
                            "every real army boss retains the original 220-tick birth and 100/300 HP");
                    JsonObject first = f.states.stream().filter(m -> Proto.type(m).equals("mob_state")
                            && Proto.string(m, "entityId").equals(boss.getUUID().toString())).findFirst().orElseThrow();
                    helper.assertTrue(Proto.integer(first, "armySlot") == slot && !Proto.bool(first, "armyPlayerSide"),
                            "the very first genuine state maps its original host enemy slot without a None-team duplicate");
                    helper.assertTrue(MobRuntime.canTarget(boss, playerArmy) && MobRuntime.canTarget(boss, f.player)
                            && !MobRuntime.canTarget(boss, enemyArmy) && !MobRuntime.canTarget(boss, f.foreign),
                            "enemy assigned boss acquisition accepts player army/current player while preserving allies and foreign entities");
                    boss.setTarget(enemyArmy);
                    helper.assertTrue(boss.getTarget() != enemyArmy && !boss.canAttack(enemyArmy),
                            "the real native Mob.setTarget and canAttack reject a same-army soldier");
                    helper.assertTrue(MobRuntime.allowedTarget(enemyArmy),
                            "native explosion and stray-projectile eligibility keeps legitimate allied friendly fire");
                }
                int actualBefore = MobRuntime.activeMobCount();
                helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, batch).success()
                        && MobRuntime.activeMobCount() == actualBefore,
                        "exact successful batch retry returns its cached result without generating a fourth Wither");
                String cachedEventId = Proto.string(result(f, batch), "eventId");
                f.player.setPos(f.origin.add(0, 0, -160));
                try {
                    helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, batch).success()
                            && MobRuntime.activeMobCount() == actualBefore && Proto.string(result(f, batch), "eventId").equals(cachedEventId),
                            "completed exact retry after player movement retains original native bodies and its reliable event UUID");
                } finally { f.applyPose(); }
                JsonObject conflict = batch.deepCopy(); conflict.addProperty("requestId", UUID.randomUUID().toString());
                helper.assertTrue(!MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, conflict).success()
                        && MobRuntime.activeMobCount() == 3, "another request cannot adopt or duplicate already-bound host slots");
                friendlyBatch[0] = batch(true, new int[] { 71004 }, List.of(f.origin.add(0, 0, 23)), "wither_skeleton");
                helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, friendlyBatch[0]).accepted(),
                        "friendly native army request is independently queued");
                phase[0] = 1;
                BridgeRuntime.log("MC_ARMY_NATIVE_FIRST_STATE_PASS count=" + bosses.size());
            } else if (phase[0] == 1 && ArmySpawnRuntime.pendingCount() == 0) {
                helper.assertTrue(result(f, friendlyBatch[0]).get("ok").getAsBoolean(), "friendly actual skeleton army completes");
                friendlySkeleton[0] = assigned(71004);
                helper.assertTrue(friendlySkeleton[0] != null && MobRuntime.canTarget(friendlySkeleton[0], enemyArmy)
                        && !MobRuntime.canTarget(friendlySkeleton[0], playerArmy) && !MobRuntime.canTarget(friendlySkeleton[0], f.player),
                        "friendly assigned melee creature acquires only original opposing host actors and excludes the real owner");
                // Keep the hostile boss's real opposing target close to its original
                // spawn. The independent friendly melee lane remains away from its blast.
                phase[0] = 2;
            } else if (phase[0] == 2) {
                for (WitherBoss boss : bosses) if (boss.getInvulnerableTicks() == 0 && boss.tickCount >= 240) {
                    helper.assertTrue(boss.getTarget() != enemyArmy && boss.getAlternativeTarget(1) != enemyArmy.getId()
                            && boss.getAlternativeTarget(2) != enemyArmy.getId() && boss.getTarget() != blastAlly
                            && boss.getAlternativeTarget(1) != blastAlly.getId() && boss.getAlternativeTarget(2) != blastAlly.getId(),
                            "original main and two side heads never choose an original same-army soldier");
                    if (boss.getTarget() == playerArmy || boss.getAlternativeTarget(1) == playerArmy.getId()
                            || boss.getAlternativeTarget(2) == playerArmy.getId()) checkedNativeTargets[0] = true;
                }
                if (friendlySkeleton[0].tickCount >= 60)
                    helper.assertTrue(friendlySkeleton[0].getTarget() != playerArmy && friendlySkeleton[0].getTarget() != f.player,
                            "natural melee goal preserves friendly army ownership while genuine AI navigates");
                if (!checkedAllyBlast[0] && bosses.stream().allMatch(b -> b.tickCount >= 240 && b.getInvulnerableTicks() == 0)) {
                    helper.assertTrue(f.reliable.stream().anyMatch(m -> Proto.type(m).equals("mob_hit")
                            && Proto.integer(m, "eid") == blastAlly.actorId() && Proto.string(m, "kind").equals("explosion")),
                            "actual original birth blast can damage an allied soldier without making him an AI target");
                    checkedAllyBlast[0] = true;
                }
                if (bosses.stream().allMatch(b -> b.tickCount >= 500)
                        && f.reliable.stream().anyMatch(m -> Proto.type(m).equals("mob_hit")
                            && Proto.string(m, "sourceId").equals(friendlySkeleton[0].getUUID().toString())
                            && Proto.integer(m, "eid") == enemyArmy.actorId())) {
                    helper.assertTrue(checkedNativeTargets[0], "actual original boss AI/side-head acquisition chooses the opposing army");
                    helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("projectile_state")
                            && Proto.string(m, "kind").equals("wither_skull")),
                            "assigned original Withers naturally fire original skulls after their genuine birth");
                    // Capacity rejects the entire proposed batch and never quietly trims its members.
                    helper.assertTrue(friendlySkeleton[0].isAlive() && MobRuntime.activeMobCount() == 4,
                            "capacity precondition has four real currently owned native mobs before proposing thirteen more");
                    JsonObject overflow = batch(false, new int[] { 71100,71101,71102,71103,71104,71105,71106,
                            71107,71108,71109,71110,71111,71112 },
                            java.util.stream.IntStream.range(0, 13).mapToObj(i -> f.origin.add((i % 3) * 3 - 3, 1, 55 + (i / 3) * 3)).toList(), "wither");
                    helper.assertTrue(!MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, overflow).success()
                            && MobRuntime.activeMobCount() == 4 && ArmySpawnRuntime.pendingCount() == 0,
                            "a 13-creature overflow rejects completely instead of silently keeping 12");
                    // Spatial all-or-nothing failure is determined by genuine original bodies, no fake overlap allowance.
                    JsonObject overlap = batch(false, new int[] { 71200, 71201 },
                            List.of(f.origin.add(0, 1, 60), f.origin.add(0, 1, 60)), "wither");
                    helper.assertTrue(MobRuntime.summonArmy(ProjectileNativeFixture.SID, f.player, overlap).accepted(), "overlap reaches native asynchronous body inspection");
                    friendlyBatch[0] = overlap; phase[0] = 3;
                }
            } else if (phase[0] == 3 && ArmySpawnRuntime.pendingCount() == 0) {
                helper.assertTrue(!result(f, friendlyBatch[0]).get("ok").getAsBoolean() && MobRuntime.activeMobCount() == 4
                        && assigned(71200) == null && assigned(71201) == null,
                        "native overlapping batch failure creates no partial published army or stranded slot");
                WitherBoss victim = bosses.get(0); victim.setNoAi(true);
                deathHit[0] = hurt(victim, playerArmy, 1000);
                beforeDeathReceipts[0] = f.reliable.stream().filter(m -> Proto.type(m).equals("mob_remove")
                        && Proto.string(m, "entityId").equals(victim.getUUID().toString())).count();
                helper.assertTrue(MobRuntime.hurt(deathHit[0], playerArmy) && !victim.isAlive(),
                        "actual ordinary opposing host soldier damage causes native assigned boss death");
                helper.assertTrue(!level.getEntitiesOfClass(ItemEntity.class, victim.getBoundingBox().inflate(5),
                        item -> item.getItem().is(Items.NETHER_STAR) && NativeWorldGuard.ownsEntity(item)).isEmpty(),
                        "assigned native boss retains the original Nether Star death loot without fabricated rewards");
                phase[0] = 4;
            } else if (phase[0] == 4 && bosses.get(0).isRemoved()) {
                WitherBoss victim = bosses.get(0);
                helper.assertTrue(MobRuntime.hurt(deathHit[0], playerArmy), "exact native lethal receipt stays ACKable after the boss army slot retires");
                long removed = f.reliable.stream().filter(m -> Proto.type(m).equals("mob_remove")
                        && Proto.string(m, "entityId").equals(victim.getUUID().toString())).count();
                helper.assertTrue(removed == beforeDeathReceipts[0] + 1,
                        "one real native boss retirement removes exactly one original UUID/army slot");
                helper.assertTrue(bosses.get(1).isAlive() && bosses.get(2).isAlive(), "one death preserves the other two genuine enemy bosses");
                helper.assertTrue(ArmySpawnRuntime.centreCount() == 0,
                        "completed and failed batch loading tickets have released while actual mobs keep their own leases");
                f.assertForeignPreserved();
                BridgeRuntime.log("MC_ARMY_NATIVE_COMBAT_PASS enemies=3 friendlyMelee=1 actualBossTarget=" + checkedNativeTargets[0]);
                helper.succeed();
            }
        });
        NativeTestCleanup.register(helper, () -> {
            MobRuntime.clear(); OwnedEntityTicks.clear(); actors.clear();
            for (WitherBoss boss : bosses) boss.discard();
            if (friendlySkeleton[0] != null) friendlySkeleton[0].discard();
            level.getServer().setDifficulty(oldDifficulty, true);
            helper.assertTrue(ArmySpawnRuntime.pendingCount() == 0 && ArmySpawnRuntime.centreCount() == 0,
                    "army session cleanup leaves no pending spawn or temporary ticket");
        });
    }
    private static JsonObject batch(boolean playerSide, int[] slots, List<Vec3> positions, String kind) {
        JsonObject message = Proto.msg(ProjectileNativeFixture.SID, 1, "mc_army_spawn");
        message.addProperty("requestId", UUID.randomUUID().toString()); message.addProperty("playerSide", playerSide);
        message.addProperty("count", slots.length);
        for (int index = 0; index < slots.length; index++) {
            String prefix = "mob" + index;
            message.addProperty(prefix + "Kind", "minecraft:" + kind); message.addProperty(prefix + "Slot", slots[index]);
            Vec3 pos = positions.get(index); message.addProperty(prefix + "X", pos.x);
            message.addProperty(prefix + "Y", pos.y); message.addProperty(prefix + "Z", pos.z);
        }
        return message;
    }
    private static JsonObject result(ProjectileNativeFixture f, JsonObject batch) {
        return f.reliable.stream().filter(m -> Proto.type(m).equals("mc_army_spawn_result")
                && Proto.string(m, "requestId").equals(Proto.string(batch, "requestId"))).reduce((a,b) -> b).orElseThrow();
    }
    private static JsonObject hurt(Mob mob, BridgeTargetEntity actor, double damage) {
        JsonObject message = Proto.msg(ProjectileNativeFixture.SID, 1, "mob_hurt");
        message.addProperty("eventId", UUID.randomUUID().toString()); message.addProperty("entityId", mob.getUUID().toString());
        message.addProperty("sourceEid", actor.actorId()); message.addProperty("damage", damage); return message;
    }
    @SuppressWarnings("unchecked")
    private static Mob assigned(int slot) {
        try {
            var field = NativeWorldGuard.class.getDeclaredField("ENTITY_REFS"); field.setAccessible(true);
            return ((java.util.Map<UUID, Entity>)field.get(null)).values().stream().filter(e -> e instanceof Mob && MobRuntime.owns(e)
                    && MobRuntime.armyAssignment(e) != null && MobRuntime.armyAssignment(e).slot() == slot).map(e -> (Mob)e).findFirst().orElse(null);
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

