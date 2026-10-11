package dev.mycraft.bridge;

import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.gametest.framework.GameTestInfo;
import net.minecraft.gametest.framework.GameTestListener;
import net.minecraft.gametest.framework.GameTestRunner;
import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.Map;

/** Real 26.3 completion callbacks; runBeforeTestEnd only schedules timeout-minus-one. */
final class NativeTestCleanup {
    private static final Field INFO = testInfoField();
    private static final Map<GameTestInfo, Cleanup> PENDING = new IdentityHashMap<>();
    private NativeTestCleanup() { }

    static void register(GameTestHelper helper, Runnable action) {
        GameTestInfo info;
        try { info = (GameTestInfo)INFO.get(helper); }
        catch (ReflectiveOperationException failure) { throw new IllegalStateException("native test completion hook unavailable", failure); }
        Cleanup cleanup = PENDING.get(info);
        if (cleanup == null) {
            cleanup = new Cleanup(info);
            PENDING.put(info, cleanup); info.addListener(cleanup);
        }
        if (cleanup.closed) throw new IllegalStateException("native test cleanup was registered after completion");
        cleanup.actions.add(action);
    }
    private static Field testInfoField() {
        try {
            Field result = GameTestHelper.class.getDeclaredField("testInfo"); result.setAccessible(true); return result;
        } catch (ReflectiveOperationException failure) { throw new ExceptionInInitializerError(failure); }
    }
    private static final class Cleanup implements GameTestListener {
        final GameTestInfo info;
        final ArrayList<Runnable> actions = new ArrayList<>();
        boolean closed;
        Cleanup(GameTestInfo info) { this.info = info; }
        @Override public void testStructureLoaded(GameTestInfo current) { }
        @Override public void testPassed(GameTestInfo current, GameTestRunner runner) { finish(); }
        @Override public void testFailed(GameTestInfo current, GameTestRunner runner) { finish(); }
        @Override public void testAddedForRerun(GameTestInfo previous, GameTestInfo next, GameTestRunner runner) { finish(); }
        private void finish() {
            if (closed) return;
            closed = true; PENDING.remove(info);
            RuntimeException failure = null;
            // Keep the original registration order, including post-teardown loot assertions.
            // Every independent release still runs when another release reports a problem.
            for (Runnable action : actions) try { action.run(); }
            catch (RuntimeException | Error problem) {
                if (failure == null) failure = new IllegalStateException("native test teardown failed: " + info.id(), problem);
                else failure.addSuppressed(problem);
            }
            actions.clear();
            if (failure != null) throw failure;
            BridgeRuntime.log("NATIVE_TEST_TEARDOWN_PASS test=" + info.id());
        }
    }
}

