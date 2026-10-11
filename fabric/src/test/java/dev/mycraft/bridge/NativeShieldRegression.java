package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.Holder;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageType;
import net.minecraft.world.item.component.BlocksAttacks;
import net.minecraft.world.phys.Vec3;

import java.util.List;
import java.util.Optional;

/** Tests the MC26.3 component itself; no copied shield reduction or fabricated server player. */
public final class NativeShieldRegression {
    private static int assertions;
    private static void check(boolean condition, String description) {
        assertions++;
        if (!condition) throw new AssertionError(description);
    }
    private static void fails(Runnable action, String description) {
        try { action.run(); }
        catch (IllegalArgumentException expected) { assertions++; return; }
        throw new AssertionError(description);
    }
    private static JsonObject hit() {
        JsonObject result = Proto.msg("session", 1, "player_hurt");
        result.addProperty("x", 1.25); result.addProperty("y", -59.0); result.addProperty("z", 3.5);
        result.addProperty("yaw", -90.0); result.addProperty("pitch", 23.0);
        result.addProperty("hasSourcePosition", true);
        result.addProperty("sourceX", 5.75); result.addProperty("sourceY", -57.5); result.addProperty("sourceZ", 3.5);
        return result;
    }

    public static void main(String[] args) {
        // DamageType/BlocksAttacks codecs initialize the native sound/item registries.
        // Bootstrap only this isolated test JVM; no game client, server, or world is launched.
        net.minecraft.SharedConstants.tryDetectVersion();
        net.minecraft.server.Bootstrap.bootStrap();
        JsonObject message = hit();
        ShieldHurtContext context = ShieldHurtContext.read(message);
        check(context.pose().orElseThrow().x() == 1.25 && context.pose().orElseThrow().yaw() == -90
                && context.pose().orElseThrow().pitch() == 23, "complete victim pose remains exact");
        check(context.sourcePosition().orElseThrow().equals(new Vec3(5.75, -57.5, 3.5)), "source position remains exact");
        message.addProperty("sourceX", 9);
        message.addProperty("yaw", 45);
        check(context.sourcePosition().orElseThrow().x == 5.75 && context.pose().orElseThrow().yaw() == -90,
                "hit snapshot is detached from later input or queued pose mutation");
        check(ShieldHurtContext.read(Proto.msg("session", 2, "player_hurt")).pose().isEmpty(), "legacy hit may omit pose");
        check(ShieldHurtContext.read(Proto.msg("session", 2, "player_hurt")).sourcePosition().isEmpty(),
                "unknown source never fabricates a victim-facing position");
        for (String key : List.of("x", "y", "z", "yaw", "pitch")) {
            JsonObject partial = hit(); partial.remove(key);
            fails(() -> ShieldHurtContext.read(partial), "partial victim pose rejected: " + key);
        }
        for (String key : List.of("sourceX", "sourceY", "sourceZ")) {
            JsonObject partial = hit(); partial.remove(key);
            fails(() -> ShieldHurtContext.read(partial), "partial source position rejected: " + key);
        }
        for (String key : List.of("x", "y", "z", "yaw", "pitch", "sourceX", "sourceY", "sourceZ")) {
            JsonObject invalid = hit(); invalid.addProperty(key, Double.NaN);
            fails(() -> ShieldHurtContext.read(invalid), "NaN rejected: " + key);
        }
        for (String key : List.of("x", "z", "sourceX", "sourceZ")) {
            JsonObject invalid = hit(); invalid.addProperty(key, 30_000_000);
            fails(() -> ShieldHurtContext.read(invalid), "world border rejected: " + key);
        }
        for (String key : List.of("y", "sourceY")) {
            JsonObject invalid = hit(); invalid.addProperty(key, -2049);
            fails(() -> ShieldHurtContext.read(invalid), "bridge height rejected: " + key);
        }
        JsonObject invalidYaw = hit(); invalidYaw.addProperty("yaw", Double.MAX_VALUE);
        fails(() -> ShieldHurtContext.read(invalidYaw), "finite double cannot overflow native float yaw");
        JsonObject invalidPitch = hit(); invalidPitch.addProperty("pitch", -90.01);
        fails(() -> ShieldHurtContext.read(invalidPitch), "pitch range rejected");
        JsonObject unmarkedSource = hit(); unmarkedSource.remove("hasSourcePosition");
        fails(() -> ShieldHurtContext.read(unmarkedSource), "source coordinates require an explicit presence flag");
        JsonObject falseSource = hit(); falseSource.addProperty("hasSourcePosition", false);
        fails(() -> ShieldHurtContext.read(falseSource), "false source flag cannot hide trailing coordinates");
        JsonObject stringFlag = hit(); stringFlag.addProperty("hasSourcePosition", "true");
        fails(() -> ShieldHurtContext.read(stringFlag), "source presence flag is a strict boolean");
        JsonObject noSource = Proto.msg("session", 2, "player_hurt"); noSource.addProperty("hasSourcePosition", false);
        check(ShieldHurtContext.read(noSource).sourcePosition().isEmpty(), "explicitly unknown source stays unknown");

        DamageSource base = new DamageSource(Holder.direct(new DamageType("mycraft-regression", 0.1f)));
        DamageSource positioned = context.withSourcePosition(base);
        check(positioned.typeHolder() == base.typeHolder() && positioned.getEntity() == base.getEntity()
                && positioned.getDirectEntity() == base.getDirectEntity(), "source wrapper preserves damage type and attribution");
        check(positioned.getSourcePosition().equals(context.sourcePosition().orElseThrow())
                && positioned.sourcePositionRaw().equals(context.sourcePosition().orElseThrow()), "native blocking and damage packet see the same source snapshot");
        check(ShieldHurtContext.read(noSource).withSourcePosition(base) == base, "legacy source is preserved without synthetic direction");
        DamageSource legacyPositioned = new DamageSource(base.typeHolder(), new Vec3(-2, 3, -4));
        check(ShieldHurtContext.read(noSource).withSourcePosition(legacyPositioned) == legacyPositioned,
                "legacy real source direction is retained when the optional snapshot is absent");
        fails(() -> ShieldHurtContext.withSourcePosition(base, new Vec3(Double.POSITIVE_INFINITY, 0, 0)),
                "factory rejects nonfinite source vector");

        BlocksAttacks.DamageReduction reduction = new BlocksAttacks.DamageReduction(90, Optional.empty(), 0, 1);
        check(reduction.resolve(base, 8, 0) == 8, "native shield blocks all damage from front");
        check(reduction.resolve(base, 8, Math.PI) == 0, "native shield leaves rear damage intact");
        check(reduction.resolve(base, 8, Math.toRadians(89)) == 8
                && reduction.resolve(base, 8, Math.toRadians(91)) == 0, "native 90 degree cone boundary enforced");
        BlocksAttacks shield = new BlocksAttacks(.25f, 1, List.of(reduction),
                BlocksAttacks.ItemDamageFunction.DEFAULT, Optional.empty(), Optional.empty(), Optional.empty());
        check(shield.blockDelayTicks() == 5, "native quarter-second shield delay is exactly five game ticks");
        check(shield.resolveBlockedDamage(base, 8, 0) == 8 && shield.resolveBlockedDamage(base, 8, Math.PI) == 0,
                "native component resolves front/back without applying durability twice");
        Vec3 victim = new Vec3(10, 64, 20);
        double front = ShieldHurtContext.horizontalAngle(victim, 0, new Vec3(10, 68, 23));
        double back = ShieldHurtContext.horizontalAngle(victim, 0, new Vec3(10, 60, 17));
        check(shield.resolveBlockedDamage(positioned, 8, front) == 8
                && shield.resolveBlockedDamage(positioned, 8, back) == 0, "world coordinates and head yaw reach native reduction with vertical distance ignored");
        check(shield.resolveBlockedDamage(base, 8, ShieldHurtContext.horizontalAngle(victim, 0, null)) == 0,
                "missing source uses vanilla's unblocked angle");
        check(shield.resolveBlockedDamage(base, 8, ShieldHurtContext.horizontalAngle(victim, -90, new Vec3(13, 64, 20))) == 8,
                "hurt snapshot head yaw immediately changes the blockable front");

        var world = net.minecraft.world.level.EmptyBlockGetter.INSTANCE;
        var groundPosition = new net.minecraft.core.BlockPos(0, 128, 0);
        var quantizedFeet = new net.minecraft.world.phys.AABB(.2, 128.7, .2, .8, 130.5, .8);
        var stone = net.minecraft.world.level.block.Blocks.STONE.defaultBlockState();
        var dirt = net.minecraft.world.level.block.Blocks.DIRT.defaultBlockState();
        check(BridgePoseSpace.isForeignCollision(world, groundPosition, stone, null, null, quantizedFeet),
                "actual native stone top at 129 overlaps host feet at 128.7 and foreign terrain remains protected");
        check(!BridgePoseSpace.isForeignCollision(world, groundPosition, stone, null, stone, quantizedFeet),
                "registered host ground support does not reject a quantized host pose");
        check(!BridgePoseSpace.isForeignCollision(world, groundPosition, stone, stone, null, quantizedFeet),
                "registered visible session block delegates overlap handling to host physics");
        check(BridgePoseSpace.isForeignCollision(world, groundPosition, stone, null, dirt, quantizedFeet),
                "stale support registration cannot conceal replaced original terrain");
        check(BridgePoseSpace.isForeignCollision(world, groundPosition, stone, dirt, null, quantizedFeet),
                "stale visible registration cannot conceal a foreign replacement");
        check(!BridgePoseSpace.isForeignCollision(world, groundPosition,
                net.minecraft.world.level.block.Blocks.FIRE.defaultBlockState(), null, null, quantizedFeet),
                "actual native fire collision shape never rejects player pose");
        check(!BridgePoseSpace.isForeignCollision(world, groundPosition,
                net.minecraft.world.level.block.Blocks.AIR.defaultBlockState(), null, null, quantizedFeet),
                "actual native air collision shape is empty");
        check(!BridgePoseSpace.isForeignCollision(world, groundPosition, stone, null, null,
                new net.minecraft.world.phys.AABB(.2, 129, .2, .8, 130.8, .8)),
                "touching the top face without penetration is not a foreign collision");
        System.out.println("PASS: native shield " + assertions + " direction/snapshot assertions");
    }
}
