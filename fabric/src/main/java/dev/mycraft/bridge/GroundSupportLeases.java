package dev.mycraft.bridge;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Pure bounded lifetime policy; saved block identity and every mutation remain at the world owner. */
final class GroundSupportLeases {
    record Cell(int x,int y,int z) { long column(){return ((long)x<<32)^(z&0xffffffffL);} }
    record Region(double minX,double minY,double minZ,double maxX,double maxY,double maxZ) {
        boolean contains(Cell cell){return cell.x>=minX&&cell.x<=maxX&&cell.y>=minY&&cell.y<=maxY&&cell.z>=minZ&&cell.z<=maxZ;}
    }
    private static final class Lease {long sampledUntil,interactionUntil;}
    private final int capacity;
    private final Map<Cell,Lease> leases=new HashMap<>();
    private final Map<Long,Cell> columns=new HashMap<>();
    GroundSupportLeases(int capacity){if(capacity<1)throw new IllegalArgumentException("positive ground capacity required");this.capacity=capacity;}
    void touch(Cell cell,long now,boolean interaction){
        Lease lease=leases.get(cell);
        if(lease==null){if(leases.size()>=capacity)throw new IllegalArgumentException("ground lease budget reached");lease=new Lease();leases.put(cell,lease);}
        if(interaction)lease.interactionUntil=Math.max(lease.interactionUntil,now+100);
        else lease.sampledUntil=Math.max(lease.sampledUntil,now+20);
        columns.put(cell.column(),cell);
    }
    boolean keep(Cell cell,long now,boolean placedSupport,boolean physicalSupport,List<Region> active,Map<Long,Cell> incomingColumns){
        if(placedSupport||physicalSupport)return true;
        Lease lease=leases.get(cell);
        if(lease!=null&&lease.interactionUntil>now)return true;
        Cell latest=incomingColumns.getOrDefault(cell.column(),columns.get(cell.column()));
        // The sender sampled a new height for this column. Keep an old layer only
        // while a real body/visible attachment still physically depends on it.
        if(!cell.equals(latest))return false;
        if(lease!=null&&lease.sampledUntil>now)return true;
        for(Region region:active)if(region.contains(cell))return true;
        return false;
    }
    void forget(Cell cell){leases.remove(cell);columns.remove(cell.column(),cell);}
    int size(){return leases.size();}
    int columnCount(){return columns.size();}
    void clear(){leases.clear();columns.clear();}
}
