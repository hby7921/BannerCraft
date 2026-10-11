package dev.mycraft.bridge;

import java.util.function.Function;
import java.util.function.Predicate;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;

final class NearestRayTarget {
    private NearestRayTarget() { }

    static <T> T select(Iterable<T> targets, Vec3 eye, Vec3 end,
                        Function<T, AABB> bounds, Predicate<T> eligible) {
        T nearest = null;
        double nearestDistance = Double.POSITIVE_INFINITY;
        for (T target : targets) {
            if (!eligible.test(target)) continue;
            AABB box = bounds.apply(target);
            var intersection = box.contains(eye) ? java.util.Optional.of(eye) : box.clip(eye, end);
            if (intersection.isEmpty()) continue;
            double distance = eye.distanceToSqr(intersection.get());
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = target;
            }
        }
        return nearest;
    }
}
