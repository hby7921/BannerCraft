package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.core.component.DataComponents;
import net.minecraft.core.registries.Registries;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.network.chat.Component;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.commands.FillBiomeCommand;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.npc.villager.Villager;
import net.minecraft.world.entity.LightningBolt;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.entity.projectile.arrow.ThrownTrident;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.item.component.ChargedProjectiles;
import net.minecraft.world.item.component.FireworkExplosion;
import net.minecraft.world.item.component.Fireworks;
import net.minecraft.world.item.enchantment.Enchantments;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.saveddata.WeatherData;
import net.minecraft.world.level.biome.Biomes;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;

/** Real vanilla use/ticks/projectile impacts, with production ownership and damage mixins. */
public final class WeaponNativeGameTest {
    private static final String SID = "11223344556677889900aabbccddeeff";

    // Ownership is a single active session. Start after the TNT fixture's 420-tick limit.
    @GameTest(maxTicks = 450, setupTicks = 450, skyAccess = true, padding = 16)
    public void nativeWeaponUseDamageAndCleanup(GameTestHelper helper) {
        var fixture = new Fixture(helper);
        var player = fixture.player;
        var bow = new ItemStack(Items.BOW);
        var crossbow = new ItemStack(Items.CROSSBOW);
        final ThrownTrident[] thrown = {null};
        final ItemStack[] originalPickup = {null};
        final ThrownTrident[] retained = {null};
        final ThrownTrident[] capacityThrown = {null};
        final ItemStack[] capacityPickup = {null};
        final int[] foreignFireContactTick = {0};
        var beforeBowCancel = new HashSet<String>();
        var beforeTridentClear = new HashSet<String>();
        final float[] jabDamage = {0};

        fixture.target(101, 7);
        player.setItemSlot(EquipmentSlot.MAINHAND, bow);
        fixture.press();
        helper.assertTrue(!player.isUsingItem() && fixture.projectileIds("arrow").isEmpty(),
                "Survival bow without actual ammunition cannot draw or fabricate an arrow");
        fixture.release();
        player.getInventory().setItem(1, new ItemStack(Items.ARROW, 2));
        fixture.press();
        helper.assertTrue(player.isUsingItem(), "actual vanilla bow begins held item use");

        helper.runAtTickTime(25, () -> {
            helper.assertTrue(player.getTicksUsingItem() >= 20, "native player ticks accumulate the bow draw");
            var state = Proto.msg(SID, 0, "player_state");
            WeaponRuntime.addPlayerState(state, player);
            helper.assertTrue(Proto.string(state, "weaponKind").equals("bow")
                    && Proto.integer(state, "useTicks") >= 20, "weapon classification and draw state come from the native stack");
            fixture.release();
            helper.assertTrue(!player.isUsingItem() && player.getInventory().getItem(1).getCount() == 1
                    && bow.getDamageValue() == 1, "native release consumes one real arrow and one bow durability");
        });
        helper.runAtTickTime(38, () -> {
            fixture.assertHit(101, "arrow", "native drawn bow arrow reaches the living actor proxy");
            helper.assertTrue(fixture.hitCount(101) == 1, "one native arrow impact emits one damage receipt");
            helper.assertTrue(fixture.projectileIds("arrow").size() == 1
                    && fixture.states.stream().filter(message -> Proto.bool(message, "alive"))
                        .map(message -> Proto.number(message, "z")).distinct().count() > 2,
                    "actual moving native projectile positions are mirrored rather than a synthetic instant hit");
            beforeBowCancel.addAll(fixture.projectileIds("arrow"));
            fixture.press();
        });
        helper.runAtTickTime(62, () -> {
            fixture.held = false;
            fixture.interaction.cancel(player);
            helper.assertTrue(!player.isUsingItem() && fixture.projectileIds("arrow").equals(beforeBowCancel)
                    && player.getInventory().getItem(1).getCount() == 1 && bow.getDamageValue() == 1,
                    "cancelling a charged bow stops use without firing or consuming ammunition/durability");
            fixture.release();
            fixture.discardTargets();
            fixture.target(102, 7);
            player.setItemSlot(EquipmentSlot.MAINHAND, crossbow);
            fixture.press();
        });
        helper.runAtTickTime(96, () -> {
            helper.assertTrue(CrossbowItem.isCharged(crossbow) && player.getInventory().getItem(1).isEmpty(),
                    "native held crossbow loads and consumes the last real arrow");
            var state = Proto.msg(SID, 0, "player_state");
            WeaponRuntime.addPlayerState(state, player);
            helper.assertTrue(Proto.string(state, "weaponKind").equals("crossbow")
                    && Proto.bool(state, "crossbowLoaded") && !Proto.bool(state, "crossbowFirework"),
                    "loaded crossbow appearance is derived from its charged-projectiles component");
            fixture.release();
            fixture.press();
            fixture.release();
            helper.assertTrue(!CrossbowItem.isCharged(crossbow) && crossbow.getDamageValue() == 1,
                    "native loaded crossbow fires its stored arrow and applies durability exactly once");
        });
        helper.runAtTickTime(112, () -> {
            fixture.assertHit(102, "arrow", "native crossbow arrow independently reaches its proxy");
            helper.assertTrue(fixture.hitCount(102) == 1, "one crossbow impact emits one authoritative receipt");
            fixture.discardTargets();
            fixture.target(103, 7);
            var trident = new ItemStack(Items.TRIDENT);
            trident.setDamageValue(12);
            trident.set(DataComponents.CUSTOM_NAME, Component.literal("Original native trident"));
            trident.enchant(helper.getLevel().registryAccess().lookupOrThrow(Registries.ENCHANTMENT)
                    .getOrThrow(Enchantments.IMPALING), 2);
            player.setItemSlot(EquipmentSlot.MAINHAND, trident);
            fixture.press();
        });
        helper.runAtTickTime(128, () -> {
            helper.assertTrue(player.getTicksUsingItem() >= 10, "native trident charge satisfies its throw threshold");
            fixture.release();
            helper.assertTrue(player.getMainHandItem().isEmpty(), "Survival throw removes the genuine trident from its hand");
            thrown[0] = helper.getLevel().getEntitiesOfClass(ThrownTrident.class, fixture.bounds).stream()
                    .filter(projectile -> projectile.getOwner() == player).findFirst().orElseThrow();
            originalPickup[0] = thrown[0].getPickupItemStackOrigin();
            helper.assertTrue(originalPickup[0].is(Items.TRIDENT) && originalPickup[0].getDamageValue() == 13
                    && originalPickup[0].isEnchanted()
                    && originalPickup[0].get(DataComponents.CUSTOM_NAME).getString().equals("Original native trident"),
                    "native thrown entity owns the original named/enchantable stack with updated durability");
        });
        helper.runAtTickTime(145, () -> {
            fixture.assertHit(103, "trident", "native thrown trident damage is captured without melee input");
            helper.assertTrue(fixture.hitCount(103) == 1, "one trident cannot damage the proxy twice during flight");
            fixture.discardTargets();
            fixture.target(104, 2.6);
            fixture.target(105, 3.8);
            player.setItemSlot(EquipmentSlot.MAINHAND, new ItemStack(Items.DIAMOND_SPEAR));
        });
        helper.runAtTickTime(170, () -> {
            var stack = player.getMainHandItem();
            helper.assertTrue(WeaponRuntime.kind(stack).equals("spear")
                    && stack.has(DataComponents.PIERCING_WEAPON) && stack.has(DataComponents.KINETIC_WEAPON),
                    "diamond spear retains vanilla piercing and kinetic components");
            helper.assertTrue(!player.cannotAttackWithItem(stack, 5), "native spear cooldown has elapsed before the jab");
            stack.get(DataComponents.PIERCING_WEAPON).attack(player, EquipmentSlot.MAINHAND);
            fixture.assertHit(104, "spear", "native piercing jab hits first proxy along its actual attack range");
            fixture.assertHit(105, "spear", "native piercing jab reaches the second proxy in the same stab");
            jabDamage[0] = fixture.hit(104).get("damage").getAsFloat();
            helper.assertTrue(player.cannotAttackWithItem(stack, 5), "native piercing attack resets the minimum attack charge");
            helper.assertTrue(fixture.hitCount(104) == 1 && fixture.hitCount(105) == 1,
                    "multi-target jab preserves one receipt per target instead of collapsing to nearest melee target");
        });
        helper.runAtTickTime(178, () -> {
            fixture.discardTargets();
            fixture.target(106, 3);
            fixture.press();
        });
        helper.runAtTickTime(195, () -> {
            helper.assertTrue(player.isUsingItem() && fixture.hitCount(106) == 0,
                    "holding a stationary spear does not invent kinetic charge damage");
            fixture.hostMovement = new Vec3(0, 0, .4); // Eight blocks/sec supplied by the host, in native tick units.
        });
        helper.runAtTickTime(211, () -> {
            fixture.assertHit(106, "spear", "vanilla held kinetic spear uses authoritative host movement");
            helper.assertTrue(fixture.hit(106).get("damage").getAsFloat() > jabDamage[0],
                    "native kinetic relative-speed calculation produces more damage than the standing jab");
            fixture.release();
            fixture.hostMovement = Vec3.ZERO;
            fixture.discardTargets();
            fixture.target(107, 7);
            var rocket = new ItemStack(Items.FIREWORK_ROCKET);
            rocket.set(DataComponents.FIREWORKS, new Fireworks(1, List.of(FireworkExplosion.DEFAULT)));
            var loaded = new ItemStack(Items.CROSSBOW);
            loaded.set(DataComponents.CHARGED_PROJECTILES, ChargedProjectiles.ofNonEmpty(List.of(rocket)));
            player.setItemSlot(EquipmentSlot.MAINHAND, loaded);
            var state = Proto.msg(SID, 0, "player_state");
            WeaponRuntime.addPlayerState(state, player);
            helper.assertTrue(Proto.bool(state, "crossbowLoaded") && Proto.bool(state, "crossbowFirework"),
                    "a genuinely stored firework selects the loaded rocket crossbow appearance");
            fixture.press();
            fixture.release();
        });
        helper.runAtTickTime(232, () -> {
            fixture.assertHit(107, "firework", "vanilla crossbow rocket applies native explosion damage to the actor proxy");
            helper.assertTrue(!fixture.projectileIds("firework").isEmpty(), "native crossbow rocket flight is mirrored");
            var cancellationTrident = new ItemStack(Items.TRIDENT);
            cancellationTrident.set(DataComponents.CUSTOM_NAME, Component.literal("Retained mismatch trident"));
            player.setItemSlot(EquipmentSlot.MAINHAND, cancellationTrident);
            beforeTridentClear.addAll(fixture.projectileIds("trident"));
            fixture.press();
        });
        helper.runAtTickTime(250, () -> {
            fixture.held = false;
            fixture.interaction.clear(player);
            helper.assertTrue(!player.isUsingItem() && fixture.projectileIds("trident").equals(beforeTridentClear)
                    && player.getMainHandItem().is(Items.TRIDENT) && player.getMainHandItem().getDamageValue() == 0,
                    "session cleanup stops a charged trident without throwing or debiting its real stack");
            var returnPosition = fixture.origin.add(-40, 0, -40);
            fixture.forceChunks((int)Math.floor(returnPosition.x), (int)Math.floor(returnPosition.x),
                    (int)Math.floor(returnPosition.z), (int)Math.floor(returnPosition.z));
            WeaponRuntime.preservePickupProjectiles(helper.getLevel(), returnPosition);
            helper.assertTrue(!thrown[0].isRemoved() && thrown[0].position().distanceToSqr(returnPosition) < 1e-8
                    && thrown[0].getPickupItemStackOrigin() == originalPickup[0]
                    && originalPickup[0].getDamageValue() == 13 && originalPickup[0].isEnchanted(),
                    "ending ownership preserves the same thrown trident entity and named/enchantable pickup stack");
            fixture.press();
        });
        helper.runAtTickTime(266, () -> {
            fixture.release();
            retained[0] = helper.getLevel().getEntitiesOfClass(ThrownTrident.class, fixture.bounds).stream()
                    .filter(projectile -> projectile.getOwner() == player && projectile != thrown[0])
                    .findFirst().orElseThrow();
            helper.assertTrue(retained[0].getPickupItemStackOrigin().getDamageValue() == 1,
                    "the mismatched-return fixture is a real Survival-thrown trident, not a reconstructed item");
        });
        helper.runAtTickTime(280, () -> {
            Vec3 retainedPosition = retained[0].position();
            ItemStack retainedStack = retained[0].getPickupItemStackOrigin();
            var otherWorld = helper.getLevel().getServer().getLevel(Level.NETHER);
            helper.assertTrue(otherWorld != null && otherWorld != helper.getLevel(), "the return-world mismatch is genuine");
            if (thrown[0].isRemoved())
                BridgeRuntime.log("WEAPON_PICKUP_DIAGNOSTIC beforeClear removed=" + thrown[0].getRemovalReason()
                        + " position=" + thrown[0].position() + " pickup=" + thrown[0].getPickupItemStackOrigin()
                        + " originalSame=" + (thrown[0].getPickupItemStackOrigin() == originalPickup[0])
                        + " nativeInventory=" + player.getInventory().getNonEquipmentItems());
            WeaponRuntime.preservePickupProjectiles(otherWorld, fixture.origin.add(-40, 0, -40));
            WeaponRuntime.clear();
            fixture.building.clear();
            helper.assertTrue(!retained[0].isRemoved() && retained[0].position().equals(retainedPosition)
                    && retained[0].getPickupItemStackOrigin() == retainedStack && retainedStack.getDamageValue() == 1
                    && retainedStack.get(DataComponents.CUSTOM_NAME).getString().equals("Retained mismatch trident"),
                    "a mismatched return world retains the same real thrown entity and pickup stack through cleanup");
            if (thrown[0].isRemoved())
                BridgeRuntime.log("WEAPON_PICKUP_DIAGNOSTIC afterClear removed=" + thrown[0].getRemovalReason()
                        + " position=" + thrown[0].position() + " pickup=" + thrown[0].getPickupItemStackOrigin());
            helper.assertTrue(!thrown[0].isRemoved() && thrown[0].getPickupItemStackOrigin() == originalPickup[0],
                    "later session cleanup cannot erase an already preserved native trident");
            fixture.assertForeignHealthy("projectile, piercing, kinetic, and firework paths preserve unrelated living entities");
            helper.assertTrue(fixture.reliable.stream().map(message -> Proto.string(message, "eventId")).distinct().count()
                    == fixture.reliable.size(), "all native weapon receipts have independent retry/dedup IDs");
            thrown[0].discard();
            retained[0].discard();
            fixture.discardTargets();
            fixture.building.begin(player, SID);
            WeaponRuntime.begin(SID, player, message -> fixture.reliable.add(message.deepCopy()),
                    message -> fixture.states.add(message.deepCopy()));
        });
        helper.runAtTickTime(290, () -> {
            fixture.target(108, 3.3);
            LightningBolt bolt = EntityTypes.LIGHTNING_BOLT.create(helper.getLevel(), EntitySpawnReason.TRIGGERED);
            helper.assertTrue(bolt != null, "a real vanilla Channeling lightning entity can be created");
            bolt.setPos(fixture.origin.x, fixture.origin.y, fixture.origin.z + 3.3);
            NativeWorldGuard.enter(helper.getLevel(), bolt.blockPosition());
            try { helper.assertTrue(helper.getLevel().addFreshEntity(bolt), "actual Channeling bolt is added under native ownership"); }
            finally { NativeWorldGuard.exit(); }
            helper.assertTrue(fixture.states.stream().noneMatch(message -> Proto.type(message).equals("lightning_state")
                    && Proto.string(message, "entityId").equals(bolt.getUUID().toString())),
                    "Channeling does not presume the cause before vanilla assigns it after spawning");
            bolt.setCause(player); // SummonEntityEffect's real ordering: addFreshEntity, then setCause.
            helper.assertTrue(WeaponRuntime.ownsLightning(bolt), "Channeling ownership retains the genuine player cause");
            NativeWorldGuard.enterEntity(helper.getLevel(), bolt);
            try { bolt.tick(); }
            finally { NativeWorldGuard.exit(); }
            helper.assertTrue(fixture.states.stream().anyMatch(message -> Proto.type(message).equals("lightning_state")
                    && Proto.string(message, "entityId").equals(bolt.getUUID().toString()) && Proto.bool(message, "alive")
                    && Proto.bool(message, "visible") && Proto.string(message, "seed").matches("[0-9a-f]{16}")
                    && Proto.string(message, "seed").equals(String.format(java.util.Locale.ROOT, "%016x", bolt.seed))),
                    "the first native owned tick lazily exports the real bolt ID, full seed and visible phase");
            fixture.assertHit(108, "trident", "actual native lightning tick captures Channeling damage on the proxy");
            helper.assertTrue(Proto.string(fixture.hit(108), "sourceId").equals(bolt.getUUID().toString()),
                    "Channeling receipt is correlated to the actual native bolt");
            fixture.assertForeignHealthy("Channeling lightning leaves the foreign villager healthy and unowned");
            helper.assertTrue(!fixture.foreign.isOnFire() && !fixture.foreign.isRemoved()
                    && fixture.foreign.getType() == EntityTypes.VILLAGER,
                    "Channeling never ignites or transforms an unrelated native villager");
            bolt.discard();
            fixture.discardTargets();
        });
        helper.runAtTickTime(291, () -> {
            var fireCell = BlockPos.containing(fixture.origin).offset(2, 0, 10);
            var fireBolt = EntityTypes.LIGHTNING_BOLT.create(helper.getLevel(), EntitySpawnReason.TRIGGERED);
            helper.assertTrue(fireBolt != null, "a genuine native bolt creates the later fire-contact fixture");
            fireBolt.setPos(fireCell.getX() + .5, fireCell.getY(), fireCell.getZ() + .5);
            NativeWorldGuard.enter(helper.getLevel(), fireCell);
            try { helper.assertTrue(helper.getLevel().addFreshEntity(fireBolt), "the distant native bolt is genuinely owned"); }
            finally { NativeWorldGuard.exit(); }
            fireBolt.setCause(player);
            NativeWorldGuard.enterEntity(helper.getLevel(), fireBolt);
            try { fireBolt.tick(); }
            finally { NativeWorldGuard.exit(); }
            helper.assertTrue(helper.getLevel().getBlockState(fireCell).is(Blocks.FIRE)
                    && WeaponRuntime.ownsFire(helper.getLevel(), fireCell),
                    "actual native LightningBolt.spawnFire retains fire away from foreign entities and attributes it to the bolt");
            fireBolt.discard();
            foreignFireContactTick[0] = fixture.foreign.tickCount;
            fixture.foreign.teleportTo(fireCell.getX() + .5, fireCell.getY(), fireCell.getZ() + .5);
        });
        helper.runAtTickTime(298, () -> {
            helper.assertTrue(fixture.foreign.tickCount - foreignFireContactTick[0] >= 5,
                    "the foreign living entity really ticks while standing inside existing lightning-created fire");
            fixture.assertForeignHealthy("a foreign creature entering existing owned lightning fire later retains full native health");
            helper.assertTrue(!fixture.foreign.isOnFire()
                    && helper.getLevel().getBlockState(fixture.foreign.blockPosition()).is(Blocks.FIRE),
                    "native fire contact protects the foreign creature without erasing the real fire block");
            fixture.foreign.teleportTo(fixture.origin.x, fixture.origin.y, fixture.origin.z + 3.3);
        });
        helper.runAtTickTime(300, () -> {
            fixture.forceChunks((int)fixture.origin.x - 80, (int)fixture.origin.x + 3,
                    (int)fixture.origin.z - 3, (int)fixture.origin.z + 3);
            var weather = helper.getLevel().getWeatherData();
            weather.setClearWeatherTime(0); weather.setRainTime(1000); weather.setRaining(true);
            helper.getLevel().setRainLevel(1);
            fixture.hostYaw = 90; // Use a clear flight lane perpendicular to the foreign villager/proxy line.
            fixture.applyHostPose();
            // GameTest's void biome does not precipitate even with real weather enabled.
            var rainBiome = helper.getLevel().registryAccess().lookupOrThrow(Registries.BIOME).getOrThrow(Biomes.PLAINS);
            var biomeFill = FillBiomeCommand.fill(helper.getLevel(), player.blockPosition().offset(-2, -2, -2),
                    player.blockPosition().offset(2, 3, 2), rainBiome);
            if (biomeFill.right().isPresent()) helper.assertTrue(biomeFill.right().get().getType() == FillBiomeCommand.ERROR_NO_BIOMES_SET,
                    "real rainfall biome is established without an unloaded-chunk error");
            if (!player.isInWaterOrRain())
                BridgeRuntime.log("WEAPON_RAIN_DIAGNOSTIC pose=" + player.position() + " raining=" + helper.getLevel().isRaining()
                        + " precipitation=" + helper.getLevel().precipitationAt(player.blockPosition())
                        + " sky=" + helper.getLevel().canSeeSky(player.blockPosition().above(2))
                        + " biome=" + helper.getLevel().getBiome(player.blockPosition()).unwrapKey());
            helper.assertTrue(player.isInWaterOrRain(), "native Riptide use is authorized by actual test-world rain in a rainfall biome");
            var riptide = new ItemStack(Items.TRIDENT);
            riptide.enchant(helper.getLevel().registryAccess().lookupOrThrow(Registries.ENCHANTMENT)
                    .getOrThrow(Enchantments.RIPTIDE), 3);
            player.setItemSlot(EquipmentSlot.MAINHAND, riptide);
            fixture.press();
            helper.assertTrue(player.isUsingItem(), "actual wet Riptide trident starts native item use");
        });
        helper.runAtTickTime(299, () -> fixture.assertForeignHealthy(
                "delayed native Channeling fire must not harm a foreign living entity before Riptide starts"));
        helper.runAtTickTime(315, () -> {
            fixture.release();
            helper.assertTrue(player.isAutoSpinAttack() && player.getDeltaMovement().lengthSqr() > 0
                    && player.getMainHandItem().is(Items.TRIDENT) && player.getMainHandItem().getDamageValue() == 1,
                    "native Riptide release starts its spin/velocity and retains the real trident with durability debit");
            WeaponRuntime.tick();
            helper.assertTrue(fixture.states.stream().anyMatch(message -> Proto.type(message).equals("player_motion")
                    && Proto.bool(message, "spinning") && (Proto.number(message, "vx") != 0
                        || Proto.number(message, "vy") != 0 || Proto.number(message, "vz") != 0)),
                    "authoritative Riptide player_motion publishes actual positive native velocity");
        });
        helper.runAtTickTime(340, () -> {
            helper.assertTrue(!player.isAutoSpinAttack() && fixture.states.stream().anyMatch(message ->
                    Proto.type(message).equals("player_motion") && !Proto.bool(message, "spinning")),
                    "real native ticks finish the Riptide spin and publish its end state");
            var spinningTicks = fixture.states.stream().filter(message -> Proto.type(message).equals("player_motion")
                    && Proto.bool(message, "spinning")).map(message -> Proto.integer(message, "nativeTick")).distinct().toList();
            helper.assertTrue(spinningTicks.size() > 1 && spinningTicks.getLast() - spinningTicks.getFirst() <= 20,
                    "Riptide snapshots follow the native twenty-tick lifetime rather than an invented host timer");
            fixture.hostPosition = fixture.origin;
            fixture.hostYaw = 0;
            fixture.applyHostPose();
            fixture.assertForeignHealthy("Riptide/native motion does not harm the unrelated living entity");
        });
        helper.runAtTickTime(350, () -> {
            fixture.forceChunks((int)fixture.origin.x + 11, (int)fixture.origin.x + 13,
                    (int)fixture.origin.z + 11, (int)fixture.origin.z + 13);
            NativeWorldGuard.enter(helper.getLevel(), BlockPos.containing(fixture.origin));
            try {
                for (int index = 0; index < 1024; index++) {
                    var stack = new ItemStack(Items.COBBLESTONE);
                    stack.set(DataComponents.CUSTOM_NAME, Component.literal("Budget fixture " + index));
                    var drop = new ItemEntity(helper.getLevel(), fixture.origin.x + 12,
                            fixture.origin.y + 1, fixture.origin.z + 12, stack);
                    drop.setNoGravity(true);
                    drop.setPickUpDelay(32767);
                    helper.assertTrue(helper.getLevel().addFreshEntity(drop), "actual regular owned-entity capacity fixture added: " + index);
                    fixture.budgetDrops.add(drop);
                }
                helper.assertTrue(!NativeWorldGuard.canSpawnOwnedTnt(helper.getLevel()),
                        "the genuine regular owned-entity budget is full before the weapon consumes its stack");
            } finally { NativeWorldGuard.exit(); }
            var capacityTrident = new ItemStack(Items.TRIDENT);
            capacityTrident.setDamageValue(23);
            capacityTrident.set(DataComponents.CUSTOM_NAME, Component.literal("Capacity native trident"));
            player.setItemSlot(EquipmentSlot.MAINHAND, capacityTrident);
            fixture.press();
        });
        helper.runAtTickTime(366, () -> {
            NativeWorldGuard.enter(helper.getLevel(), BlockPos.containing(fixture.origin));
            try { helper.assertTrue(!NativeWorldGuard.canSpawnOwnedTnt(helper.getLevel()),
                    "regular capacity stays full after real charge ticks; original distinct named drops do not merge"); }
            finally { NativeWorldGuard.exit(); }
            fixture.release();
            capacityThrown[0] = helper.getLevel().getEntitiesOfClass(ThrownTrident.class, fixture.bounds).stream()
                    .filter(projectile -> projectile.getOwner() == player).findFirst().orElseThrow();
            capacityPickup[0] = capacityThrown[0].getPickupItemStackOrigin();
            helper.assertTrue(player.getMainHandItem().isEmpty() && WeaponRuntime.ownsProjectile(capacityThrown[0])
                    && NativeWorldGuard.ownsEntity(capacityThrown[0]) && capacityPickup[0].getDamageValue() == 24
                    && capacityPickup[0].get(DataComponents.CUSTOM_NAME).getString().equals("Capacity native trident"),
                    "a genuine named Survival-thrown weapon is tracked despite a full ordinary entity budget");
        });
        helper.runAtTickTime(380, () -> {
            var returnPosition = fixture.origin.add(-40, 0, -40);
            WeaponRuntime.preservePickupProjectiles(helper.getLevel(), returnPosition);
            WeaponRuntime.clear();
            fixture.building.clear();
            helper.assertTrue(!capacityThrown[0].isRemoved() && capacityThrown[0].position().distanceToSqr(returnPosition) < 1e-8
                    && capacityThrown[0].getPickupItemStackOrigin() == capacityPickup[0] && capacityPickup[0].getDamageValue() == 24,
                    "full regular capacity cannot erase or reconstruct the genuine thrown weapon during final cleanup");
            fixture.assertForeignHealthy("foreign living entity remains untouched after every native weapon path");
            fixture.finished = true;
            BridgeRuntime.log("NATIVE_WEAPONS_PASS: bow, crossbow arrow/rocket, named enchanted trident, spear jab/kinetic, cancellation, original/mismatched pickup preservation, Channeling foreign protection, Riptide native motion, full1024regular capacity; hits="
                    + fixture.reliable.size());
            capacityThrown[0].discard();
            helper.succeed();
        });
    }

    private static final class Fixture {
        final GameTestHelper helper;
        final ServerPlayer player;
        final BuildingRuntime building = new BuildingRuntime();
        final NativeInteractionRuntime interaction = new NativeInteractionRuntime(building);
        final ArrayList<JsonObject> reliable = new ArrayList<>(), states = new ArrayList<>();
        final ArrayList<BridgeTargetEntity> targets = new ArrayList<>();
        final ArrayList<ItemEntity> budgetDrops = new ArrayList<>();
        final Villager foreign;
        final float foreignHealth;
        final Vec3 origin;
        final AABB bounds;
        final Set<Long> forcedChunks = new HashSet<>();
        final WeatherData originalWeather;
        final float originalRainLevel, originalThunderLevel;
        Vec3 hostPosition;
        float hostYaw;
        Vec3 hostMovement = Vec3.ZERO;
        boolean held, finished;

        Fixture(GameTestHelper helper) {
            this.helper = helper;
            var base = helper.absolutePos(new BlockPos(4, 4, 4));
            origin = new Vec3(base.getX() + .5, base.getY(), base.getZ() + .5);
            hostPosition = origin;
            bounds = new AABB(base).inflate(32);
            var weather = helper.getLevel().getWeatherData();
            originalWeather = new WeatherData(weather.getClearWeatherTime(), weather.getRainTime(), weather.getThunderTime(),
                    weather.isRaining(), weather.isThundering());
            originalRainLevel = helper.getLevel().getRainLevel(1);
            originalThunderLevel = helper.getLevel().getThunderLevel(1);
            // A shot can cross the test cell's chunk boundary; loaded terrain alone does not keep entities ticking.
            forceChunks(base.getX() - 3, base.getX() + 3, base.getZ() - 3, base.getZ() + 18);
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 14; z++) for (int y = 0; y <= 4; y++)
                helper.getLevel().setBlock(base.offset(x, y, z), Blocks.AIR.defaultBlockState(), Block.UPDATE_ALL);
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 14; z++)
                helper.getLevel().setBlock(base.offset(x, -1, z), Blocks.STONE.defaultBlockState(), Block.UPDATE_ALL);
            player = helper.makeMockServerPlayerInLevel();
            player.setGameMode(GameType.SURVIVAL);
            player.setNoGravity(true);
            player.connection.teleport(origin.x, origin.y, origin.z, 0, 0);
            player.setYHeadRot(0);
            player.setYBodyRot(0);
            player.getInventory().clearContent();
            player.getInventory().setSelectedSlot(0);
            foreign = EntityTypes.VILLAGER.create(helper.getLevel(), EntitySpawnReason.TRIGGERED);
            helper.assertTrue(foreign != null, "unrelated living entity fixture exists");
            foreign.setNoAi(true);
            foreign.setNoGravity(true);
            foreign.setPos(origin.x, origin.y, origin.z + 3.3);
            helper.assertTrue(helper.getLevel().addFreshEntity(foreign), "foreign living entity is added outside native ownership");
            foreignHealth = foreign.getHealth();
            building.begin(player, SID);
            WeaponRuntime.begin(SID, player, message -> reliable.add(message.deepCopy()), message -> states.add(message.deepCopy()));
            helper.onEachTick(() -> {
                if (finished) return;
                applyHostPose();
                player.setKnownMovement(hostMovement);
                if (held) interaction.interact(player, input(true), SID);
                // Embedded GameTest connections do not drive ServerGamePacketListenerImpl.tick/doTick.
                // Run the real native Player/LivingEntity tick; never patch or fabricate item-use timers.
                player.doTick();
                if (player.isAutoSpinAttack()) hostPosition = player.position();
                interaction.tick(player);
                building.tick();
                WeaponRuntime.tick();
            });
            NativeTestCleanup.register(helper, () -> {
                held = false;
                interaction.clear(player);
                WeaponRuntime.clear();
                building.clear();
                discardTargets();
                foreign.discard();
                player.discard();
                for (var drop : budgetDrops) drop.discard();
                var currentWeather = helper.getLevel().getWeatherData();
                currentWeather.setClearWeatherTime(originalWeather.getClearWeatherTime());
                currentWeather.setRainTime(originalWeather.getRainTime());
                currentWeather.setThunderTime(originalWeather.getThunderTime());
                currentWeather.setRaining(originalWeather.isRaining());
                currentWeather.setThundering(originalWeather.isThundering());
                helper.getLevel().setRainLevel(originalRainLevel);
                helper.getLevel().setThunderLevel(originalThunderLevel);
                for (long packed : forcedChunks) {
                    var chunk = net.minecraft.world.level.ChunkPos.unpack(packed);
                    helper.getLevel().setChunkForced(chunk.x(), chunk.z(), false);
                }
            });
        }

        void press() { held = true; interaction.interact(player, input(true), SID); }
        void release() { held = false; interaction.interact(player, input(false), SID); }
        void target(int actorId, double distance) {
            var target = BridgeTargetEntity.spawn(helper.getLevel().getServer(), actorId, origin.x, origin.y, origin.z + distance);
            helper.assertTrue(target != null, "native living actor proxy exists: " + actorId);
            target.setHostSpeed(Vec3.ZERO);
            targets.add(target);
        }
        void discardTargets() { for (var target : targets) target.discard(); targets.clear(); }
        long hitCount(int actorId) { return reliable.stream().filter(message -> Proto.integer(message, "eid") == actorId).count(); }
        JsonObject hit(int actorId) {
            return reliable.stream().filter(message -> Proto.integer(message, "eid") == actorId).findFirst().orElseThrow();
        }
        void assertHit(int actorId, String kind, String description) {
            if (hitCount(actorId) == 0)
                BridgeRuntime.log("WEAPON_FLIGHT_DIAGNOSTIC actor=" + actorId + " kind=" + kind + " shooter=" + player.position()
                        + " look=" + player.getLookAngle() + " yaw=" + player.getYRot() + " pitch=" + player.getXRot()
                        + " headYaw=" + player.getYHeadRot() + " playerTick=" + player.tickCount + " foreignHealth=" + foreign.getHealth()
                        + " targetBounds=" + targets.stream().filter(target -> target.actorId() == actorId)
                            .map(target -> target.getBoundingBox().toString()).toList()
                        + " actualFlight=" + states.stream().filter(message -> Proto.type(message).equals("projectile_state"))
                            .toList());
            helper.assertTrue(hitCount(actorId) > 0, description);
            var hit = hit(actorId);
            helper.assertTrue(Proto.type(hit).equals("weapon_hit") && Proto.string(hit, "kind").equals(kind)
                    && Proto.number(hit, "damage") > 0 && !Proto.string(hit, "sourceId").isBlank(),
                    "native impact publishes positive correlated authoritative damage: " + actorId);
            var target = targets.stream().filter(entity -> entity.actorId() == actorId).findFirst().orElseThrow();
            helper.assertTrue(target.getHealth() == target.getMaxHealth(), "capture-only proxy retains native health after impact");
            assertForeignHealthy("unrelated living entity in the native weapon's flight/range remains healthy: " + kind);
        }
        void assertForeignHealthy(String description) {
            if (!foreign.isAlive() || foreign.getHealth() != foreignHealth || NativeWorldGuard.ownsEntity(foreign))
                BridgeRuntime.log("WEAPON_FOREIGN_DIAGNOSTIC description=" + description + " health=" + foreign.getHealth()
                        + " baseline=" + foreignHealth + " removed=" + foreign.getRemovalReason() + " position=" + foreign.position()
                        + " motion=" + foreign.getDeltaMovement() + " owned=" + NativeWorldGuard.ownsEntity(foreign)
                        + " type=" + foreign.getType() + " onFire=" + foreign.isOnFire()
                        + " feetBlock=" + helper.getLevel().getBlockState(foreign.blockPosition())
                        + " lastDamage=" + foreign.getLastDamageSource());
            helper.assertTrue(foreign.isAlive() && foreign.getHealth() == foreignHealth
                    && !NativeWorldGuard.ownsEntity(foreign), description);
        }
        Set<String> projectileIds(String kind) {
            var result = new HashSet<String>();
            for (var state : states) if (Proto.type(state).equals("projectile_state") && Proto.string(state, "kind").equals(kind))
                result.add(Proto.string(state, "entityId"));
            return Set.copyOf(result);
        }
        void applyHostPose() {
            player.connection.teleport(hostPosition.x, hostPosition.y, hostPosition.z, hostYaw, 0);
            player.setYHeadRot(hostYaw);
            player.setYBodyRot(hostYaw);
            player.fallDistance = 0;
            if (!player.isAutoSpinAttack()) player.setDeltaMovement(Vec3.ZERO);
        }
        void forceChunks(int minX, int maxX, int minZ, int maxZ) {
            for (int cx = minX >> 4; cx <= maxX >> 4; cx++) for (int cz = minZ >> 4; cz <= maxZ >> 4; cz++) {
                if (!forcedChunks.add(net.minecraft.world.level.ChunkPos.pack(cx, cz))) continue;
                helper.getLevel().setChunkForced(cx, cz, true);
                helper.getLevel().getChunkAt(new BlockPos(cx * 16, (int)origin.y, cz * 16));
            }
        }
        private static JsonObject input(boolean pressed) {
            var message = Proto.msg(SID, 1, "interact");
            message.addProperty("requestId", UUID.randomUUID().toString());
            message.addProperty("pressed", pressed);
            message.addProperty("target", false);
            return message;
        }
    }
}

