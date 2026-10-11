package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.world.item.Items;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.RenderShape;
import net.minecraft.world.level.block.state.BlockBehaviour;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.StateDefinition;
import net.minecraft.world.level.block.state.properties.IntegerProperty;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Invisible session terrain with an actual native collision surface. No inventory item is registered. */
public final class HostGroundBlock extends Block {
    public static final IntegerProperty HEIGHT = IntegerProperty.create("height", 1, 256);
    public static final int TNT_REPLACE_MAX_HEIGHT = 179;
    private static final VoxelShape[] SHAPES = new VoxelShape[257];
    private static final ResourceKey<Block> KEY = ResourceKey.create(Registries.BLOCK,
            Identifier.fromNamespaceAndPath(MycraftBridge.MOD_ID, "host_ground"));
    private static HostGroundBlock instance;

    private HostGroundBlock(BlockBehaviour.Properties properties) {
        super(properties);
        registerDefaultState(stateDefinition.any().setValue(HEIGHT, 256));
    }
    public static void register() {
        for (int height = 1; height <= 256; height++) SHAPES[height] = Block.box(0, 0, 0, 16, height / 16.0, 16);
        instance = Registry.register(BuiltInRegistries.BLOCK, KEY,
                new HostGroundBlock(BlockBehaviour.Properties.of().setId(KEY).strength(-1, 3_600_000).noLootTable().noOcclusion()));
    }
    public static BlockState state(double height) {
        if (!Double.isFinite(height) || height < 0 || height > 1) throw new IllegalArgumentException("surface height must be 0..1");
        if (instance == null) throw new IllegalStateException("host ground block is not registered");
        return instance.defaultBlockState().setValue(HEIGHT, Math.clamp((int)Math.round(height * 256), 1, 256));
    }
    static boolean isGround(BlockState state) { return state != null && state.getBlock() == instance; }
    static double height(BlockState state) { return isGround(state) ? state.getValue(HEIGHT) / 256.0 : 1; }
    static boolean canSinkTnt(BlockState state) { return isGround(state) && state.getValue(HEIGHT) <= TNT_REPLACE_MAX_HEIGHT; }
    @Override protected void createBlockStateDefinition(StateDefinition.Builder<Block, BlockState> builder) { builder.add(HEIGHT); }
    @Override protected RenderShape getRenderShape(BlockState state) { return RenderShape.INVISIBLE; }
    @Override protected VoxelShape getShape(BlockState state, BlockGetter world, BlockPos pos, CollisionContext context) {
        return SHAPES[state.getValue(HEIGHT)];
    }
    @Override protected VoxelShape getCollisionShape(BlockState state, BlockGetter world, BlockPos pos, CollisionContext context) {
        return SHAPES[state.getValue(HEIGHT)];
    }
    @Override protected boolean canBeReplaced(BlockState state, BlockPlaceContext context) {
        return canSinkTnt(state) && context.getItemInHand().is(Items.TNT) && context.getClickedFace() == Direction.UP
                && NativeWorldGuard.canReplaceHostGround(context.getLevel(), context.getClickedPos(), state);
    }
}
