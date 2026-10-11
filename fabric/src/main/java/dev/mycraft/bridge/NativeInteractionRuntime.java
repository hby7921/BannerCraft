package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.network.protocol.game.ServerboundPlayerActionPacket.Action;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.item.CrossbowItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;

/** Server-thread implementation of the client's native use/mining input, without a build mode. */
final class NativeInteractionRuntime {
    private final BuildingRuntime building;
    private final ItemUseLease use = new ItemUseLease();
    private final ItemUseLease mining = new ItemUseLease();
    private final BoundedIds seen = new BoundedIds(2048);
    private volatile boolean hostUse;
    private boolean inputDown;
    private ItemStack activeStack;
    private int selectedSlot;
    private BlockPos minePos;
    private Direction mineFace;
    private int mineTicks;
    private ItemStack mineStack;
    private static final InteractionHand[] HANDS = { InteractionHand.MAIN_HAND, InteractionHand.OFF_HAND };

    NativeInteractionRuntime(BuildingRuntime building) { this.building = building; }
    boolean ownsUse() { return hostUse; }

    void interact(ServerPlayer player, JsonObject message, String sid) {
        NativeInteractionRules.Request request = NativeInteractionRules.read(message, false);
        boolean newPress = request.pressed() && !inputDown;
        if(request.pressed()!=inputDown) {
            inputDown=request.pressed();
            long sentAt=message.has("inputAt")?Proto.integer(message,"inputAt"):0;
            BridgeRuntime.log("USE "+(inputDown?"press":"release")+" request="+request.id()
                    +" transportMs="+(sentAt>0?System.currentTimeMillis()-sentAt:-1)
                    +" main="+player.getMainHandItem()+" off="+player.getOffhandItem());
        }
        use.update(request.pressed(), System.nanoTime());
        if (!request.pressed()) {
            if (message.has("cancel") && Proto.bool(message,"cancel")) cancel(player);
            else release(player);
            return;
        }
        if (!use.held() || !seen.add(request.id())) return;
        if (player.containerMenu != player.inventoryMenu) { cancel(player); return; }
        if (player.isUsingItem()) return; // Held shield/bow/food retains its native start time.
        if (!newPress && (player.getMainHandItem().is(Items.CROSSBOW) || player.getOffhandItem().is(Items.CROSSBOW))) return;
        abortMining(player);
        selectedSlot = player.getInventory().getSelectedSlot();
        // The host ray is longer than Minecraft's block reach. Vanilla turns such a
        // ray into a MISS, then still tries held-item use (bow draw, crossbow load).
        // Decide before preparing either target or ground, so a MISS cannot mutate them.
        double reach = player.getAttributeValue(Attributes.BLOCK_INTERACTION_RANGE);
        boolean blockTarget = request.target() && player.getEyePosition().distanceToSqr(
                new Vec3(request.hitX(), request.hitY(), request.hitZ())) <= reach * reach;
        BlockPos origin = blockTarget ? new BlockPos(request.x(), request.y(), request.z()) : player.blockPosition();
        try {
            if (blockTarget) building.prepareNativeTarget(request, BuildingRuntime.surfaceHeight(message, "surfaceH"), message.has("surfaceH"));
            if (blockTarget) building.prepareNativeGround(message, origin);
            BlockHitResult hit = blockTarget ? new BlockHitResult(new Vec3(request.hitX(), request.hitY(), request.hitZ()),
                    Direction.from3DDataValue(request.face()), origin, false) : null;
            NativeWorldGuard.enter(player.level(), origin);
            boolean previousPlacement=NativeWorldGuard.setPlacementAllowed(blockTarget && request.canPlace());
            NativeInteractionRules.Result result;
            String[] rejected = {"native use rejected"};
            try {
                // Set the client suppression lease before the server begins use.
                hostUse = true;
                result = NativeInteractionRules.dispatch(blockTarget, index -> {
                    InteractionHand hand = HANDS[index];
                    ItemStack stack = player.getItemInHand(hand);
                    if (!MobRuntime.canUseSpawnEgg(player, stack))
                        throw new IllegalArgumentException("当前凋零骷髅过多；刷怪蛋已保留，请等待现有生物离场");
                    if (!BoatRuntime.canUseBoat(player, stack))
                        throw new IllegalArgumentException("当前船只过多；船物品已保留，请等待现有船只离场");
                    if (player.level().getBlockState(origin).is(net.minecraft.world.level.block.Blocks.TNT)
                            && (stack.is(Items.FLINT_AND_STEEL) || stack.is(Items.FIRE_CHARGE))
                            && !TntRuntime.canPrime(player.level()))
                        throw new IllegalArgumentException("同时燃烧的TNT已达32个；原方块保留，请等待当前引线结束");
                    return classify(player.gameMode.useItemOn(player, player.level(), stack, hand, hit));
                }, index -> {
                    InteractionHand hand = HANDS[index];
                    if (!WeaponRuntime.canLaunch(player.getItemInHand(hand)))
                        throw new IllegalArgumentException("当前投射物过多；请等待箭矢回收或消失，物品与弹药已保留");
                    ItemStack stack = player.getItemInHand(hand);
                    if (!MobRuntime.canUseSpawnEgg(player, stack))
                        throw new IllegalArgumentException("当前凋零骷髅过多；刷怪蛋已保留，请等待现有生物离场");
                    if (!BoatRuntime.canUseBoat(player, stack))
                        throw new IllegalArgumentException("当前船只过多；船物品已保留，请等待现有船只离场");
                    NativeInteractionRules.Result itemResult = classify(player.gameMode.useItem(player, player.level(), stack, hand));
                    if (itemResult == NativeInteractionRules.Result.FAIL && !player.isCreative()
                            && (stack.is(Items.BOW) || stack.is(Items.CROSSBOW) && !CrossbowItem.isCharged(stack))
                            && player.getProjectile(stack).isEmpty())
                        rejected[0] = stack.is(Items.BOW) ? "弓没有可用箭矢；请把箭放入背包或副手"
                                : "弩没有可用弹药；请把箭放入背包，或在副手持烟花火箭";
                    return itemResult;
                });
            } finally { NativeWorldGuard.setPlacementAllowed(previousPlacement);NativeWorldGuard.exit(); }
            hostUse = player.isUsingItem();
            activeStack = hostUse ? player.getUseItem() : null;
            building.flushNativeChanges();
            BridgeRuntime.log("INTERACT request=" + request.id() + " target=" + request.target()
                    + " nativeTarget=" + blockTarget + " using=" + player.isUsingItem()
                    + " pos=" + origin.toShortString() + " terrain=" + request.terrain()
                    + " face=" + request.face() + " canPlace=" + request.canPlace()
                    + " targetState=" + player.level().getBlockState(origin)
                    + (blockTarget ? " adjacentState=" + player.level().getBlockState(origin.relative(Direction.from3DDataValue(request.face()))) : "")
                    + " result=" + result
                    + (result == NativeInteractionRules.Result.FAIL ? " reason=" + rejected[0] : "")
                    + " main=" + player.getMainHandItem() + " off=" + player.getOffhandItem());
            if (result != NativeInteractionRules.Result.PASS)
                BridgeClient.send(Proto.operationResult(sid, "interaction_result", request.id(),
                        result == NativeInteractionRules.Result.SUCCESS,
                        result == NativeInteractionRules.Result.SUCCESS ? "native item used" : rejected[0]));
        } catch (IllegalArgumentException e) {
            hostUse = false;
            BridgeRuntime.log("INTERACT request=" + request.id() + " target=" + request.target()
                    + " nativeTarget=" + blockTarget + " result=FAIL reason=" + e.getMessage());
            BridgeClient.send(Proto.operationResult(sid, "interaction_result", request.id(), false, e.getMessage()));
        }
    }

    private static NativeInteractionRules.Result classify(InteractionResult result) {
        return result instanceof InteractionResult.Success ? NativeInteractionRules.Result.SUCCESS
                : result instanceof InteractionResult.Fail ? NativeInteractionRules.Result.FAIL : NativeInteractionRules.Result.PASS;
    }

    void mine(ServerPlayer player, JsonObject message, String sid) {
        NativeInteractionRules.Request request = NativeInteractionRules.read(message, true);
        mining.update(request.pressed(), System.nanoTime());
        if (!request.pressed() || !request.target()) { abortMining(player); return; }
        if (!mining.held() || player.isUsingItem() || player.containerMenu != player.inventoryMenu) return;
        BlockPos pos = new BlockPos(request.x(), request.y(), request.z());
        if (pos.equals(minePos)) return;
        abortMining(player);
        mining.update(true, System.nanoTime());
        try {
            if (!building.ownsVisible(pos)) throw new IllegalArgumentException("only this session's blocks can be mined");
            building.prepareNativeTarget(request);
            minePos = pos;
            mineFace = Direction.from3DDataValue(request.face());
            mineTicks = 0;
            mineStack = player.getMainHandItem();
            selectedSlot = player.getInventory().getSelectedSlot();
            breakAction(player, Action.START_DESTROY_BLOCK);
            building.flushNativeChanges();
        } catch (IllegalArgumentException e) {
            abortMining(player);
            BridgeClient.send(Proto.operationResult(sid, "interaction_result", request.id(), false, e.getMessage()));
        }
    }

    void tick(ServerPlayer player) {
        long now = System.nanoTime();
        if (use.held()) {
            if (use.expired(now)) { cancel(player); use.clear(); }
            else if (!player.isAlive() || player.containerMenu != player.inventoryMenu
                    || player.getInventory().getSelectedSlot() != selectedSlot
                    || player.isUsingItem() && player.getUseItem() != activeStack) cancel(player);
            else if (!player.isUsingItem()) { hostUse = false; activeStack = null; }
        }
        if (minePos == null) return;
        if (mining.expired(now) || !player.isAlive() || player.containerMenu != player.inventoryMenu
                || player.getInventory().getSelectedSlot() != selectedSlot || player.getMainHandItem() != mineStack
                || !building.ownsVisible(minePos)) { abortMining(player); return; }
        var state = player.level().getBlockState(minePos);
        float progress = state.getDestroyProgress(player, player.level(), minePos);
        if (state.isAir()) { abortMining(player); return; }
        if (progress * ++mineTicks >= 1.0f) {
            breakAction(player, Action.STOP_DESTROY_BLOCK);
            minePos = null;
            mineStack = null;
            mining.clear();
            building.flushNativeChanges();
        }
    }

    private void breakAction(ServerPlayer player, Action action) {
        NativeWorldGuard.enter(player.level(), minePos);
        try { player.gameMode.handleBlockBreakAction(minePos, action, mineFace, player.level().getMaxY(), 0); }
        finally { NativeWorldGuard.exit(); }
    }
    private void abortMining(ServerPlayer player) {
        if (minePos != null) breakAction(player, Action.ABORT_DESTROY_BLOCK);
        minePos = null;
        mineStack = null;
        mining.clear();
    }
    private void release(ServerPlayer player) {
        hostUse = false;
        if (player.isUsingItem()) {
            if(!WeaponRuntime.canLaunch(player.getUseItem())) {
                player.stopUsingItem();activeStack=null;
                BridgeClient.send(Proto.operationResult(BridgeRuntime.getSessionId(),"interaction_result","weapon-capacity",false,
                        "当前投射物过多；本次蓄力已取消，物品与弹药已保留"));
                return;
            }
            NativeWorldGuard.enter(player.level(), player.blockPosition());
            try { player.releaseUsingItem(); }
            finally { NativeWorldGuard.exit(); }
        }
        activeStack = null;
    }
    void cancel(ServerPlayer player) {
        hostUse = false;
        player.stopUsingItem();
        activeStack = null;
        if (use.held()) use.cancelUntilRelease();
        abortMining(player);
    }
    void clear(ServerPlayer player) {
        if (player != null) { player.stopUsingItem(); abortMining(player); }
        use.clear(); mining.clear(); seen.clear(); hostUse = false; activeStack = null; inputDown=false;
    }
}
