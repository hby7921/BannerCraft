package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.component.DataComponents;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.network.chat.Component;
import net.minecraft.world.Difficulty;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.monster.skeleton.WitherSkeleton;
import net.minecraft.world.entity.vehicle.boat.AbstractBoat;
import net.minecraft.world.entity.vehicle.boat.AbstractChestBoat;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.gamerules.GameRules;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** Serial isolated vanilla AI/effect/damage/drop and real boat-item regression. */
public final class MobBoatNativeGameTest {
    @GameTest(maxTicks = 1000, setupTicks = 2600, skyAccess = true, padding = 16)
    public void ownedWitherCombatAndBoats(GameTestHelper helper) {
        var f = new ProjectileNativeFixture(helper);
        var level = helper.getLevel();
        var oldDifficulty = level.getDifficulty();
        boolean oldMobGriefing = level.getGameRules().get(GameRules.MOB_GRIEFING);
        level.getServer().setDifficulty(Difficulty.NORMAL, true);
        level.getGameRules().set(GameRules.MOB_GRIEFING, true, level.getServer());
        MobRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        BoatRuntime.begin(ProjectileNativeFixture.SID, f.player, m -> f.reliable.add(m.deepCopy()), m -> f.states.add(m.deepCopy()));
        ActorRegistry<BridgeTargetEntity> actors = actors();
        helper.assertTrue(actors.values().isEmpty(), "serial fixture starts with no previous native actors");
        BridgeTargetEntity friendly = actors.getOrCreate(23001, () -> BridgeTargetEntity.spawn(level.getServer(), 23001,
                f.origin.x, f.origin.y, f.origin.z + 13));
        friendly.setHostile(false); friendly.setHostHealth(200, 200);
        BridgeTargetEntity enemy = actors.getOrCreate(23002, () -> BridgeTargetEntity.spawn(level.getServer(), 23002,
                f.origin.x + 3, f.origin.y, f.origin.z + 13));
        enemy.setHostile(true); enemy.setHostHealth(200, 200);
        var foreignSkeleton = EntityTypes.WITHER_SKELETON.create(level, EntitySpawnReason.TRIGGERED);
        foreignSkeleton.setNoAi(true); foreignSkeleton.setPos(f.origin.x - 4, f.origin.y, f.origin.z + 40);
        helper.assertTrue(level.addFreshEntity(foreignSkeleton) && !MobRuntime.owns(foreignSkeleton), "ambient world skeleton is never adopted");
        var foreignBoat = EntityTypes.OAK_BOAT.create(level, EntitySpawnReason.TRIGGERED);
        foreignBoat.setPos(f.origin.x - 4, f.origin.y, f.origin.z + 50);
        helper.assertTrue(level.addFreshEntity(foreignBoat) && !NativeWorldGuard.ownsEntity(foreignBoat), "world boat outside actual owned use is never adopted");
        f.foreign.setPos(f.origin.x - 4, f.origin.y, f.origin.z + 8);
        var mob = spawnEgg(f, BlockPos.containing(f.origin.add(0, -1, 10)));
        Vec3 spawnedPosition = mob.position();
        fractionalGround(f, mob);
        helper.assertTrue(mob.getMainHandItem().is(Items.STONE_SWORD) && mob.getHealth() == 20,
                "native spawn egg supplies original equipment and twenty HP");
        mob.setGuaranteedDrop(EquipmentSlot.MAINHAND); // Makes a genuine native equipment drop deterministic.
        List<WitherSkeleton> fillers = new ArrayList<>();
        AbstractChestBoat[] retainedChest = { null };
        net.minecraft.world.entity.animal.rabbit.Rabbit[] boatVisitor = { null };
        WitherSkeleton[] pickupMob = { null };
        ItemEntity[] foreignPickupSword = { null }, ownedPickupSword = { null };
        ItemStack[] pickedComponents = { null };
        JsonObject[] receivedHurt = { null };
        float[] afterHurt = { 0 };
        long[] witherBeforeDeath = { 0 };
        int[] witherDurationAtDeath = { 0 }, victimTickAtDeath = { 0 };
        boolean[] cleaned = { false };
        Runnable cleanup = () -> {
            if (cleaned[0]) return;
            cleaned[0] = true; f.finished = true;
            // Run every independent cleanup even when an assertion failed; later
            // native cases must not inherit this case's static runtime or tickets.
            var steps = new ArrayList<Runnable>();
            steps.add(() -> f.interaction.clear(f.player));
            steps.add(f::discardTargets);
            steps.add(WeaponRuntime::clear);
            steps.add(MobRuntime::clear);
            steps.add(BoatRuntime::clear);
            steps.add(OwnedEntityTicks::clear);
            steps.add(f.building::clear);
            steps.add(actors::clear);
            steps.add(foreignSkeleton::discard); steps.add(foreignBoat::discard);
            steps.add(() -> { for (var filler : fillers) filler.discard(); });
            steps.add(() -> { if (retainedChest[0] != null) retainedChest[0].discard(); });
            steps.add(() -> { if (boatVisitor[0] != null) boatVisitor[0].discard(); });
            steps.add(() -> { if (pickupMob[0] != null) pickupMob[0].discard(); });
            steps.add(() -> { if (foreignPickupSword[0] != null) foreignPickupSword[0].discard(); });
            steps.add(f.foreign::discard);
            steps.add(() -> level.getServer().getPlayerList().remove(f.player));
            steps.add(() -> f.connectionChannel.finishAndReleaseAll());
            steps.add(() -> { for (long packed : f.fixtureTickets) { var chunk = net.minecraft.world.level.ChunkPos.unpack(packed); level.setChunkForced(chunk.x(), chunk.z(), false); } });
            steps.add(() -> level.getServer().setDifficulty(oldDifficulty, true));
            steps.add(() -> level.getGameRules().set(GameRules.MOB_GRIEFING, oldMobGriefing, level.getServer()));
            RuntimeException failure = null;
            for (var step : steps) try { step.run(); }
            catch (RuntimeException | Error error) {
                if (failure == null) failure = new IllegalStateException("native mob fixture teardown failed", error);
                else failure.addSuppressed(error);
            }
            if (failure != null) throw failure;
        };
        var timeline = new NativeTimeline(helper, mob);
        helper.onEachTick(() -> {
            if (f.finished) return;
            try {
            MobRuntime.tick(); BoatRuntime.tick(); OwnedEntityTicks.tick();
            if (retainedChest[0] != null && !retainedChest[0].isRemoved()) {
                JsonObject state = f.states.stream().filter(m -> Proto.type(m).equals("boat_state")
                        && Proto.string(m, "entityId").equals(retainedChest[0].getUUID().toString())).reduce((a, b) -> b).orElseThrow();
                helper.assertTrue(state.get("leftPaddle").getAsJsonPrimitive().isNumber()
                        && state.get("rightPaddle").getAsJsonPrimitive().isNumber(),
                        "wire paddle pose contains real numeric renderer phases rather than enable booleans");
                helper.assertTrue(Proto.number(state, "leftPaddle") == retainedChest[0].getRowingTime(AbstractBoat.PADDLE_LEFT, 1.0F)
                        && Proto.number(state, "rightPaddle") == retainedChest[0].getRowingTime(AbstractBoat.PADDLE_RIGHT, 1.0F),
                        "wire paddle pose matches original native getRowingTime renderer state on each actual tick");
            }
            if (boatVisitor[0] != null) {
                var rabbit = boatVisitor[0];
                boolean retained = rabbit.getVehicle() == null && rabbit.getDeltaMovement().lengthSqr() < 1.0e-8;
                if (!retained) logBoatVisitor(f, rabbit, retainedChest[0], foreignBoat, foreignSkeleton);
                helper.assertTrue(retained, "owned boat native ticks cannot adopt or push the original foreign small animal");
            }
            helper.assertTrue(OwnedEntityTicks.centreCount() <= 32 && OwnedEntityTicks.fullChunkCount() <= 96,
                    "ordinary entity leases retain bounded nonpersistent native ticking");
            timeline.tick();
            } catch (RuntimeException | Error failure) {
                try { cleanup.run(); } catch (RuntimeException | Error cleanupFailure) { failure.addSuppressed(cleanupFailure); }
                throw failure;
            }
        });
        NativeTestCleanup.register(helper, cleanup);
        timeline.at(40, () -> {
            helper.assertTrue(hits(f, 23001, "melee") > 0, "native skeleton reaches its nearest invisible friendly proxy through original pathfinding");
            helper.assertTrue(mob.position().distanceToSqr(spawnedPosition) > .25,
                    "the real native mob moved from its actual spawn position while chasing the soldier");
            helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("mob_state")
                    && Proto.integer(m, "targetEid") == 23001 && Proto.number(m, "attackProgress") > 0),
                    "actual native attack swing progression is mirrored independently of the chase position");
            assertNativeWalkFormula(f, mob.getUUID().toString());
            // Native repeated four-damage melee hits can suppress one-damage WITHER during
            // hurt immunity. Let the soldier escape, retaining the original timed effect.
            friendly.reposition(f.origin.x - 4, f.origin.y, f.origin.z + 50);
        });
        timeline.at(120, () -> {
            helper.assertTrue(hits(f, 23001, "melee") > 0, "native owned skeleton naturally selects and strikes a friendly soldier proxy");
            helper.assertTrue(hits(f, 23001, "wither") > 0 && friendly.hasEffect(net.minecraft.world.effect.MobEffects.WITHER),
                    "vanilla successful attack applies real WITHER and ticks attributed damage");
            helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("mob_state")
                    && Proto.integer(m, "targetEid") == 23001 && Proto.number(m, "walkSpeed") > 0),
                    "natural chase and target animation come from the original native mob");
            actors.remove(23001);
            enemy.reposition(mob.getX(), mob.getY(), mob.getZ() + 1.4);
        });
        timeline.at(140, () -> f.player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.STONE_SWORD)));
        timeline.at(150, () -> {
            helper.assertTrue(java.util.stream.StreamSupport.stream(MobRuntime.attackTargets().spliterator(), false)
                    .anyMatch(target -> target == mob), "native melee attack candidates contain the actual owned living skeleton");
            helper.assertTrue(java.util.stream.StreamSupport.stream(MobRuntime.attackTargets().spliterator(), false)
                    .noneMatch(target -> target == foreignSkeleton), "native melee attack candidates exclude ambient skeletons");
            f.player.connection.teleport(mob.getX(), mob.getY(), mob.getZ() - 1.5, 0, 0);
            float before = mob.getHealth();
            long hostReceipts = f.reliable.stream().filter(m -> Proto.type(m).equals("hit_actor")).count();
            f.player.attack(mob);
            helper.assertTrue(mob.getHealth() < before && mob.isAlive(),
                    "the genuine MC player's ordinary sword attack debits the actual native owned mob HP");
            helper.assertTrue(f.reliable.stream().filter(m -> Proto.type(m).equals("hit_actor")).count() == hostReceipts,
                    "attacking a real native mob does not fabricate a Bannerlord hit_actor receipt");
            f.applyPose();
        });
        timeline.at(180, () -> {
            helper.assertTrue(hits(f, 23002, "melee") > 0, "native skeleton reacquires and attacks an enemy soldier as well");
            helper.assertTrue(!mob.canAttack(f.foreign) && !mob.doHurtTarget(level, f.foreign)
                    && f.foreign.getHealth() == f.foreignHealth, "foreign living target cannot be acquired or damaged");
            JsonObject hurt = hurt(mob, enemy, 3.75);
            float before = mob.getHealth();
            helper.assertTrue(MobRuntime.hurt(hurt, enemy) && Math.abs(mob.getHealth() - (before - 3.75)) < .001,
                    "soldier damage reaches actual native HP with conversion applied exactly once");
            receivedHurt[0] = hurt; afterHurt[0] = mob.getHealth();
            JsonObject foreign = hurt.deepCopy(); foreign.addProperty("eventId", UUID.randomUUID().toString());
            foreign.addProperty("sourceEid", 999999);
            helper.assertTrue(!MobRuntime.hurt(foreign, enemy), "invalid soldier source rejects rather than fabricating an attacker");
            JsonObject foreignTarget = hurt.deepCopy(); foreignTarget.addProperty("eventId", UUID.randomUUID().toString());
            foreignTarget.addProperty("entityId", foreignSkeleton.getUUID().toString());
            helper.assertTrue(!MobRuntime.hurt(foreignTarget, enemy) && foreignSkeleton.getHealth() == 20,
                    "host damage cannot adopt or debit an existing foreign skeleton");
        });
        timeline.at(210, () -> {
            helper.assertTrue(MobRuntime.hurt(receivedHurt[0], enemy) && mob.getHealth() == afterHurt[0],
                    "retry after native immunity expired is ACKable without a second HP debit");
            helper.assertTrue(foreignSkeleton.getTarget() == null, "native retaliation alerts cannot recruit ambient skeletons against artificial host proxies");
            witherBeforeDeath[0] = hits(f, 23002, "wither");
            var effect = enemy.getEffect(net.minecraft.world.effect.MobEffects.WITHER);
            helper.assertTrue(effect != null && effect.getDuration() > 80, "native refreshed WITHER still has sufficient original duration before source death");
            witherDurationAtDeath[0] = effect.getDuration(); victimTickAtDeath[0] = enemy.tickCount;
            BridgeRuntime.log("MOB_WITHER_AT_SOURCE_DEATH duration=" + effect.getDuration() + " phase="
                    + effect.getDuration() % 40 + " victimTick=" + enemy.tickCount + " hurtCooldown=" + enemy.damageCooldownTime
                    + " sourceId=" + mob.getUUID() + " tickCentres=" + OwnedEntityTicks.centreCount());
            helper.assertTrue(MobRuntime.hurt(hurt(mob, enemy, 1000), enemy) && !mob.isAlive(), "real soldier damage performs native death");
        });
        timeline.at(245, () -> {
            helper.assertTrue(mob.isRemoved() && f.reliable.stream().anyMatch(m -> Proto.type(m).equals("mob_remove")
                    && Proto.string(m, "entityId").equals(mob.getUUID().toString())), "native removal emits a reliable UUID retirement");
            var drops = level.getEntitiesOfClass(ItemEntity.class, mob.getBoundingBox().inflate(4), item -> item.getItem().is(Items.STONE_SWORD));
            helper.assertTrue(!drops.isEmpty() && drops.stream().allMatch(NativeWorldGuard::ownsEntity),
                    "native original equipment loot is tracked rather than deleted or fabricated");
            helper.assertTrue(MobRuntime.hurt(hurt(mob, enemy, 1), enemy), "late hit on known removed mob is ACKable without resurrection");
            for (int i = 0; i < 16; i++) {
                var filler = spawnEgg(f, BlockPos.containing(f.origin.add(2, -1, 10)));
                filler.setNoAi(true); fillers.add(filler);
            }
            ItemStack egg = new ItemStack(Items.WITHER_SKELETON_SPAWN_EGG, 2);
            f.player.setItemSlot(EquipmentSlot.MAINHAND, egg);
            helper.assertTrue(!MobRuntime.canUseSpawnEgg(f.player, egg), "sixteen native mobs refuse another egg before vanilla consumes it");
            f.press(); f.release();
            helper.assertTrue(egg.getCount() == 2, "production use preflight retains an over-capacity spawn egg");
            for (var filler : fillers) filler.discard();
            MobRuntime.tick();
            ItemEntity realDrop = boats(f, retainedChest, boatVisitor);
            pickupMob[0] = spawnEgg(f, BlockPos.containing(f.origin.add(0, -1, 70)));
            pickupMob[0].setNoAi(true); pickupMob[0].setCanPickUpLoot(true);
            pickupMob[0].getMainHandItem().setDamageValue(70);
            var foreignDiamond = new ItemStack(Items.DIAMOND_SWORD);
            foreignDiamond.set(DataComponents.CUSTOM_NAME, Component.literal("Foreign pickup diamond sword"));
            var visitorSword = new ItemEntity(level, pickupMob[0].getX() + .1, pickupMob[0].getY() + .25,
                    pickupMob[0].getZ(), foreignDiamond);
            visitorSword.setNoGravity(true); visitorSword.setDeltaMovement(Vec3.ZERO);
            helper.assertTrue(level.addFreshEntity(visitorSword) && !NativeWorldGuard.ownsEntity(visitorSword)
                    && pickupMob[0].wantsToPickUp(level, foreignDiamond),
                    "foreign named diamond sword is a real better vanilla pickup candidate inside owned skeleton bounds");
            foreignPickupSword[0] = visitorSword;
            pickedComponents[0] = realDrop.getItem().copy(); ownedPickupSword[0] = realDrop;
            realDrop.teleportTo(pickupMob[0].getX() - .1, pickupMob[0].getY() + .25, pickupMob[0].getZ());
            realDrop.setNoGravity(true); realDrop.setDeltaMovement(Vec3.ZERO);
        });
        timeline.at(310, () -> {
            var effect = enemy.getEffect(net.minecraft.world.effect.MobEffects.WITHER);
            int nativeTicks = enemy.tickCount - victimTickAtDeath[0];
            BridgeRuntime.log("MOB_WITHER_AFTER_SOURCE_DEATH duration=" + (effect == null ? -1 : effect.getDuration())
                    + " originalDuration=" + witherDurationAtDeath[0] + " victimNativeTicks=" + nativeTicks
                    + " hurtCooldown=" + enemy.damageCooldownTime + " attributed="
                    + MobRuntime.captures(enemy.actorId(), level.damageSources().wither())
                    + " hits=" + hits(f, 23002, "wither") + " tickCentres=" + OwnedEntityTicks.centreCount());
            helper.assertTrue(nativeTicks >= 40 && effect != null
                    && effect.getDuration() == witherDurationAtDeath[0] - nativeTicks,
                    "original timed WITHER duration advances once per actual victim tick after its source dies");
            helper.assertTrue(MobRuntime.captures(enemy.actorId(), level.damageSources().wither()),
                    "owned native WITHER provenance remains active after original skeleton UUID retires");
            helper.assertTrue(hits(f, 23002, "wither") > witherBeforeDeath[0]
                    && f.reliable.stream().anyMatch(m -> Proto.type(m).equals("mob_hit") && Proto.integer(m, "eid") == 23002
                    && Proto.string(m, "kind").equals("wither") && Proto.string(m, "sourceId").equals(mob.getUUID().toString())),
                    "a full original effect period produces real WITHER damage with its original source UUID after skeleton death");
        });
        timeline.at(330, () -> {
            var chest = retainedChest[0];
            helper.assertTrue(chest != null && !chest.isRemoved() && chest.getItem(0).is(Items.DIAMOND)
                    && chest.getItem(0).getCount() == 3, "actual native chest boat owns its unchanged container");
            helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("boat_state")
                    && Proto.string(m, "kind").equals("bamboo_raft") && Proto.integer(m, "nativeTick") > 20),
                    "actual distant raft advances native ticks outside the fixture's single forced source chunk");
            helper.assertTrue(pickupMob[0].tickCount > 20 && ownedPickupSword[0].isRemoved()
                    && ItemStack.isSameItemSameComponents(pickupMob[0].getMainHandItem(), pickedComponents[0]),
                    "natural native looting equips the actual original owned better sword with unchanged components");
            helper.assertTrue(!foreignPickupSword[0].isRemoved() && foreignPickupSword[0].getItem().getCount() == 1
                    && Component.literal("Foreign pickup diamond sword").equals(foreignPickupSword[0].getItem().get(DataComponents.CUSTOM_NAME)),
                    "natural owned skeleton looting leaves the original foreign named diamond sword untouched");
            f.assertForeignPreserved();
            helper.assertTrue(!foreignSkeleton.isRemoved() && !foreignBoat.isRemoved(), "foreign mob and boat survive session work");
            // This assertion tests source retirement, not a 10-second clock expiry.
            // Earlier receipts may legitimately expire while the real 1024-entity
            // fixture and asynchronous native chunk preparation are doing work.
            JsonObject retirementReceipt = hurt(pickupMob[0], enemy, .25);
            retirementReceipt.addProperty("sourceKind", "melee");
            float beforeRetirement = pickupMob[0].getHealth();
            helper.assertTrue(MobRuntime.hurt(retirementReceipt, enemy)
                    && Math.abs(pickupMob[0].getHealth() - (beforeRetirement - .25F)) < .001,
                    "the still-registered original soldier creates a fresh real native receipt before retirement");
            actors.remove(23002);
            helper.assertTrue(MobRuntime.hurt(retirementReceipt, null)
                    && Math.abs(pickupMob[0].getHealth() - (beforeRetirement - .25F)) < .001,
                    "exact fresh reliable retry remains ACKable after its original soldier proxy retires without another debit");
            JsonObject changed = retirementReceipt.deepCopy(); changed.addProperty("damage", 4);
            helper.assertTrue(!MobRuntime.hurt(changed, null), "an altered payload cannot reuse a previously accepted event ID");
            JsonObject changedKind = retirementReceipt.deepCopy(); changedKind.addProperty("sourceKind", "arrow");
            helper.assertTrue(!MobRuntime.hurt(changedKind, null), "an altered missile category cannot reuse the retired source's fresh event ID");
            Vec3 destination = f.origin.add(-2, 0, -1);
            BoatRuntime.preserve(level, destination);
            helper.assertTrue(!NativeWorldGuard.ownsEntity(chest) && chest.position().distanceToSqr(destination) < 1.0e-8
                    && chest.getItem(0).getCount() == 3, "return preserves the original boat entity and chest inventory");
            MobRuntime.preservePickedEquipment();
            helper.assertTrue(pickupMob[0].getMainHandItem().isEmpty(), "native preserved equipment drop clears the original living pickup slot");
            NativeWorldGuard.preserveItemDrops(level, destination);
            var returnedWeapons = level.getEntitiesOfClass(ItemEntity.class,
                    new net.minecraft.world.phys.AABB(destination, destination).inflate(3),
                    item -> ItemStack.isSameItemSameComponents(item.getItem(), pickedComponents[0]));
            helper.assertTrue(returnedWeapons.stream().mapToInt(item -> item.getItem().getCount()).sum() == 1,
                    "cleanup returns exactly one original picked weapon with its real metadata, without loss or duplication");
            BridgeRuntime.log("MOB_PICKUP_PRESERVE_PASS ownedNativePickup=true foreignOriginalRetained=true returnedCount=1");
            MobRuntime.clear(); BoatRuntime.clear(); OwnedEntityTicks.clear();
            helper.assertTrue(OwnedEntityTicks.centreCount() == 0 && OwnedEntityTicks.fullChunkCount() == 0,
                    "explicit session cleanup removes every ordinary native simulation lease");
            f.finished = true;
            helper.runAfterDelay(5, () -> {
                helper.assertTrue(!level.isPositionEntityTicking(BlockPos.containing(f.origin.add(0, 0, 80))),
                        "native distant ticking stops after the actual session leases are released");
                BridgeRuntime.log("MOB_BOAT_NATIVE_PASS: original spawn eggs; friendly/enemy native chase/melee/WITHER; exact HP/retry/death/loot; real boat use/state/chest/foreign/ticket cleanup");
                helper.succeed();
            });
        });
    }
    private static final class NativeTimeline {
        private static final class Step {
            final int age; final Runnable action; boolean done;
            Step(int age, Runnable action) { this.age = age; this.action = action; }
        }
        private final GameTestHelper helper;
        private final WitherSkeleton mob;
        private final List<Step> steps = new ArrayList<>();
        private long origin;
        private boolean started;
        NativeTimeline(GameTestHelper helper, WitherSkeleton mob) { this.helper = helper; this.mob = mob; }
        void at(int age, Runnable action) { steps.add(new Step(age, action)); }
        void tick() {
            if (!started) {
                if (helper.getTick() % 40 == 0) BridgeRuntime.log("MOB_NATIVE_WARMUP helperTick=" + helper.getTick()
                        + " nativeTick=" + mob.tickCount + " entityTicking=" + helper.getLevel().isPositionEntityTicking(mob.blockPosition())
                        + " owned=" + MobRuntime.owns(mob) + " position=" + mob.position() + " target="
                        + (mob.getTarget() instanceof BridgeTargetEntity target ? target.actorId() : 0)
                        + " navigationDone=" + mob.getNavigation().isDone() + " centres=" + OwnedEntityTicks.centreCount());
                helper.assertTrue(helper.getTick() < 600, "async native simulation must permit forty genuine mob ticks within the bounded warmup");
                if (mob.tickCount < 40) return;
                origin = helper.getTick() - 40;
                started = true;
                BridgeRuntime.log("MOB_NATIVE_WARMUP_READY helperTick=" + helper.getTick() + " nativeTick=" + mob.tickCount
                        + " position=" + mob.position() + " target="
                        + (mob.getTarget() instanceof BridgeTargetEntity target ? target.actorId() : 0));
            }
            long age = helper.getTick() - origin;
            for (Step step : steps) if (!step.done && age >= step.age) { step.done = true; step.action.run(); }
        }
    }
    private static WitherSkeleton spawnEgg(ProjectileNativeFixture f, BlockPos floor) {
        var before = f.states.stream().filter(m -> Proto.type(m).equals("mob_state"))
                .map(m -> Proto.string(m, "entityId")).collect(java.util.stream.Collectors.toSet());
        ItemStack egg = new ItemStack(Items.WITHER_SKELETON_SPAWN_EGG, 2);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, egg);
        NativeWorldGuard.enter(f.helper.getLevel(), floor);
        try {
            var result = f.player.gameMode.useItemOn(f.player, f.helper.getLevel(), egg, InteractionHand.MAIN_HAND,
                    new BlockHitResult(Vec3.atCenterOf(floor).add(0, .5, 0), Direction.UP, floor, false));
            f.helper.assertTrue(result.consumesAction() && egg.getCount() == 1, "actual Survival spawn egg consumes one original item");
        } finally { NativeWorldGuard.exit(); }
        String id = f.states.stream().filter(m -> Proto.type(m).equals("mob_state") && !before.contains(Proto.string(m, "entityId")))
                .map(m -> Proto.string(m, "entityId")).findFirst().orElseThrow();
        // Native visible entity tracking may not have caught up to a newly
        // admitted far chunk. Use the exact registered instance, not a nullable
        // visible lookup, while keeping the production provenance assertion.
        var mob = (WitherSkeleton) registeredRef(UUID.fromString(id));
        f.helper.assertTrue(mob != null && MobRuntime.owns(mob), "actual spawned entity has scope-provenance ownership");
        return mob;
    }
    private static ItemEntity boats(ProjectileNativeFixture f, AbstractChestBoat[] retained,
                              net.minecraft.world.entity.animal.rabbit.Rabbit[] visitor) {
        var level = f.helper.getLevel();
        BlockPos water = BlockPos.containing(f.origin.add(0, 0, 3));
        for (int x = -1; x <= 1; x++) for (int z = 0; z <= 2; z++) f.placeOwned(water.offset(x, 0, z), Blocks.WATER.defaultBlockState());
        f.pitch = 20; f.applyPose();
        ItemEntity realDrop = totalEntityCapacity(f);
        f.pitch = 20; f.applyPose();
        var boat = useBoat(f, new ItemStack(Items.OAK_BOAT, 2), "oak_boat");
        boat.discard(); BoatRuntime.tick();
        f.helper.assertTrue(f.reliable.stream().anyMatch(m -> Proto.type(m).equals("boat_remove")
                && Proto.string(m, "entityId").equals(boat.getUUID().toString())), "native boat destruction publishes reliable removal");
        var chest = (AbstractChestBoat) useBoat(f, new ItemStack(Items.OAK_CHEST_BOAT, 2), "oak_chest_boat");
        chest.setItem(0, new ItemStack(Items.DIAMOND, 3)); retained[0] = chest;
        // Place the actual just-created boat on the fixture's dry native stone floor,
        // so water currents cannot disguise a forbidden push as unrelated animal motion.
        int sourceX=Math.floorMod(BlockPos.containing(f.origin).getX(),16),sourceZ=Math.floorMod(BlockPos.containing(f.origin).getZ(),16);
        // A real pushable server candidate needs an entity-ticking chunk. Keep
        // this dry contact inside the already-ticking original source chunk.
        chest.teleportTo(f.origin.x + (sourceX<=11?4:-4), f.origin.y, f.origin.z + (sourceZ>=3?-3:10));
        chest.setDeltaMovement(Vec3.ZERO);
        var rabbit = EntityTypes.RABBIT.create(level, EntitySpawnReason.TRIGGERED);
        rabbit.setNoAi(true); rabbit.setNoGravity(true);
        rabbit.setPos(chest.getX() + .2, chest.getY(), chest.getZ());
        f.helper.assertTrue(level.addFreshEntity(rabbit) && !NativeWorldGuard.ownsEntity(rabbit)
                && rabbit.getVehicle() == null && rabbit.getDeltaMovement().equals(Vec3.ZERO),
                "small ambient animal begins at actual owned boat bounds without a passenger or motion lease");
        f.helper.assertTrue(rabbit.getBoundingBox().intersects(chest.getBoundingBox()) && rabbit.isPushable(),
                "foreign animal is a genuine vanilla automatic pickup/push candidate at the boat");
        BridgeRuntime.log("BOAT_FOREIGN_CANDIDATE rabbitPos=" + rabbit.position() + " rabbitBox=" + rabbit.getBoundingBox()
                + " boatPos=" + chest.position() + " boatBox=" + chest.getBoundingBox() + " ticking="
                + level.isPositionEntityTicking(rabbit.blockPosition()) + " pushable=" + rabbit.isPushable()
                + " fluid=" + level.getFluidState(rabbit.blockPosition()) + " boatStatus=" + boatStatus(chest));
        visitor[0] = rabbit;
        chest.push(rabbit);
        f.helper.assertTrue(rabbit.getDeltaMovement().equals(Vec3.ZERO), "owned boat direct push preserves foreign animal motion");
        var raft = EntityTypes.BAMBOO_RAFT.create(level, EntitySpawnReason.TRIGGERED);
        raft.setPos(f.origin.x, f.origin.y, f.origin.z + 80);
        NativeWorldGuard.enter(level, raft.blockPosition());
        try { f.helper.assertTrue(level.addFreshEntity(raft), "native owned raft is accepted without synthetic duplicate summon"); }
        finally { NativeWorldGuard.exit(); }
        f.helper.assertTrue(f.states.stream().anyMatch(m -> Proto.type(m).equals("boat_state")
                && Proto.string(m, "kind").equals("bamboo_raft") && Proto.string(m, "entityId").equals(raft.getUUID().toString())),
                "original registry kind distinguishes actual raft geometry");
        for (int index = 0; index < 14; index++) {
            var filler = EntityTypes.OAK_BOAT.create(level, EntitySpawnReason.TRIGGERED);
            filler.setPos(f.origin.x + 3, f.origin.y, f.origin.z + 60);
            NativeWorldGuard.enter(level, filler.blockPosition());
            try { f.helper.assertTrue(level.addFreshEntity(filler), "native boat slot below sixteen remains available"); }
            finally { NativeWorldGuard.exit(); }
        }
        BridgeRuntime.log("BOAT_PENDING_OWNERSHIP id=" + raft.getUUID() + " visibleLookup=" + (level.getEntity(raft.getUUID()) == raft)
                + " nativeTick=" + raft.tickCount + " entityTicking=" + level.isPositionEntityTicking(raft.blockPosition())
                + " owned=" + NativeWorldGuard.ownsEntity(raft) + " tickCentres=" + OwnedEntityTicks.centreCount());
        f.helper.assertTrue(!raft.isRemoved() && NativeWorldGuard.ownsEntity(raft),
                "later spawn admissions preserve a genuine far boat while native chunk tracking is still preparing");
        ItemStack overCap = new ItemStack(Items.OAK_BOAT, 2);
        f.player.setItemSlot(EquipmentSlot.MAINHAND, overCap);
        f.helper.assertTrue(!BoatRuntime.canUseBoat(f.player, overCap), "sixteen native boats refuse use before vanilla item consumption");
        f.press(); f.release();
        f.helper.assertTrue(overCap.getCount() == 2, "production use preflight preserves a capacity-rejected boat item");
        var rejected = EntityTypes.OAK_BOAT.create(level, EntitySpawnReason.TRIGGERED);
        rejected.setPos(f.origin.x + 3, f.origin.y, f.origin.z + 60);
        NativeWorldGuard.enter(level, rejected.blockPosition());
        try { f.helper.assertTrue(!level.addFreshEntity(rejected) && !NativeWorldGuard.ownsEntity(rejected),
                "native add hook independently refuses a seventeenth owned boat without adoption"); }
        finally { NativeWorldGuard.exit(); }
        return realDrop;
    }
    private static void logBoatVisitor(ProjectileNativeFixture f, net.minecraft.world.entity.animal.rabbit.Rabbit rabbit,
                                       AbstractBoat boat, AbstractBoat foreignBoat, WitherSkeleton foreignSkeleton) {
        var level = f.helper.getLevel();
        var vehicle = rabbit.getVehicle();
        BridgeRuntime.log("BOAT_FOREIGN_MOTION rabbitPos=" + rabbit.position() + " delta=" + rabbit.getDeltaMovement()
                + " vehicle=" + (vehicle == null ? "none" : vehicle.getType() + ":" + vehicle.getUUID())
                + " vehicleOwned=" + (vehicle != null && NativeWorldGuard.ownsEntity(vehicle)) + " rabbitOwned=" + NativeWorldGuard.ownsEntity(rabbit)
                + " rabbitInWater=" + rabbit.isInWater() + " rabbitFluid=" + level.getFluidState(rabbit.blockPosition())
                + " rabbitUnderFluid=" + level.getFluidState(rabbit.blockPosition().below()) + " rabbitBlock=" + level.getBlockState(rabbit.blockPosition())
                + " rabbitNoGravity=" + rabbit.isNoGravity() + " rabbitNoAi=" + rabbit.isNoAi()
                + " boatPos=" + (boat == null ? "none" : boat.position()) + " boatDelta=" + (boat == null ? "none" : boat.getDeltaMovement())
                + " boatStatus=" + (boat == null ? "none" : boatStatus(boat)) + " boatOwned=" + (boat != null && BoatRuntime.owns(boat))
                + " boatInWater=" + (boat != null && boat.isInWater()) + " boatFluid=" + (boat == null ? "none" : level.getFluidState(boat.blockPosition()))
                + " foreignBoatPos=" + foreignBoat.position() + " foreignBoatBox=" + foreignBoat.getBoundingBox()
                + " foreignSkeletonPos=" + foreignSkeleton.position() + " foreignSkeletonBox=" + foreignSkeleton.getBoundingBox());
    }
    private static Object boatStatus(AbstractBoat boat) {
        try { var field = AbstractBoat.class.getDeclaredField("status"); field.setAccessible(true); return field.get(boat); }
        catch (ReflectiveOperationException failure) { return failure.toString(); }
    }
    private static AbstractBoat useBoat(ProjectileNativeFixture f, ItemStack stack, String kind) {
        var before = f.states.stream().filter(m -> Proto.type(m).equals("boat_state"))
                .map(m -> Proto.string(m, "entityId")).collect(java.util.stream.Collectors.toSet());
        f.player.setItemSlot(EquipmentSlot.MAINHAND, stack); f.press(); f.release();
        String id = f.states.stream().filter(m -> Proto.type(m).equals("boat_state") && Proto.string(m, "kind").equals(kind)
                && !before.contains(Proto.string(m, "entityId"))).map(m -> Proto.string(m, "entityId")).findFirst().orElseThrow();
        f.helper.assertTrue(stack.getCount() == 1, "actual Survival boat item consumes one original stack");
        return (AbstractBoat) f.helper.getLevel().getEntity(UUID.fromString(id));
    }
    private static ItemEntity totalEntityCapacity(ProjectileNativeFixture f) {
        var level = f.helper.getLevel();
        BlockPos clicked = BlockPos.containing(f.origin.add(1, 0, 2));
        f.placeOwned(clicked, Blocks.STONE.defaultBlockState());
        WitherSkeleton flushMob = overflowMob(f, "Original overflow flush sword", 17);
        WitherSkeleton resetMob = overflowMob(f, "Original overflow reset sword", 19);
        ItemStack flushStack = flushMob.getMainHandItem().copy(), resetStack = resetMob.getMainHandItem().copy();
        BridgeTargetEntity source = BridgeRuntime.findActor(23002);
        f.helper.assertTrue(source != null && source.isAlive(), "overflow death has the actual still-registered soldier source");
        var capacity = new ArrayList<ItemEntity>();
        ItemEntity[] flushed = { null };
        try {
            fillCapacity(f, capacity);
            long mobStates = f.states.stream().filter(m -> Proto.type(m).equals("mob_state")).count();
            long boatStates = f.states.stream().filter(m -> Proto.type(m).equals("boat_state")).count();
            var egg = new ItemStack(Items.WITHER_SKELETON_SPAWN_EGG, 2);
            f.player.setItemSlot(EquipmentSlot.MAINHAND, egg);
            f.helper.assertTrue(!MobRuntime.canUseSpawnEgg(f.player, egg), "global 1024-entity quota rejects egg use while fewer than sixteen mob slots are occupied");
            JsonObject input = Proto.msg(ProjectileNativeFixture.SID, 1, "interact");
            input.addProperty("requestId", UUID.randomUUID().toString()); input.addProperty("pressed", true);
            input.addProperty("target", true); input.addProperty("x", clicked.getX()); input.addProperty("y", clicked.getY()); input.addProperty("z", clicked.getZ());
            input.addProperty("face", Direction.UP.get3DDataValue()); input.addProperty("hitX", clicked.getX() + .5);
            input.addProperty("hitY", clicked.getY() + 1); input.addProperty("hitZ", clicked.getZ() + .5);
            input.addProperty("terrain", false); input.addProperty("canPlace", true);
            f.interaction.interact(f.player, input, ProjectileNativeFixture.SID); f.release();
            f.helper.assertTrue(egg.getCount() == 2 && f.states.stream().filter(m -> Proto.type(m).equals("mob_state")).count() == mobStates,
                    "production useOn preflight keeps both egg items and never creates a ghost mob at full global capacity");
            var boat = new ItemStack(Items.OAK_BOAT, 2);
            f.player.setItemSlot(EquipmentSlot.MAINHAND, boat);
            f.helper.assertTrue(!BoatRuntime.canUseBoat(f.player, boat), "global 1024-entity quota rejects boat use even with zero active boat slots");
            f.press(); f.release();
            f.helper.assertTrue(boat.getCount() == 2 && f.states.stream().filter(m -> Proto.type(m).equals("boat_state")).count() == boatStates,
                    "production water-facing use preflight keeps both boat items and creates no untracked native boat");
            BridgeRuntime.log("MOB_BOAT_GLOBAL_CAPACITY_PASS genuineFillers=" + capacity.size() + " egg=" + egg.getCount() + " boat=" + boat.getCount());
            f.helper.assertTrue(MobRuntime.hurt(hurt(flushMob, source, 1000), source) && !flushMob.isAlive(),
                    "registered soldier performs a real native equipment death at the full regular quota");
            ItemEntity originalFlush = pendingSword("Original overflow flush sword");
            flushed[0] = originalFlush;
            UUID flushId = originalFlush.getUUID();
            f.helper.assertTrue(!originalFlush.isRemoved() && !NativeWorldGuard.ownsEntity(originalFlush)
                    && ItemStack.isSameItemSameComponents(originalFlush.getItem(), flushStack),
                    "exact native named damaged sword drop survives deferred admission without a fabricated refund");
            for (var item : capacity) item.discard(); capacity.clear();
            OwnedMobLoot.tick();
            f.helper.assertTrue(OwnedMobLoot.pendingCount() == 0 && NativeWorldGuard.ownsEntity(originalFlush)
                    && registeredRef(flushId) == originalFlush && originalFlush.getUUID().equals(flushId)
                    && ItemStack.isSameItemSameComponents(originalFlush.getItem(), flushStack),
                    "quota release flushes the same original UUID, entity reference and stack components through normal native add");

            fillCapacity(f, capacity);
            f.helper.assertTrue(MobRuntime.hurt(hurt(resetMob, source, 1000), source) && !resetMob.isAlive(),
                    "second original mob dies at a freshly full regular quota for the reset preservation path");
            ItemEntity originalReset = pendingSword("Original overflow reset sword");
            UUID resetId = originalReset.getUUID();
            Vec3 returned = f.origin.add(-3, 0, -1);
            OwnedMobLoot.preserve(level, returned);
            f.helper.assertTrue(OwnedMobLoot.pendingCount() == 0 && level.getEntity(resetId) == originalReset
                    && !originalReset.isRemoved() && !NativeWorldGuard.ownsEntity(originalReset)
                    && originalReset.position().distanceToSqr(returned) < 1.0e-8
                    && ItemStack.isSameItemSameComponents(originalReset.getItem(), resetStack),
                    "reset admits and returns the same pending native loot object at full quota without deleting or copying its real stack");
            NativeTestCleanup.register(f.helper, () -> f.helper.assertTrue(!originalReset.isRemoved()
                    && ItemStack.isSameItemSameComponents(originalReset.getItem(), resetStack),
                    "ordinary returned overflow loot survives subsequent full session cleanup"));
            BridgeRuntime.log("MOB_LOOT_OVERFLOW_PASS flushId=" + flushId + " resetId=" + resetId
                    + " preservedOriginalComponents=true pending=" + OwnedMobLoot.pendingCount());
        } finally {
            for (var item : capacity) item.discard();
            // This owned collision fixture was required only for the egg useOn test.
            // Its corner otherwise overlaps the genuine boat's vanilla spawn box.
            NativeWorldGuard.enter(level, clicked);
            try { f.helper.assertTrue(level.setBlock(clicked, Blocks.AIR.defaultBlockState(), net.minecraft.world.level.block.Block.UPDATE_ALL),
                    "remove only the temporary owned egg target before testing normal boat collision admission"); }
            finally { NativeWorldGuard.exit(); }
        }
        f.helper.assertTrue(NativeWorldGuard.hasOwnedEntityCapacity(level), "discarded fixture entities release their original quota without touching genuine mob loot");
        return flushed[0];
    }
    private static void fillCapacity(ProjectileNativeFixture f, List<ItemEntity> capacity) {
        var level = f.helper.getLevel();
        while (NativeWorldGuard.hasOwnedEntityCapacity(level)) {
            f.helper.assertTrue(capacity.size() < 1024, "owned total-entity fixture uses a finite native admission budget");
            var item = new ItemEntity(level, f.origin.x + 4, f.origin.y + 2, f.origin.z, new ItemStack(Items.STICK));
            NativeWorldGuard.enter(level, item.blockPosition());
            try { f.helper.assertTrue(level.addFreshEntity(item), "genuine native item entity fills one regular session admission slot"); }
            finally { NativeWorldGuard.exit(); }
            capacity.add(item);
        }
    }
    private static WitherSkeleton overflowMob(ProjectileNativeFixture f, String name, int damage) {
        WitherSkeleton mob = spawnEgg(f, BlockPos.containing(f.origin.add(2, -1, 10)));
        mob.setNoAi(true); mob.setGuaranteedDrop(EquipmentSlot.MAINHAND);
        mob.getMainHandItem().set(DataComponents.CUSTOM_NAME, Component.literal(name));
        mob.getMainHandItem().setDamageValue(damage); return mob;
    }
    @SuppressWarnings("unchecked")
    private static ItemEntity pendingSword(String name) {
        try {
            var field = OwnedMobLoot.class.getDeclaredField("PENDING"); field.setAccessible(true);
            var pending = (java.util.Map<UUID, ItemEntity>) field.get(null);
            return pending.values().stream().filter(item -> item.getItem().is(Items.STONE_SWORD)
                    && Component.literal(name).equals(item.getItem().get(DataComponents.CUSTOM_NAME))).findFirst().orElseThrow();
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
    @SuppressWarnings("unchecked")
    private static net.minecraft.world.entity.Entity registeredRef(UUID id) {
        try {
            var field = NativeWorldGuard.class.getDeclaredField("ENTITY_REFS"); field.setAccessible(true);
            return ((java.util.Map<UUID, net.minecraft.world.entity.Entity>) field.get(null)).get(id);
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
    private static void fractionalGround(ProjectileNativeFixture f, WitherSkeleton mob) {
        BlockPos sample = mob.blockPosition().below(2);
        f.helper.assertTrue(f.helper.getLevel().getBlockState(sample).isAir(), "fractional ground fixture uses one genuine empty native support cell below its original floor");
        JsonObject ground = Proto.msg(ProjectileNativeFixture.SID, 1, "mob_ground");
        ground.addProperty("entityId", mob.getUUID().toString()); ground.addProperty("x", mob.getX());
        ground.addProperty("y", mob.getY() + .25); ground.addProperty("z", mob.getZ());
        ground.addProperty("groundCount", 1); ground.addProperty("ground0X", sample.getX());
        ground.addProperty("ground0Y", sample.getY()); ground.addProperty("ground0Z", sample.getZ()); ground.addProperty("ground0H", .25);
        MobRuntime.prepareGround(ground, f.building);
        f.helper.assertTrue(f.building.matchesSupport(sample, HostGroundBlock.state(.25)) && !f.building.ownsVisible(sample),
                "real fractional entity anchor prepares one quarter-height hidden native ground support without rounding rejection");
        BridgeRuntime.log("MOB_FRACTIONAL_GROUND_PASS anchor=" + mob.getX() + "," + (mob.getY() + .25) + "," + mob.getZ());
    }
    private static JsonObject hurt(WitherSkeleton mob, BridgeTargetEntity source, double amount) {
        JsonObject message = Proto.msg(ProjectileNativeFixture.SID, 0, "mob_hurt");
        message.addProperty("eventId", UUID.randomUUID().toString()); message.addProperty("entityId", mob.getUUID().toString());
        message.addProperty("sourceEid", source.actorId()); message.addProperty("damage", amount); return message;
    }
    private static long hits(ProjectileNativeFixture f, int eid, String kind) {
        return f.reliable.stream().filter(m -> Proto.type(m).equals("mob_hit") && Proto.integer(m, "eid") == eid
                && Proto.string(m, "kind").equals(kind) && Proto.number(m, "damage") > 0).count();
    }
    private static void assertNativeWalkFormula(ProjectileNativeFixture f, String id) {
        List<JsonObject> samples = f.states.stream().filter(m -> Proto.type(m).equals("mob_state")
                && Proto.string(m, "entityId").equals(id)).toList();
        float speed = 0, position = 0;
        JsonObject previous = null;
        boolean moved = false;
        for (JsonObject sample : samples) {
            if (previous != null && Proto.integer(sample, "nativeTick") != Proto.integer(previous, "nativeTick")) {
                f.helper.assertTrue(Proto.integer(sample, "nativeTick") == Proto.integer(previous, "nativeTick") + 1,
                        "animation samples follow consecutive genuine native entity ticks");
                float distance = (float) net.minecraft.util.Mth.length(Proto.number(sample, "x") - Proto.number(previous, "x"),
                        0, Proto.number(sample, "z") - Proto.number(previous, "z"));
                float desired = Math.min(distance * 4.0F, 1.0F);
                speed += (desired - speed) * .4F; position += speed;
                moved |= distance > .01F;
            }
            f.helper.assertTrue(Math.abs(Proto.number(sample, "walkSpeed") - speed) < .00001
                    && Math.abs(Proto.number(sample, "walkPosition") - position) < .00001,
                    "server visual walk exactly matches native client distance, smoothing and phase accumulation");
            previous = sample;
        }
        f.helper.assertTrue(moved && speed > 0 && position > 0, "real native chase produces a moving original-formula visual phase");
    }
    @SuppressWarnings("unchecked")
    private static ActorRegistry<BridgeTargetEntity> actors() {
        try {
            var field = BridgeRuntime.class.getDeclaredField("PROXIES"); field.setAccessible(true);
            return (ActorRegistry<BridgeTargetEntity>) field.get(null);
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
}

