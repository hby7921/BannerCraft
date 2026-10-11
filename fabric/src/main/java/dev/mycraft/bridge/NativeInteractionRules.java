package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.util.function.IntFunction;

/** Wire and hand-priority rules that can be checked without bootstrapping the game. */
final class NativeInteractionRules {
    enum Result { PASS, SUCCESS, FAIL }
    record Request(String id, boolean pressed, boolean target, int x, int y, int z,
                   int face, double hitX, double hitY, double hitZ, boolean terrain, boolean canPlace) { }

    static Request read(JsonObject message, boolean mining) {
        String id = BuildingRules.requestId(message);
        boolean pressed = Proto.bool(message, "pressed"), target = Proto.bool(message, "target");
        if (!pressed && target) throw new IllegalArgumentException("released input cannot carry a target");
        if (!target) return new Request(id, pressed, false, 0, 0, 0, 1, 0, 0, 0, false, false);
        int x = BuildingRules.coordinate(message, "x"), y = BuildingRules.coordinate(message, "y"),
                z = BuildingRules.coordinate(message, "z");
        long wireFace = Proto.integer(message, "face");
        if (wireFace > 5) throw new IllegalArgumentException("face must be 0..5");
        int face = (int)wireFace;
        double hx = mining ? x + .5 : Proto.number(message, "hitX"),
                hy = mining ? y + .5 : Proto.number(message, "hitY"),
                hz = mining ? z + .5 : Proto.number(message, "hitZ");
        if (hx < x - .001 || hx > x + 1.001 || hy < y - .001 || hy > y + 1.001
                || hz < z - .001 || hz > z + 1.001)
            throw new IllegalArgumentException("hit point must belong to clicked block");
        return new Request(id, pressed, true, x, y, z, face, hx, hy, hz,
                !mining && Proto.bool(message, "terrain"), !mining && Proto.bool(message, "canPlace"));
    }

    /** MC client order: useOn then use for main hand, then repeat for offhand. */
    static Result dispatch(boolean target, IntFunction<Result> useOn, IntFunction<Result> use) {
        Result rejected = Result.PASS;
        for (int hand = 0; hand < 2; hand++) {
            if (target) {
                Result result = useOn.apply(hand);
                if (result != Result.PASS) return result; // FAIL must not raise the offhand shield.
            }
            Result result = use.apply(hand);
            if (result == Result.SUCCESS) return result;
            // Minecraft.startUseItem continues to the other hand after air-use FAIL.
            // Retain the rejection only for feedback when neither hand succeeds.
            if (result == Result.FAIL) rejected = Result.FAIL;
        }
        return rejected;
    }
}
