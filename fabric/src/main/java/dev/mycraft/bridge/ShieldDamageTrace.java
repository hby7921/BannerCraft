package dev.mycraft.bridge;

import net.minecraft.world.entity.LivingEntity;
import java.util.ArrayDeque;

/** Observation only: record the native blocking calculation inside one actual hurtServer call. */
public final class ShieldDamageTrace {
    public record Result(boolean checked,float blocked) { }
    private static final class Capture {
        final LivingEntity victim; boolean checked;float blocked;
        Capture(LivingEntity victim){this.victim=victim;}
    }
    private static final ThreadLocal<ArrayDeque<Capture>> ACTIVE=ThreadLocal.withInitial(ArrayDeque::new);
    static void begin(LivingEntity victim){ACTIVE.get().push(new Capture(victim));}
    public static void record(LivingEntity victim,float blocked) {
        Capture capture=ACTIVE.get().peek();
        if(capture==null || capture.victim!=victim)return;
        capture.checked=true;
        if(Float.isFinite(blocked)&&blocked>0)capture.blocked+=blocked;
    }
    static Result end() {
        Capture capture=ACTIVE.get().pop();
        if(ACTIVE.get().isEmpty())ACTIVE.remove();
        return new Result(capture.checked,capture.blocked);
    }
    private ShieldDamageTrace() { }
}
