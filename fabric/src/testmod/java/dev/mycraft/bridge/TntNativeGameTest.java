package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.UUID;

/** Runs inside a real isolated Fabric GameTest server, with all production mixins applied. */
public final class TntNativeGameTest {
    private static final String SID = "00112233445566778899aabbccddeeff00";
    private static final class NativePhase {
        Runnable tick = () -> { }, early = () -> { }, complete = () -> { }, ignite = () -> { }, midpoint = () -> { }, cleanup = () -> { };
    }

    @GameTest(maxTicks = 80, skyAccess = true, padding = 16)
    public void stagingNeverGeneratesDistantChunks(GameTestHelper helper) {
        var level=helper.getLevel();
        // Fixed coordinates in this isolated test world also cover negative
        // chunk boundaries and remain inside staging_origin's wire limits.
        int x=-224,z=-48;
        for(int cx=(x-12)>>4;cx<=(x+12)>>4;cx++)for(int cz=(z-12)>>4;cz<=(z+12)>>4;cz++) {
            level.setChunkForced(cx,cz,true);
            level.getChunkAt(new BlockPos(cx*16,128,cz*16));
        }
        NativeTestCleanup.register(helper, ()->{
            for(int cx=(x-12)>>4;cx<=(x+12)>>4;cx++)for(int cz=(z-12)>>4;cz<=(z+12)>>4;cz++)
                level.setChunkForced(cx,cz,false);
        });
        helper.runAfterDelay(2,()->{
            for(int cx=(x-12)>>4;cx<=(x+12)>>4;cx++)for(int cz=(z-12)>>4;cz<=(z+12)>>4;cz++)
                helper.assertTrue(level.getChunkSource().getChunkNow(cx,cz)!=null,"local fixture chunk is held loaded: "+cx+","+cz);
            helper.assertTrue(BridgeStaging.loadedAir(level,x,z,124,136),"local fixture contains air at staging height");
            int before=level.getChunkSource().getLoadedChunksCount();
            var origin=BridgeStaging.choose(level,-100.25,80.75,128,x+.5,z+.5);
            helper.assertTrue((int)Math.floor(-100.25)+origin.x()==x && (int)Math.floor(80.75)+origin.z()==z,
                    "staging preserves signed integer mapping and prefers the already loaded player area");
            helper.assertTrue(level.getChunkSource().getLoadedChunksCount()==before,
                    "successful staging selection does not load another chunk");
            var foreign=new BlockPos(x,128,z);var original=level.getBlockState(foreign);
            try {
                level.setBlock(foreign,Blocks.TNT.defaultBlockState(),Block.UPDATE_ALL);
                helper.assertTrue(!BridgeStaging.loadedAir(level,x,z,124,136)&&level.getBlockState(foreign).is(Blocks.TNT),
                        "existing foreign TNT is rejected and remains untouched");
            } finally {level.setBlock(foreign,original,Block.UPDATE_ALL);}
            int remoteX=80000,remoteZ=80000;
            helper.assertTrue(level.getChunkSource().getChunkNow(remoteX>>4,remoteZ>>4)==null,"unloaded-region fixture is genuinely unloaded");
            before=level.getChunkSource().getLoadedChunksCount();long started=System.nanoTime();boolean rejected=false;
            try{BridgeStaging.choose(level,-100.25,80.75,128,remoteX,remoteZ);}
            catch(IllegalArgumentException expected){rejected=expected.getMessage().contains("no loaded empty");}
            long elapsed=(System.nanoTime()-started)/1_000_000;
            helper.assertTrue(rejected&&elapsed<2000&&level.getChunkSource().getLoadedChunksCount()==before,
                    "unloaded staging fails promptly without generating distant chunks or stalling handshake");
            BridgeRuntime.log("STAGING_NO_GENERATION_PASS: loadedChunks="+before+" missingRegionElapsedMs="+elapsed);
            helper.succeed();
        });
    }

    @GameTest(maxTicks = 420, skyAccess = true, padding = 16)
    public void placeIgniteFuseExplode(GameTestHelper helper) {
        var level = helper.getLevel();
        var player = helper.makeMockServerPlayerInLevel();
        player.setGameMode(GameType.SURVIVAL);
        player.setNoGravity(true);
        BlockPos ground = helper.absolutePos(new BlockPos(4, 4, 4));
        BlockPos target = ground.above();
        Runnable restoreFixtureChunks = holdFixtureChunks(level, ground);
        java.util.function.BiConsumer<Long, Runnable> atTick = (tick, action) -> helper.runAtTickTime(tick, () -> {
            try { action.run(); }
            catch (RuntimeException | Error failure) { restoreFixtureChunks.run(); throw failure; }
        });
        // The test host supplies a measured flat 9x9 terrain patch. Every MC proxy starts in air.
        for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++)
            level.setBlock(ground.offset(x, 0, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        BlockPos foreignStone = ground.offset(-2, 1, 0);
        level.setBlock(foreignStone, Blocks.STONE.defaultBlockState(), Block.UPDATE_ALL);
        level.setBlock(ground, Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        level.setBlock(target, Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        BlockPos foreignTnt = ground.offset(12, 1, 0);
        level.setBlock(foreignTnt, Blocks.TNT.defaultBlockState(), Block.UPDATE_ALL);
        player.connection.teleport(ground.getX() + .5, ground.getY() + 1, ground.getZ() - 2, 0, 30);
        player.getInventory().setSelectedSlot(0);
        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT, 2));
        player.setItemSlot(EquipmentSlot.OFFHAND, new ItemStack(Items.SHIELD));
        var building = new BuildingRuntime();
        var interaction = new NativeInteractionRuntime(building);
        var reliable = new ArrayList<JsonObject>();
        var states = new ArrayList<JsonObject>();
        building.begin(player, SID);
        TntRuntime.begin(SID, player, message -> reliable.add(message.deepCopy()), message -> states.add(message.deepCopy()));
        final boolean[] finished = {false};
        final BridgeTargetEntity[] near = {null}, far = {null};
        final NativePhase[] flatChain = {new NativePhase()}, partialChain = {new NativePhase()}, fractional = {new NativePhase()};
        final Runnable[] beginFractional = {() -> { }};
        final Runnable[] tickPhase = {() -> { if (!finished[0]) { interaction.tick(player); building.tick(); TntRuntime.tick(); } }};
        NativeTestCleanup.register(helper, () -> {
            interaction.clear(player); TntRuntime.clear(); building.clear();
            if (near[0] != null) near[0].discard(); if (far[0] != null) far[0].discard();
            flatChain[0].cleanup.run(); partialChain[0].cleanup.run(); fractional[0].cleanup.run();
            player.discard();
            restoreFixtureChunks.run();
        });
        // Register every native GameTest scheduled task before its map starts iterating.
        // Re-registering onEachTick from a scheduled callback corrupts the native Fastutil iterator.
        helper.onEachTick(() -> {
            try { tickPhase[0].run(); }
            catch (RuntimeException | Error failure) { restoreFixtureChunks.run(); throw failure; }
        });
        atTick.accept(105L, () -> flatChain[0].early.run());
        atTick.accept(145L, () -> {
            flatChain[0].complete.run();
            partialChain[0] = nativeChain(helper, player, ground, .625);
            tickPhase[0] = partialChain[0].tick;
        });
        atTick.accept(150L, () -> partialChain[0].early.run());
        atTick.accept(190L, () -> {
            partialChain[0].complete.run();
            beginFractional[0].run();
        });
        atTick.accept(192L, () -> fractional[0].ignite.run());
        atTick.accept(230L, () -> fractional[0].midpoint.run());
        atTick.accept(290L, () -> {
            try { fractional[0].complete.run(); }
            finally { restoreFixtureChunks.run(); }
        });

        InteractionResult background = player.gameMode.useItemOn(player, level, player.getMainHandItem(), InteractionHand.MAIN_HAND,
                new BlockHitResult(new Vec3(ground.getX() + .5, ground.getY() + 1, ground.getZ() + .5), Direction.UP, ground, false));
        helper.assertTrue(background == InteractionResult.FAIL && level.getBlockState(ground).isAir()
                && player.getMainHandItem().getCount() == 2, "background world use is rejected without item/world mutation");
        interaction.interact(player, interact(ground, true, true), SID);
        helper.assertTrue(level.getBlockState(target).is(Blocks.TNT), "real ServerPlayerGameMode placed vanilla TNT");
        helper.assertTrue(building.ownsVisible(target) && building.ownsNative(target)
                && NativeWorldGuard.observedMutationCount() > 0, "production Level.setBlock mixin immediately records TNT ownership");
        helper.assertTrue(player.getMainHandItem().getCount() == 1, "vanilla Survival placement consumes one actual TNT item");
        release(interaction, player);

        atTick.accept(2L, () -> {
            helper.assertTrue(building.ownsNative(target), "ownership survives later real server ticks");
            building.validatePose(target.getX() + .5, target.getY(), target.getZ() + .5);
            boolean rejected = false;
            try { building.validatePose(foreignTnt.getX() + .5, foreignTnt.getY(), foreignTnt.getZ() + .5); }
            catch (IllegalArgumentException expected) { rejected = true; }
            helper.assertTrue(rejected && !building.ownsVisible(foreignTnt), "untracked foreign TNT is never adopted or ignored");
            near[0] = BridgeTargetEntity.spawn(level.getServer(), 41, ground.getX() + 2.5, ground.getY() + 1, ground.getZ() + .5);
            far[0] = BridgeTargetEntity.spawn(level.getServer(), 42, ground.getX() + 20.5, ground.getY() + 1, ground.getZ() + .5);
            helper.assertTrue(near[0] != null && far[0] != null, "real living actor proxies spawned");
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
            interaction.interact(player, interact(target, false, true), SID);
            helper.assertTrue(level.getBlockState(target).isAir() && !building.ownsVisible(target), "native ignition removes the owned TNT block");
            helper.assertTrue(TntRuntime.ownedCount() == 1, "native spawned PrimedTnt is owned and mirrored");
            helper.assertTrue(player.getMainHandItem().getDamageValue() == 1, "native flint ignition consumes one durability");
            helper.assertTrue(!player.isUsingItem(), "successful main-hand flint ignition never raises offhand shield");
            var entities = level.getEntitiesOfClass(PrimedTnt.class, new AABB(target).inflate(2));
            helper.assertTrue(entities.size() == 1 && entities.get(0).getFuse() == PrimedTnt.DEFAULT_FUSE_TIME,
                    "actual primed TNT starts with the native eighty-tick fuse");
            release(interaction, player);
            player.connection.teleport(ground.getX() - 20, ground.getY() + 1, ground.getZ() - 20, 0, 0);
        });
        atTick.accept(35L, () -> {
            helper.assertTrue(reliable.isEmpty(), "native TNT does not explode before its fuse expires");
            helper.assertTrue(states.stream().anyMatch(message -> message.get("alive").getAsBoolean()
                    && message.get("fuse").getAsInt() > 0 && message.get("fuse").getAsInt() < 80),
                    "fuse/motion snapshots come from the ticking native entity");
        });
        atTick.accept(100L, () -> {
            long explosions = reliable.stream().filter(message -> Proto.type(message).equals("explosion")).count();
            helper.assertTrue(explosions == 1, "one real vanilla explosion produces exactly one visual/audio event");
            helper.assertTrue(TntRuntime.ownedCount() == 0 && states.stream().anyMatch(message -> !message.get("alive").getAsBoolean()),
                    "exploded TNT publishes removal and leaves no live owned entity");
            JsonObject hit = reliable.stream().filter(message -> Proto.type(message).equals("explosion_hit")
                    && message.get("eid").getAsInt() == 41).findFirst().orElse(null);
            helper.assertTrue(hit != null && hit.get("damage").getAsFloat() > 0, "real MC explosion applies captured native area damage to nearby actor");
            helper.assertTrue(reliable.stream().noneMatch(message -> Proto.type(message).equals("explosion_hit")
                    && message.get("eid").getAsInt() == 42), "actor outside native blast range receives no fabricated damage");
            helper.assertTrue(Proto.type(reliable.getFirst()).equals("explosion")
                    && hit.get("blastId").getAsString().equals(reliable.getFirst().get("blastId").getAsString()),
                    "reliable explosion event precedes and correlates native damage hits");
            helper.assertTrue(reliable.stream().map(message -> message.get("eventId").getAsString()).distinct().count() == reliable.size(),
                    "all gameplay events have distinct retry/dedup identifiers");
            JsonObject capturedBlast = reliable.getFirst().deepCopy();
            JsonObject capturedNearHit = hit.deepCopy();
            JsonObject nativeEvidence = new JsonObject();
            nativeEvidence.addProperty("success", true);
            nativeEvidence.addProperty("source", "TntNativeGameTest.placeIgniteFuseExplode / production TntRuntime reliable sink");
            nativeEvidence.addProperty("nearEid", 41); nativeEvidence.addProperty("farEid", 42);
            nativeEvidence.addProperty("nearX", near[0].getX()); nativeEvidence.addProperty("nearY", near[0].getY()); nativeEvidence.addProperty("nearZ", near[0].getZ());
            nativeEvidence.addProperty("farX", far[0].getX()); nativeEvidence.addProperty("farY", far[0].getY()); nativeEvidence.addProperty("farZ", far[0].getZ());
            nativeEvidence.addProperty("actualDamage", hit.get("damage").getAsFloat());
            nativeEvidence.addProperty("actualRadius", capturedBlast.get("radius").getAsFloat());
            nativeEvidence.addProperty("blastId", hit.get("blastId").getAsString());
            nativeEvidence.addProperty("farHitCount", 0);
            nativeEvidence.addProperty("nearProxyHealth", near[0].getHealth());
            nativeEvidence.addProperty("mcToBlDamageFactor", MycraftBridge.CONFIG.mcToBlDamageFactor);
            nativeEvidence.addProperty("tntPeakHostDamage", TntRuntime.HOST_PEAK_DAMAGE);
            nativeEvidence.addProperty("tntNativePeakDamage", 57);
            nativeEvidence.addProperty("damageAlreadyInHostUnits", true);
            nativeEvidence.addProperty("note", "Actual native explosion events; damage/radius/IDs unchanged. Proxy HP is intentionally capture-only. Positive seq values emulate BridgeClient enqueue. Host must map eid41 to its test agent and independently assert health loss and duplicate suppression.");
            helper.assertTrue(level.getBlockState(foreignStone).is(Blocks.STONE)
                    && level.getBlockState(foreignTnt).is(Blocks.TNT), "native explosion preserves original stone and unrelated TNT");
            player.connection.teleport(ground.getX() + .5, ground.getY() + 1, ground.getZ() - 2, 0, 30);
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT));
            interaction.interact(player, interact(ground, true, true), SID);
            release(interaction, player);
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
            interaction.interact(player, interact(target, false, true), SID);
            helper.assertTrue(TntRuntime.ownedCount() == 1, "a second real native fuse is running when the session ends");
            release(interaction, player);
            BlockPos capGround = ground.offset(1, 0, 0), capTarget = capGround.above();
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT));
            interaction.interact(player, interact(capGround, true, true), SID);
            helper.assertTrue(building.ownsNative(capTarget), "capacity test has a genuinely owned TNT block");
            release(interaction, player);
            var fuseSource = level.getEntitiesOfClass(PrimedTnt.class, new AABB(target).inflate(2)).getFirst();
            NativeWorldGuard.enter(level, target);
            try {
                for (int index = 1; index < TntRuntime.MAX_TNT; index++) {
                    PrimedTnt extra = new PrimedTnt(level, target.getX() + .5, target.getY(), target.getZ() + .5, player);
                    helper.assertTrue(level.addFreshEntity(extra), "native capacity fixture entity registered");
                }
            } finally { NativeWorldGuard.exit(); }
            helper.assertTrue(TntRuntime.ownedCount() == TntRuntime.MAX_TNT, "native active fuse budget reached exactly");
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
            interaction.interact(player, interact(capTarget, false, true), SID);
            helper.assertTrue(level.getBlockState(capTarget).is(Blocks.TNT)
                    && player.getMainHandItem().getDamageValue() == 0 && TntRuntime.ownedCount() == TntRuntime.MAX_TNT,
                    "capacity rejects manual ignition without consuming original TNT or flint durability");
            var chain = new net.minecraft.world.level.ServerExplosion(level, fuseSource, null, null,
                    Vec3.atCenterOf(capTarget), 4, false, net.minecraft.world.level.Explosion.BlockInteraction.DESTROY);
            level.getBlockState(capTarget).onExplosionHit(level, capTarget, chain, (stack, pos) -> { });
            helper.assertTrue(level.getBlockState(capTarget).is(Blocks.TNT) && building.ownsNative(capTarget)
                    && TntRuntime.ownedCount() == TntRuntime.MAX_TNT,
                    "capacity rejects chained ignition BEFORE vanilla removes its block");

            var capturedLoot = new net.minecraft.world.entity.item.ItemEntity(level,
                    target.getX() + .5, target.getY() + 1, target.getZ() + .5, new ItemStack(Items.DIAMOND, 7));
            capturedLoot.setExtendedLifetime(); capturedLoot.setPickUpDelay(73);
            capturedLoot.setTarget(player.getUUID()); capturedLoot.setThrower(player);
            NativeWorldGuard.enter(level, target);
            try { helper.assertTrue(level.addFreshEntity(capturedLoot), "owned native loot entity is tracked by the real spawn mixin"); }
            finally { NativeWorldGuard.exit(); }
            var originalLootStack = capturedLoot.getItem();
            var originalLootId = capturedLoot.getUUID();
            int originalLootAge = capturedLoot.getAge();
            var originalLootOwner = capturedLoot.getOwner();
            Vec3 unrelatedPosition = Vec3.atCenterOf(target.offset(3, 1, 3));
            var unrelatedLoot = new net.minecraft.world.entity.item.ItemEntity(level,
                    unrelatedPosition.x, unrelatedPosition.y, unrelatedPosition.z, new ItemStack(Items.GOLD_INGOT, 3));
            helper.assertTrue(level.addFreshEntity(unrelatedLoot), "unrelated native loot is present but not adopted");
            // The real pre-connect position can be outside the staging area's loaded chunks.
            Vec3 returnPosition = Vec3.atCenterOf(ground.offset(-512, 1, -512));
            NativeWorldGuard.preserveItemDrops(level, returnPosition);
            helper.assertTrue(capturedLoot.position().distanceToSqr(returnPosition) < 1.0e-8
                    && capturedLoot.getUUID().equals(originalLootId) && capturedLoot.getItem() == originalLootStack
                    && capturedLoot.getItem().getCount() == 7 && capturedLoot.getAge() == originalLootAge
                    && capturedLoot.getOwner() == originalLootOwner && capturedLoot.hasPickUpDelay(),
                    "native loot return moves the same entity and stack without resetting age, owner or pickup delay");
            helper.assertTrue(unrelatedLoot.position().equals(unrelatedPosition), "loot outside ownership is never moved to the return point");
            Vec3 retainedPosition = Vec3.atCenterOf(target.offset(-3, 1, 3));
            var retainedLoot = new net.minecraft.world.entity.item.ItemEntity(level,
                    retainedPosition.x, retainedPosition.y, retainedPosition.z, new ItemStack(Items.COBBLESTONE, 4));
            NativeWorldGuard.enter(level, target);
            try { helper.assertTrue(level.addFreshEntity(retainedLoot), "cross-world fallback fixture is tracked native loot"); }
            finally { NativeWorldGuard.exit(); }
            NativeWorldGuard.preserveItemDrops(level.getServer().getLevel(net.minecraft.world.level.Level.NETHER), returnPosition);
            helper.assertTrue(retainedLoot.position().equals(retainedPosition) && !retainedLoot.isRemoved(),
                    "a mismatched return dimension preserves the original native loot in place");
            interaction.clear(player); TntRuntime.clear(); building.clear();
            helper.assertTrue(!capturedLoot.isRemoved() && !unrelatedLoot.isRemoved()
                    && capturedLoot.getItem() == originalLootStack,
                    "cleanup preserves returned Survival loot and untouched unrelated loot without duplicate stacks");
            helper.assertTrue(!retainedLoot.isRemoved() && retainedLoot.position().equals(retainedPosition)
                    && retainedLoot.getItem().getCount() == 4, "cleanup cannot delete loot whose return dimension was unavailable");
            helper.assertTrue(TntRuntime.ownedCount() == 0
                    && level.getEntitiesOfClass(PrimedTnt.class, new AABB(target).inflate(3)).stream().allMatch(PrimedTnt::isRemoved),
                    "session cleanup discards a still-burning native TNT before it can explode");
            helper.assertTrue(reliable.stream().filter(message -> Proto.type(message).equals("explosion")).count() == 1,
                    "cleanup cannot synthesize an extra explosion or area damage");
            helper.assertTrue(level.getBlockState(ground).isAir(), "session cleanup removes only its temporary ground support");
            helper.assertTrue(level.getBlockState(ground.offset(4, 0, 4)).isAir(), "the complete sampled TNT terrain patch is cleaned up");
            helper.assertTrue(level.getBlockState(foreignTnt).is(Blocks.TNT), "cleanup still cannot remove unowned TNT");
            capturedLoot.discard(); unrelatedLoot.discard(); retainedLoot.discard(); near[0].discard(); far[0].discard();
            finished[0] = true;
            flatChain[0] = nativeChain(helper, player, ground, 1);
            tickPhase[0] = flatChain[0].tick;
            beginFractional[0] = () -> {
                fractional[0] = fractionalTerrain(helper, player, ground,
                        () -> exportNativeDamageFixture(helper, capturedBlast, capturedNearHit, nativeEvidence));
                tickPhase[0] = fractional[0].tick;
            };
        });
    }

    private static NativePhase nativeChain(GameTestHelper helper, net.minecraft.server.level.ServerPlayer player,
                                           BlockPos ground, double surfaceHeight) {
        var phase = new NativePhase();
        var level = helper.getLevel();
        var building = new BuildingRuntime();
        var interaction = new NativeInteractionRuntime(building);
        var events = new ArrayList<JsonObject>();
        var states = new ArrayList<JsonObject>();
        var nativeTnts = new java.util.LinkedHashMap<String, PrimedTnt>();
        final boolean[] finished = {false};
        double surface = ground.getY() + surfaceHeight;
        BlockPos target = surfaceHeight < 1 ? ground : ground.above();
        for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) for (int y = 0; y <= 2; y++)
            level.setBlock(ground.offset(x, y, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        // Foreign obstacles sit above the sampled terrain cells; a ground sample itself
        // overlapping original terrain is correctly rejected before any native placement.
        BlockPos foreignTnt = ground.above().offset(-1, 0, 0), foreignStone = ground.above().offset(0, 0, 2);
        level.setBlock(foreignTnt, Blocks.TNT.defaultBlockState(), Block.UPDATE_ALL);
        level.setBlock(foreignStone, Blocks.STONE.defaultBlockState(), Block.UPDATE_ALL);
        player.connection.teleport(ground.getX() + .5, surface, ground.getZ() - 2, 0, 30);
        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT, 3));
        building.begin(player, SID);
        TntRuntime.begin(SID, player, message -> events.add(message.deepCopy()), message -> {
            states.add(message.deepCopy());
            String id = message.get("entityId").getAsString();
            if (level.getEntity(UUID.fromString(id)) instanceof PrimedTnt tnt) nativeTnts.putIfAbsent(id, tnt);
        });
        var peak = new BridgeTargetEntity[1];
        var foreignLiving = net.minecraft.world.entity.EntityTypes.VILLAGER.create(level,
                net.minecraft.world.entity.EntitySpawnReason.TRIGGERED);
        helper.assertTrue(foreignLiving != null, "foreign blast fixture is an actual native living entity");
        foreignLiving.setNoAi(true); foreignLiving.setNoGravity(true); foreignLiving.noPhysics = true;
        Vec3 foreignPosition = new Vec3(ground.getX() + .5, surface + .5, ground.getZ() + 1.4);
        foreignLiving.setPos(foreignPosition.x, foreignPosition.y, foreignPosition.z);
        foreignLiving.setDeltaMovement(Vec3.ZERO);
        helper.assertTrue(level.addFreshEntity(foreignLiving) && !NativeWorldGuard.ownsEntity(foreignLiving),
                "foreign living blast target is spawned outside all session ownership scopes");
        float foreignHealth = foreignLiving.getHealth();
        phase.cleanup = () -> {
            interaction.clear(player); TntRuntime.clear(); building.clear();
            if (peak[0] != null) peak[0].discard();
            foreignLiving.discard();
        };
        phase.tick = () -> { if (!finished[0]) { interaction.tick(player); building.tick(); TntRuntime.tick(); } };
        for (int x = 0; x < 3; x++) {
            BlockPos support = ground.offset(x, 0, 0);
            JsonObject placement = interact(support, true, true);
            placement.addProperty("surfaceH", surfaceHeight);
            placement.addProperty("hitY", surface);
            for (int index = 0; index < 81; index++) placement.addProperty("ground" + index + "H", surfaceHeight);
            interaction.interact(player, placement, SID);
            helper.assertTrue(level.getBlockState(target.offset(x, 0, 0)).is(Blocks.TNT)
                    && building.ownsNative(target.offset(x, 0, 0)), "chain fixture uses three genuine Survival-placed owned TNT blocks");
            release(interaction, player);
        }
        helper.assertTrue(player.getMainHandItem().isEmpty(), "native chained fixture consumes exactly three actual TNT items");
        var flame = new net.minecraft.world.entity.projectile.arrow.Arrow(level, player,
                new ItemStack(Items.ARROW), new ItemStack(Items.BOW));
        flame.setRemainingFireTicks(100);
        NativeWorldGuard.enter(level, target);
        try { helper.assertTrue(level.addFreshEntity(flame), "foreign-TNT regression uses a genuinely tracked burning native arrow"); }
        finally { NativeWorldGuard.exit(); }
        NativeWorldGuard.enterEntity(level, flame);
        try {
            var original = level.getBlockState(foreignTnt);
            original.onProjectileHit(level, original,
                    new BlockHitResult(Vec3.atCenterOf(foreignTnt), Direction.UP, foreignTnt, false), flame);
        } finally { NativeWorldGuard.exit(); }
        helper.assertTrue(level.getBlockState(foreignTnt).is(Blocks.TNT) && TntRuntime.ownedCount() == 0,
                "a session-owned burning arrow cannot create a duplicate native fuse from a protected foreign TNT block");
        flame.discard();
        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
        interaction.interact(player, interact(target, false, true), SID);
        release(interaction, player);
        PrimedTnt first = level.getEntitiesOfClass(PrimedTnt.class, new AABB(target).inflate(.1)).getFirst();
        helper.assertTrue(first.getFuse() == 80 && TntRuntime.ownedCount() == 1,
                "only the first TNT is ignited manually with the native eighty-tick fuse");
        // Keep this native source still and shorten only its fixture fuse. The neighbor
        // selection, short fuses, motion and explosions run through unchanged vanilla code.
        first.setNoGravity(true); first.setDeltaMovement(Vec3.ZERO); first.setFuse(2);
        Vec3 center = new Vec3(first.getX(), first.getY(.0625), first.getZ());
        peak[0] = BridgeTargetEntity.spawn(level.getServer(), 51, center.x, center.y, center.z);
        helper.assertTrue(peak[0] != null && net.minecraft.world.level.ServerExplosion.getSeenPercent(center, peak[0]) == 1,
                "peak fixture is at the actual native explosion center with full exposure");
        var nativePeak = new net.minecraft.world.level.ServerExplosion(level, first, null, null, center, 4, false,
                net.minecraft.world.level.Explosion.BlockInteraction.DESTROY);
        helper.assertTrue(new net.minecraft.world.level.ExplosionDamageCalculator().getEntityDamageAmount(nativePeak, peak[0], 1) == 57,
                "pinned native damage calculator independently verifies radius-four TNT maximum is 57");
        double weaponFactor = MycraftBridge.CONFIG.mcToBlDamageFactor;
        player.connection.teleport(ground.getX() - 20, ground.getY() + 1, ground.getZ() - 20, 0, 0);
        phase.early = () -> {
            helper.assertTrue(events.stream().filter(message -> Proto.type(message).equals("explosion")).count() == 1,
                    "first native fuse expires and emits exactly its own blast before the chains");
            var chainStarts = states.stream().filter(message -> message.get("alive").getAsBoolean()
                    && !message.get("entityId").getAsString().equals(first.getUUID().toString()))
                    .collect(java.util.stream.Collectors.toMap(message -> message.get("entityId").getAsString(),
                            message -> message, (initial, subsequent) -> initial));
            helper.assertTrue(chainStarts.size() == 2 && chainStarts.values().stream().allMatch(message -> {
                int fuse = message.get("fuse").getAsInt(); return fuse >= 10 && fuse <= 29;
            }), "vanilla first explosion selects both neighbors and assigns actual randomized 10..29 tick chain fuses");
            helper.assertTrue(!level.getBlockState(target.offset(1, 0, 0)).is(Blocks.TNT)
                    && !level.getBlockState(target.offset(2, 0, 0)).is(Blocks.TNT)
                    && !building.ownsVisible(target.offset(1, 0, 0)) && !building.ownsVisible(target.offset(2, 0, 0)),
                    "native chained priming removes the original two owned TNT blocks before temporary terrain restores");
            JsonObject hit = events.stream().filter(message -> Proto.type(message).equals("explosion_hit")
                    && message.get("eid").getAsInt() == 51).findFirst().orElse(null);
            helper.assertTrue(hit != null && Math.abs(hit.get("damage").getAsFloat() - 100) < .0001,
                    "real native full-exposure TNT damage exports 100 host damage");
            helper.assertTrue(MycraftBridge.CONFIG.mcToBlDamageFactor == weaponFactor,
                    "TNT maximum does not mutate the unrelated weapon conversion factor");
            helper.assertTrue(foreignLiving.getHealth() == foreignHealth && foreignLiving.position().distanceToSqr(foreignPosition) < 1e-12
                    && foreignLiving.getDeltaMovement().lengthSqr() < 1e-12,
                    "owned TNT never injures, moves or pushes a nearby foreign native living entity");
            peak[0].discard();
        };
        phase.complete = () -> {
            if (events.stream().filter(message -> Proto.type(message).equals("explosion")).count() != 3 || TntRuntime.ownedCount() != 0)
                logChainPhysics(level, states, nativeTnts, surfaceHeight, TntRuntime.ownedCount());
            helper.assertTrue(events.stream().filter(message -> Proto.type(message).equals("explosion")).count() == 3
                    && TntRuntime.ownedCount() == 0, "all three native TNT entities complete separate actual explosions and retire");
            helper.assertTrue(events.stream().filter(message -> Proto.type(message).equals("explosion"))
                    .map(message -> message.get("blastId").getAsString()).distinct().count() == 3,
                    "each native chained explosion keeps a distinct reliable blast identifier");
            helper.assertTrue(level.getBlockState(foreignTnt).is(Blocks.TNT) && level.getBlockState(foreignStone).is(Blocks.STONE),
                    "nearby foreign TNT is neither primed nor removed, and foreign terrain survives every chain");
            helper.assertTrue(foreignLiving.getHealth() == foreignHealth && foreignLiving.position().distanceToSqr(foreignPosition) < 1e-12
                    && foreignLiving.getDeltaMovement().lengthSqr() < 1e-12,
                    "every chained native blast preserves foreign living health, position and motion");
            if (surfaceHeight < 1) {
                // The terrain correction must apply to air above a support only. A real
                // native blast inside that same support must still lose all ray strength.
                BlockPos retained = target.offset(2, 0, 0);
                NativeWorldGuard.enter(level, retained);
                try { level.setBlock(retained, Blocks.TNT.defaultBlockState(), Block.UPDATE_ALL); }
                finally { NativeWorldGuard.exit(); }
                var buried = new PrimedTnt(level, ground.getX() + .5, surface, ground.getZ() + .5, player);
                NativeWorldGuard.enter(level, target);
                try { helper.assertTrue(level.addFreshEntity(buried), "below-surface test source is genuinely session-owned native TNT"); }
                finally { NativeWorldGuard.exit(); }
                var buriedBlast = new net.minecraft.world.level.ServerExplosion(level, buried, null, null,
                        new Vec3(ground.getX() + .5, ground.getY() + .25, ground.getZ() + .5), 4, false,
                        net.minecraft.world.level.Explosion.BlockInteraction.DESTROY);
                buriedBlast.explode();
                helper.assertTrue(level.getBlockState(retained).is(Blocks.TNT) && TntRuntime.ownedCount() == 1,
                        "ray samples below the partial terrain surface retain native resistance and cannot ignite a neighbor");
                var budgetItems = new ArrayList<net.minecraft.world.entity.item.ItemEntity>();
                NativeWorldGuard.enter(level, retained);
                try {
                    // One live native TNT plus 1023 tracked item entities fill the regular
                    // spawn budget without reaching the separate thirty-two-fuse limit.
                    for (int index = 0; index < 1023; index++) {
                        var item = new net.minecraft.world.entity.item.ItemEntity(level,
                                retained.getX() + .5, retained.getY() + 2, retained.getZ() + .5, new ItemStack(Items.COBBLESTONE));
                        helper.assertTrue(level.addFreshEntity(item), "regular entity-capacity fixture is registered by production native spawn hooks");
                        budgetItems.add(item);
                    }
                    helper.assertTrue(!TntRuntime.canPrime(level) && TntRuntime.ownedCount() == 1,
                            "full regular entity budget rejects TNT before the independent fuse budget is exhausted");
                    level.getBlockState(retained).onExplosionHit(level, retained, buriedBlast, (stack, pos) -> { });
                    helper.assertTrue(level.getBlockState(retained).is(Blocks.TNT) && building.ownsNative(retained)
                            && TntRuntime.ownedCount() == 1,
                            "regular entity capacity cannot consume a chained TNT block whose native fuse cannot spawn");
                } finally {
                    NativeWorldGuard.exit();
                    for (var item : budgetItems) item.discard();
                }
            }
            BridgeRuntime.log("NATIVE_TNT_CHAIN_PASS surface=" + surfaceHeight + " nativeShortFuse=10..29 blasts=3 peakHostDamage=100 foreignProtected=true");
            interaction.clear(player); TntRuntime.clear(); building.clear();
            foreignLiving.discard();
            helper.assertTrue(level.getBlockState(foreignTnt).is(Blocks.TNT) && level.getBlockState(foreignStone).is(Blocks.STONE),
                    "chain cleanup preserves all foreign original blocks");
            finished[0] = true;
        };
        return phase;
    }

    private static Runnable holdFixtureChunks(net.minecraft.server.level.ServerLevel level, BlockPos ground) {
        var previous = new java.util.LinkedHashMap<ChunkPos, Boolean>();
        // Three 9x9 samples cover X=-4..6/Z=-4..4. Include four more cells for
        // native bounce/blast motion; loading terrain alone does not tick entities.
        for (int x = (ground.getX() - 8) >> 4; x <= (ground.getX() + 10) >> 4; x++)
            for (int z = (ground.getZ() - 8) >> 4; z <= (ground.getZ() + 8) >> 4; z++) {
                var chunk = new ChunkPos(x, z);
                previous.put(chunk, level.getChunkSource().getForceLoadedChunks().contains(chunk.pack()));
                level.setChunkForced(x, z, true);
            }
        return () -> previous.forEach((chunk, forced) -> level.setChunkForced(chunk.x(), chunk.z(), forced));
    }

    private static void logChainPhysics(net.minecraft.server.level.ServerLevel level, ArrayList<JsonObject> states,
                                         java.util.Map<String, PrimedTnt> nativeTnts,
                                         double surface, int owned) {
        var latest = new java.util.LinkedHashMap<String, JsonObject>();
        for (var state : states) latest.put(state.get("entityId").getAsString(), state);
        for (var entry : latest.entrySet()) {
            var state = entry.getValue();
            var pos = BlockPos.containing(state.get("x").getAsDouble(), state.get("y").getAsDouble(), state.get("z").getAsDouble());
            var entity = nativeTnts.get(entry.getKey());
            BridgeRuntime.log("TNT_CHAIN_PHYSICS surface=" + surface + " owned=" + owned + " uuid=" + entry.getKey()
                    + " lastState=" + state + " chunk=" + ChunkPos.containing(pos) + " loaded=" + level.isLoaded(pos)
                    + " entityTicking=" + level.isPositionEntityTicking(pos)
                    + " registered=" + (entity != null && level.getEntity(UUID.fromString(entry.getKey())) == entity)
                    + (entity != null ? " nativeFuse=" + entity.getFuse() + " delta=" + entity.getDeltaMovement()
                            + " nativeTick=" + entity.tickCount + " nativePos=" + entity.position() + " removed=" + entity.getRemovalReason() : " nativeEntity=null"));
        }
    }

    private static NativePhase fractionalTerrain(GameTestHelper helper, net.minecraft.server.level.ServerPlayer player, BlockPos ground, Runnable exportEvidence) {
        var phase = new NativePhase();
        var level = helper.getLevel();
        var building = new BuildingRuntime();
        var interaction = new NativeInteractionRuntime(building);
        var events = new ArrayList<JsonObject>();
        var states = new ArrayList<JsonObject>();
        final boolean[] finished = {false};
        final PrimedTnt[] primed = {null};
        double surface = ground.getY() + .625;
        for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) {
            level.setBlock(ground.offset(x, 0, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
            level.setBlock(ground.offset(x, 1, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
        }
        BlockPos foreign = ground.offset(-2, 1, 0);
        level.setBlock(foreign, Blocks.STONE.defaultBlockState(), Block.UPDATE_ALL);
        player.connection.teleport(ground.getX() + .5, surface, ground.getZ() - 2, 0, 30);
        player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.TNT));
        building.begin(player, SID);
        TntRuntime.begin(SID, player, message -> events.add(message.deepCopy()), message -> states.add(message.deepCopy()));
        phase.tick = () -> { if (!finished[0]) { interaction.tick(player); building.tick(); TntRuntime.tick(); } };
        phase.cleanup = () -> { interaction.clear(player); TntRuntime.clear(); building.clear(); };
        JsonObject placement = interact(ground, true, true);
        placement.addProperty("surfaceH", .625);
        placement.addProperty("hitY", surface);
        for (int index = 0; index < 81; index++) placement.addProperty("ground" + index + "H", .625);
        interaction.interact(player, placement, SID);
        helper.assertTrue(level.getBlockState(ground).is(Blocks.TNT) && level.getBlockState(ground.above()).isAir()
                && building.ownsNative(ground), "fractional .625 terrain permits real TNT replacement in the sampled current cell");
        var nearbySupport = level.getBlockState(ground.offset(1, 0, 0));
        helper.assertTrue(HostGroundBlock.isGround(nearbySupport)
                && nearbySupport.getCollisionShape(level, ground.offset(1, 0, 0)).bounds().maxY == .625
                && nearbySupport.getRenderShape() == net.minecraft.world.level.block.RenderShape.INVISIBLE
                && nearbySupport.getBlock().asItem() == Items.AIR,
                "the partial terrain proxy has native .625 collision, invisible rendering and no inventory item");
        helper.assertTrue(HostGroundBlock.state(.7).getValue(HostGroundBlock.HEIGHT) == 179
                && HostGroundBlock.canSinkTnt(HostGroundBlock.state(.7))
                && !HostGroundBlock.canSinkTnt(HostGroundBlock.state(180.0 / 256)),
                "TNT replaceClicked threshold uses the same quantized 179/256 boundary as the host");
        release(interaction, player);
        phase.ignite = () -> {
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.FLINT_AND_STEEL));
            interaction.interact(player, interact(ground, false, true), SID);
            primed[0] = level.getEntitiesOfClass(PrimedTnt.class, new AABB(ground).inflate(2)).getFirst();
            helper.assertTrue(Math.abs(primed[0].getY() - surface) < 1.0e-8 && primed[0].getFuse() == 80
                    && Math.abs(primed[0].getDeltaMovement().y - .2) < 1.0e-6
                    && Math.abs(primed[0].getDeltaMovement().horizontalDistance() - .02) < 1.0e-6,
                    "primed TNT starts exactly on real terrain and retains vanilla bounce impulse, drift impulse and fuse");
            helper.assertTrue(level.getBlockState(ground).isAir() && !building.ownsVisible(ground),
                    "igniting embedded TNT removes its visible block before hidden support restoration");
            building.tick();
            helper.assertTrue(HostGroundBlock.isGround(level.getBlockState(ground))
                    && HostGroundBlock.height(level.getBlockState(ground)) == .625 && !building.ownsVisible(ground),
                    "next building tick restores the original partial support without exposing a fake block_state");
            release(interaction, player);
            player.connection.teleport(ground.getX() - 20, ground.getY() + 1, ground.getZ() - 20, 0, 0);
        };
        phase.midpoint = () -> {
            helper.assertTrue(primed[0] != null && !primed[0].isRemoved() && Math.abs(primed[0].getY() - surface) < .005,
                    "native TNT gravity and collision settle on .625 terrain without floating or sinking");
            helper.assertTrue(primed[0].getFuse() > 0 && primed[0].getFuse() < 60,
                    "fractional terrain fixture executes actual native entity ticks rather than an unloaded frozen entity");
            helper.assertTrue(events.isEmpty(), "partial terrain does not shortcut the vanilla fuse");
        };
        phase.complete = () -> {
            helper.assertTrue(events.stream().filter(message -> Proto.type(message).equals("explosion")).count() == 1,
                    "fractional terrain TNT completes its real native explosion");
            helper.assertTrue(level.getBlockState(foreign).is(Blocks.STONE), "fractional terrain explosion still protects foreign original blocks");
            interaction.clear(player); TntRuntime.clear(); building.clear();
            helper.assertTrue(level.getBlockState(ground).isAir() && level.getBlockState(ground.offset(4, 0, 4)).isAir()
                    && level.getBlockState(foreign).is(Blocks.STONE), "cleanup removes partial proxy terrain while retaining original blocks");
            finished[0] = true;
            player.discard();
            exportEvidence.run();
            helper.succeed();
        };
        return phase;
    }

    private static void exportNativeDamageFixture(GameTestHelper helper, JsonObject blast, JsonObject hit, JsonObject evidence) {
        // runGameTest's isolated working directory is fabric/build/run/tntGameTest.
        // Export only once the complete native scenario (including range and grounding) has passed.
        java.nio.file.Path build = java.nio.file.Path.of("..", "..").toAbsolutePath().normalize();
        java.nio.file.Path eventsPath = build.resolve("tnt-native-events.jsonl");
        java.nio.file.Path evidencePath = build.resolve("tnt-native-events.meta.json");
        blast.addProperty("seq", 1); hit.addProperty("seq", 2);
        helper.assertTrue(Proto.validateEnvelope(blast) && Proto.validateEnvelope(hit), "exported native events have genuine valid wire envelopes");
        try {
            java.nio.file.Files.createDirectories(build);
            java.nio.file.Files.write(eventsPath, java.util.List.of(blast.toString(), hit.toString()), java.nio.charset.StandardCharsets.UTF_8);
            java.nio.file.Files.writeString(evidencePath, evidence.toString(), java.nio.charset.StandardCharsets.UTF_8);
        } catch (java.io.IOException exception) {
            throw new IllegalStateException("Could not export native explosion evidence", exception);
        }
        BridgeRuntime.log("NATIVE_TNT_FIXTURE events=" + eventsPath + " damage=" + hit.get("damage")
                + " radius=" + blast.get("radius") + " nearEid=41 farEid=42 farHitCount=0 success=true");
    }

    private static JsonObject interact(BlockPos target, boolean terrain, boolean canPlace) {
        JsonObject message = Proto.msg(SID, 1, "interact");
        message.addProperty("requestId", UUID.randomUUID().toString()); message.addProperty("pressed", true);
        message.addProperty("target", true); message.addProperty("x", target.getX()); message.addProperty("y", target.getY());
        message.addProperty("z", target.getZ()); message.addProperty("face", 1);
        message.addProperty("hitX", target.getX() + .5); message.addProperty("hitY", target.getY() + 1);
        message.addProperty("hitZ", target.getZ() + .5); message.addProperty("terrain", terrain); message.addProperty("canPlace", canPlace);
        if (terrain) {
            message.addProperty("groundCount", 81);
            int index = 0;
            for (int x = -4; x <= 4; x++) for (int z = -4; z <= 4; z++) {
                message.addProperty("ground" + index + "X", target.getX() + x);
                message.addProperty("ground" + index + "Y", target.getY());
                message.addProperty("ground" + index + "Z", target.getZ() + z);
                index++;
            }
        }
        return message;
    }
    private static void release(NativeInteractionRuntime interaction, net.minecraft.server.level.ServerPlayer player) {
        JsonObject message = Proto.msg(SID, 1, "interact");
        message.addProperty("requestId", UUID.randomUUID().toString()); message.addProperty("pressed", false); message.addProperty("target", false);
        interaction.interact(player, message, SID);
    }
}

