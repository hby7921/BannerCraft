package dev.mycraft.bridge;

import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.item.ItemEntity;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.UUID;

/** Holds original native drop entities when the regular session admission budget is full. */
public final class OwnedMobLoot {
    private static final int LIMIT=128;
    private static final ThreadLocal<Integer> DEPTH=ThreadLocal.withInitial(()->0);
    private static final LinkedHashMap<UUID,ItemEntity> PENDING=new LinkedHashMap<>();
    private OwnedMobLoot() { }
    public static void enter(){DEPTH.set(DEPTH.get()+1);}
    public static void exit(){int depth=DEPTH.get()-1;if(depth<=0)DEPTH.remove();else DEPTH.set(depth);}
    static boolean active(){return DEPTH.get()>0;}
    public static int pendingCount(){return PENDING.size();}
    static void defer(ItemEntity item){
        if(!active())throw new IllegalStateException("Native mob loot scope required");
        if(PENDING.size()>=LIMIT && !PENDING.containsKey(item.getUUID()))
            throw new IllegalStateException("Native mob loot reserve exhausted");
        PENDING.putIfAbsent(item.getUUID(),item);
    }
    public static void tick(){
        for(var item:new ArrayList<>(PENDING.values())){
            if(item.isRemoved()){PENDING.remove(item.getUUID());continue;}
            if(!(item.level() instanceof ServerLevel level) || !NativeWorldGuard.hasOwnedLootCapacity(level))continue;
            NativeWorldGuard.enter(level,item.blockPosition());
            try { if(level.addFreshEntity(item))PENDING.remove(item.getUUID()); }
            finally { NativeWorldGuard.exit(); }
        }
    }
    public static void preserve(ServerLevel returnLevel,Vec3 returnPoint){
        for(var item:new ArrayList<>(PENDING.values())){
            if(item.isRemoved()){PENDING.remove(item.getUUID());continue;}
            if(!(item.level() instanceof ServerLevel level))continue;
            if(level==returnLevel && returnPoint!=null && Double.isFinite(returnPoint.x)&&Double.isFinite(returnPoint.y)&&Double.isFinite(returnPoint.z)){
                level.getChunkAt(net.minecraft.core.BlockPos.containing(returnPoint));
                item.setPos(returnPoint);
            }
            // Reset runs outside the mutation scope. The exact native object,
            // stack components, owner and lifetime survive as ordinary MC loot.
            if(!NativeWorldGuard.active(level) && level.addFreshEntity(item))PENDING.remove(item.getUUID());
        }
    }
}
