package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.Difficulty;
import net.minecraft.world.effect.MobEffects;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.gamerules.GameRules;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** Genuine vanilla egg classes, boss birth/AI/skulls, original protection and soldier-to-boss damage. */
public final class WitherGeneralMobNativeGameTest {
    @GameTest(maxTicks = 1400, setupTicks = 4200, skyAccess = true, padding = 16)
    public void generalEggsAndWither(GameTestHelper helper) {
        var f = new ProjectileNativeFixture(helper);
        var level = helper.getLevel();
        var oldDifficulty = level.getDifficulty();
        boolean oldGriefing = level.getGameRules().get(GameRules.MOB_GRIEFING);
        level.getServer().setDifficulty(Difficulty.NORMAL, true);
        level.getGameRules().set(GameRules.MOB_GRIEFING, true, level.getServer());
        MobRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        ActorRegistry<BridgeTargetEntity> actors = actors();
        helper.assertTrue(actors.values().isEmpty(), "serial generic mob fixture starts without old host actors");
        var friendly = actors.getOrCreate(24001, () -> BridgeTargetEntity.spawn(level.getServer(), 24001,
                f.origin.x, f.origin.y, f.origin.z + 9));
        friendly.setHostile(false); friendly.setHostHealth(5000, 5000);
        var enemy = actors.getOrCreate(24002, () -> BridgeTargetEntity.spawn(level.getServer(), 24002,
                f.origin.x + 3, f.origin.y, f.origin.z + 10));
        enemy.setHostile(true); enemy.setHostHealth(5000, 5000);
        List<Mob> representative = new ArrayList<>();
        var cow = spawnEgg(f, Items.COW_SPAWN_EGG, -3, 30, "cow"); representative.add(cow);
        representative.add(spawnEgg(f, Items.ZOMBIE_SPAWN_EGG, 3, 7, "zombie"));
        representative.add(spawnEgg(f, Items.GHAST_SPAWN_EGG, -3, 14, "ghast"));
        representative.add(spawnEgg(f, Items.SLIME_SPAWN_EGG, 3, 18, "slime"));
        var cloudTarget = actors.getOrCreate(24003, () -> BridgeTargetEntity.spawn(level.getServer(), 24003,
                f.foreign.getX() + .8, f.foreign.getY(), f.foreign.getZ() + .8));
        cloudTarget.setHostHealth(5000, 5000);
        var cloud = new net.minecraft.world.entity.AreaEffectCloud(level, f.foreign.getX(), f.foreign.getY(), f.foreign.getZ());
        cloud.setOwner(representative.get(1)); cloud.setRadius(2.5F); cloud.setWaitTime(0); cloud.setDuration(300);
        cloud.setRadiusOnUse(0); cloud.setRadiusPerTick(0);
        cloud.addEffect(new net.minecraft.world.effect.MobEffectInstance(MobEffects.POISON, 200));
        NativeWorldGuard.enter(level, cloud.blockPosition());
        try { helper.assertTrue(level.addFreshEntity(cloud) && MobProjectileRuntime.owns(cloud), "actual owned native lingering potion cloud is registered with its original mob source"); }
        finally { NativeWorldGuard.exit(); }
        long[] poisonBeforeSourceDeath = { 0 };
        var cowWalk = cow.position();
        WitherBoss[] boss = { null };
        Vec3[] foreignPosition = { null };
        BlockPos[] protectedBlock = { null };
        float[] bossBeforeHurt = { 0 };
        float[] damageAfterArmor = { 0 };
        JsonObject[] hurt = { null };
        int[] phase = { 0 }, killedAt = { 0 };
        boolean[] checkedCowInteraction = { false };
        helper.onEachTick(() -> {
            if (f.finished) return;
            MobRuntime.tick(); OwnedEntityTicks.tick();
            helper.assertTrue(OwnedEntityTicks.centreCount() <= 32 && OwnedEntityTicks.fullChunkCount() <= 96,
                    "general mob and skull simulation retains original bounded transient tickets");
            if (!checkedCowInteraction[0] && cow.tickCount >= 5 && level.isPositionEntityTicking(f.foreign.blockPosition())) {
                // 26.3 isPushable intentionally returns false before native entity-ticking
                // readiness. Wait for the real lease instead of making this a dummy push.
                Vec3 originalCow = cow.position(), originalCowMotion = cow.getDeltaMovement();
                cow.setPos(f.foreign.getX() + .2, f.foreign.getY(), f.foreign.getZ());
                helper.assertTrue(cow.getBoundingBox().intersects(f.foreign.getBoundingBox()) && cow.isPushable() && f.foreign.isPushable(),
                        "owned quadruped and foreign villager are genuine loaded native collision candidates");
                cow.push(f.foreign); f.foreign.push(cow);
                helper.assertTrue(f.foreign.getDeltaMovement().equals(Vec3.ZERO) && cow.getDeltaMovement().equals(originalCowMotion),
                        "both native push directions preserve foreign motion instead of moving unrelated world entities");
                NativeWorldGuard.enterEntity(level, cow);
                try { f.foreign.discard(); }
                finally { NativeWorldGuard.exit(); }
                helper.assertTrue(!f.foreign.isRemoved(), "owned native work cannot consume a foreign living entity through direct discard");
                cow.setPos(originalCow); checkedCowInteraction[0] = true;
            }
            if (phase[0] == 0 && checkedCowInteraction[0] && representative.stream().allMatch(m -> m.tickCount >= 60)) {
                helper.assertTrue(cow.getTarget() == null && !state(f, cow).get("hostile").getAsBoolean(),
                        "native passive cow is visible without an artificial soldier attack goal");
                helper.assertTrue(cow.isAlive() && cow.getHealth() == cow.getMaxHealth(),
                        "ordinary host proxy presence does not debit passive cow HP");
                helper.assertTrue(representative.stream().allMatch(m -> MobRuntime.owns(m)
                        && state(f, m).get("width").getAsFloat() == m.getBbWidth()
                        && state(f, m).get("height").getAsFloat() == m.getBbHeight()),
                        "actual humanoid, quadruped, flying and cube mob dimensions publish from their native entity instances");
                helper.assertTrue(cloudTarget.hasEffect(MobEffects.POISON) && !f.foreign.hasEffect(MobEffects.POISON)
                        && f.foreign.getHealth() == f.foreignHealth,
                        "original cloud area applies real poison to registered soldiers while retaining an overlapping foreign villager HP and effects");
                poisonBeforeSourceDeath[0] = hits(f, representative.get(1), "projectile");
                for (Mob mob : representative) mob.discard(); MobRuntime.tick();
                helper.assertTrue(representative.stream().allMatch(m -> removed(f, m)),
                        "all genuine egg classes emit UUID retirement when native removal occurs");
                // Keep the actual original boss target choice focused on this test's two
                // ordinary host soldiers, while the preceding egg checks used Survival.
                f.player.setGameMode(net.minecraft.world.level.GameType.CREATIVE);
                var summoned = MobRuntime.summonOwned(f.player, "wither");
                helper.assertTrue(summoned.success(), "owned selected player invokes the guarded original Wither summon: " + summoned.message());
                boss[0] = (WitherBoss)registeredRef(UUID.fromString(summoned.entityId()));
                helper.assertTrue(boss[0] != null && MobRuntime.owns(boss[0]) && boss[0].getInvulnerableTicks() == 220
                        && boss[0].getMaxHealth() == 300 && boss[0].getHealth() == 100,
                        "real Wither enters its original 220-tick birth phase with native 100/300 HP");
                JsonObject bossState = state(f, boss[0]);
                helper.assertTrue(bossState.get("boss").getAsBoolean() && Proto.string(bossState, "bossName").equals(boss[0].getDisplayName().getString())
                        && Proto.number(bossState, "hp") == boss[0].getHealth() && Proto.number(bossState, "maxHp") == 300,
                        "host boss bar state uses actual native name and HP rather than birth phase progress");
                float initialHp = boss[0].getHealth();
                helper.assertTrue(MobRuntime.hurt(hurt(boss[0], enemy, 12), enemy) && boss[0].getHealth() == initialHp,
                        "native birth invulnerability rejects ordinary soldier damage without replacing its rules");
                f.foreign.setPos(boss[0].getX() + 1.3, boss[0].getY(), boss[0].getZ());
                f.foreign.setDeltaMovement(Vec3.ZERO); foreignPosition[0] = f.foreign.position();
                protectedBlock[0] = boss[0].blockPosition().offset(-2, 0, 0);
                helper.assertTrue(level.setBlock(protectedBlock[0], Blocks.GLASS.defaultBlockState(), 3)
                        && !NativeWorldGuard.ownsBlock(level, protectedBlock[0]),
                        "foreign real glass block inside the birth blast is not adopted before the explosion");
                phase[0] = 1;
                BridgeRuntime.log("GENERAL_EGGS_WITHER_BIRTH_PASS boss=" + boss[0].getUUID() + " cowMoved=" + cow.position().distanceToSqr(cowWalk));
            } else if (phase[0] == 1 && boss[0].getInvulnerableTicks() == 0 && boss[0].tickCount >= 240) {
                helper.assertTrue(hits(f, representative.get(1), "projectile") > poisonBeforeSourceDeath[0],
                        "native continuing cloud and poison ticks retain exact original mob attribution after its source entity was removed");
                helper.assertTrue(f.reliable.stream().anyMatch(m -> Proto.type(m).equals("explosion") && Proto.number(m, "radius") == 7),
                        "original completed birth emits the real seven-block explosion visual event");
                helper.assertTrue(hits(f, boss[0], "explosion") > 0, "native birth explosion supplies original exposure damage to host soldier proxies");
                assertForeign(f, foreignPosition[0], protectedBlock[0]);
                helper.assertTrue(boss[0].getHealth() == 300, "native birth healing reaches the original max HP");
                phase[0] = 2;
            } else if (phase[0] == 2 && boss[0].tickCount >= 620) {
                helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("projectile_state")
                        && Proto.string(m, "kind").equals("wither_skull") && m.get("alive").getAsBoolean()),
                        "the actual Wither naturally creates and publishes original skull projectiles");
                helper.assertTrue(hits(f, boss[0], "projectile") > 0,
                        "original naturally aimed skull collision causes positive attributed proxy damage");
                helper.assertTrue(hits(f, boss[0], "wither") > 0,
                        "native skull applies WITHER and its genuine timed effect deals attributed damage");
                helper.assertTrue(boss[0].getTarget() instanceof BridgeTargetEntity target && MobRuntime.isActor(target)
                        || boss[0].getAlternativeTarget(1) == friendly.getId() || boss[0].getAlternativeTarget(2) == enemy.getId(),
                        "original boss targeting and side-head selection acquire only live native host proxies");
                assertForeign(f, foreignPosition[0], protectedBlock[0]);
                // Stop only fixture combat during the independent soldier damage/retry assertions.
                boss[0].setNoAi(true); bossBeforeHurt[0] = boss[0].getHealth();
                hurt[0] = hurt(boss[0], enemy, 7.25);
                damageAfterArmor[0] = net.minecraft.world.damagesource.CombatRules.getDamageAfterAbsorb(boss[0], 7.25F,
                        level.damageSources().mobAttack(enemy), boss[0].getArmorValue(),
                        (float)boss[0].getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.ARMOR_TOUGHNESS));
                helper.assertTrue(MobRuntime.hurt(hurt[0], enemy)
                        && Math.abs(boss[0].getHealth() - (bossBeforeHurt[0] - damageAfterArmor[0])) < .001,
                        "actual soldier melee debits the same original boss HP through its original four-armor calculation exactly once");
                phase[0] = 3; killedAt[0] = boss[0].tickCount;
            } else if (phase[0] == 3 && boss[0].tickCount >= killedAt[0] + 30) {
                // Original Wither healing continues during these genuine native
                // ticks. A retry must leave the current HP unchanged, rather than
                // erase or deny that native regeneration.
                float beforeRetry = boss[0].getHealth();
                helper.assertTrue(MobRuntime.hurt(hurt[0], enemy) && boss[0].getHealth() == beforeRetry,
                        "an exact reliable retry remains ACKable after native immunity without a duplicate boss debit");
                JsonObject changedKind = hurt[0].deepCopy(); changedKind.addProperty("sourceKind", "arrow");
                helper.assertTrue(!MobRuntime.hurt(changedKind, enemy), "same event ID cannot change a real melee receipt into an arrow");
                boss[0].setHealth(100);
                JsonObject arrow = hurt(boss[0], enemy, 12); arrow.addProperty("sourceKind", "arrow");
                helper.assertTrue(boss[0].isPowered() && MobRuntime.hurt(arrow, enemy) && boss[0].getHealth() == 100,
                        "native powered Wither ignores real host Arrow/Bolt source classes under its original half-HP rule");
                JsonObject thrown = hurt(boss[0], enemy, 8); thrown.addProperty("sourceKind", "projectile");
                helper.assertTrue(MobRuntime.hurt(thrown, enemy) && boss[0].getHealth() < 100,
                        "other host missiles retain their non-arrow direct class and are not incorrectly made immune");
                helper.assertTrue(MobRuntime.hurt(hurt(boss[0], enemy, 1000), enemy) && !boss[0].isAlive(),
                        "ordinary soldier lethal damage performs the actual Wither death");
                helper.assertTrue(!level.getEntitiesOfClass(ItemEntity.class, boss[0].getBoundingBox().inflate(5),
                        item -> item.getItem().is(Items.NETHER_STAR) && NativeWorldGuard.ownsEntity(item)).isEmpty(),
                        "original Wither death creates its real owned Nether Star loot");
                phase[0] = 4; killedAt[0] = boss[0].tickCount;
            } else if (phase[0] == 4 && boss[0].isRemoved()) {
                helper.assertTrue(removed(f, boss[0]), "original Wither disappearance sends one reliable UUID retirement for host visual and boss bar cleanup");
                assertForeign(f, foreignPosition[0], protectedBlock[0]);
                BridgeRuntime.log("WITHER_NATIVE_COMBAT_PASS skulls=" + f.states.stream().filter(m -> Proto.type(m).equals("projectile_state")
                        && Proto.string(m, "kind").equals("wither_skull")).count() + " projectileHits=" + hits(f, boss[0], "projectile")
                        + " explosionHits=" + hits(f, boss[0], "explosion") + " witherHits=" + hits(f, boss[0], "wither"));
                helper.succeed();
            }
        });
        NativeTestCleanup.register(helper, () -> {
            MobRuntime.clear(); OwnedEntityTicks.clear(); actors.clear();
            for (Mob mob : representative) mob.discard();
            if (boss[0] != null) boss[0].discard();
            cloud.discard();
            if (protectedBlock[0] != null) level.setBlock(protectedBlock[0], Blocks.AIR.defaultBlockState(), 3);
            level.getServer().setDifficulty(oldDifficulty, true);
            level.getGameRules().set(GameRules.MOB_GRIEFING, oldGriefing, level.getServer());
        });
    }
    private static Mob spawnEgg(ProjectileNativeFixture f, net.minecraft.world.item.Item item, int dx, int dz, String kind) {
        var known = f.states.stream().filter(m -> Proto.type(m).equals("mob_state")).map(m -> Proto.string(m, "entityId")).toList();
        // The fixture's raw underlying floor is foreign, just like original MC
        // terrain. Place a real owned pedestal in its air cell for useOn's
        // production visible-block gate instead of pretending the floor is owned.
        BlockPos clicked = BlockPos.containing(f.origin.add(dx, 0, dz));
        f.placeOwned(clicked, Blocks.STONE.defaultBlockState());
        ItemStack egg = new ItemStack(item, 2); f.player.setItemSlot(EquipmentSlot.MAINHAND, egg);
        // Use the original survival reach at each real target instead of extending it in the fixture.
        f.player.connection.teleport(clicked.getX() + .5, clicked.getY() + 1, clicked.getZ() - 2, 0, 30);
        f.player.setYHeadRot(0); f.player.setYBodyRot(0);
        JsonObject input = Proto.msg(ProjectileNativeFixture.SID, 1, "interact");
        input.addProperty("requestId", UUID.randomUUID().toString()); input.addProperty("pressed", true); input.addProperty("target", true);
        input.addProperty("x", clicked.getX()); input.addProperty("y", clicked.getY()); input.addProperty("z", clicked.getZ());
        input.addProperty("face", Direction.UP.get3DDataValue()); input.addProperty("hitX", clicked.getX() + .5);
        input.addProperty("hitY", clicked.getY() + 1); input.addProperty("hitZ", clicked.getZ() + .5);
        input.addProperty("terrain", false); input.addProperty("canPlace", true);
        f.interaction.interact(f.player, input, ProjectileNativeFixture.SID); f.release();
        f.applyPose();
        String uuid = f.states.stream().filter(m -> Proto.type(m).equals("mob_state") && Proto.string(m, "kind").equals(kind)
                && !known.contains(Proto.string(m, "entityId"))).map(m -> Proto.string(m, "entityId")).findFirst().orElseThrow();
        f.helper.assertTrue(egg.getCount() == 1, "real Survival " + kind + " egg consumes exactly one native stack item");
        return (Mob)registeredRef(UUID.fromString(uuid));
    }
    private static JsonObject state(ProjectileNativeFixture f, Mob mob) {
        return f.states.stream().filter(m -> Proto.type(m).equals("mob_state") && Proto.string(m, "entityId").equals(mob.getUUID().toString()))
                .reduce((a, b) -> b).orElseThrow();
    }
    private static boolean removed(ProjectileNativeFixture f, Mob mob) {
        return f.reliable.stream().anyMatch(m -> Proto.type(m).equals("mob_remove") && Proto.string(m, "entityId").equals(mob.getUUID().toString()));
    }
    private static long hits(ProjectileNativeFixture f, Mob source, String kind) {
        return f.reliable.stream().filter(m -> Proto.type(m).equals("mob_hit") && Proto.string(m, "sourceId").equals(source.getUUID().toString())
                && Proto.string(m, "kind").equals(kind) && Proto.number(m, "damage") > 0).count();
    }
    private static JsonObject hurt(Mob mob, BridgeTargetEntity source, double damage) {
        JsonObject message = Proto.msg(ProjectileNativeFixture.SID, 0, "mob_hurt");
        message.addProperty("eventId", UUID.randomUUID().toString()); message.addProperty("entityId", mob.getUUID().toString());
        message.addProperty("sourceEid", source.actorId()); message.addProperty("damage", damage); return message;
    }
    private static void assertForeign(ProjectileNativeFixture f, Vec3 initial, BlockPos protectedBlock) {
        f.assertForeignPreserved();
        f.helper.assertTrue(f.foreign.position().distanceToSqr(initial) < 1.0e-8 && f.foreign.getDeltaMovement().lengthSqr() < 1.0e-8
                && !f.foreign.hasEffect(MobEffects.WITHER), "actual boss/skull native blasts preserve foreign motion and status effects as well as HP");
        f.helper.assertTrue(f.helper.getLevel().getBlockState(protectedBlock).is(Blocks.GLASS)
                && !NativeWorldGuard.ownsBlock(f.helper.getLevel(), protectedBlock),
                "original mob explosions and Wither block destruction retain an unrelated glass block and its ownership");
    }
    @SuppressWarnings("unchecked")
    private static net.minecraft.world.entity.Entity registeredRef(UUID id) {
        try {
            var field = NativeWorldGuard.class.getDeclaredField("ENTITY_REFS"); field.setAccessible(true);
            return ((java.util.Map<UUID, net.minecraft.world.entity.Entity>)field.get(null)).get(id);
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

