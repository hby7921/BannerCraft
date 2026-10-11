package dev.mycraft.bridge;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.chunk.LevelChunk;

/** Inspect only loaded air; connection setup must never wait for distant chunk generation. */
final class BridgeStaging {
    record Origin(int x,int z) { }
    static Origin choose(ServerLevel level,double hostX,double hostZ,double groundY,double nearX,double nearZ) {
        if(!Double.isFinite(hostX)||!Double.isFinite(hostZ)||Math.abs(hostX)>100000||Math.abs(hostZ)>100000)
            throw new IllegalArgumentException("host anchor outside staging bounds");
        if(!Double.isFinite(groundY)||!Double.isFinite(nearX)||!Double.isFinite(nearZ)||
                Math.abs(nearX)>29999984||Math.abs(nearZ)>29999984)
            throw new IllegalArgumentException("invalid staging position");
        int bottom=(int)Math.floor(groundY)-4,top=(int)Math.ceil(groundY)+8;
        if(bottom<level.getMinY()||top>=level.getMaxY())throw new IllegalArgumentException("staging height outside world bounds");
        long started=System.nanoTime();int checked=0;
        // Prefer the player's already loaded area. Larger rings only inspect;
        // getChunkNow never requests a chunk or joins a generation future.
        for(int ring=0;ring<=3;ring++)for(int dx=-ring;dx<=ring;dx++)for(int dz=-ring;dz<=ring;dz++) {
            if(Math.max(Math.abs(dx),Math.abs(dz))!=ring)continue;
            int x=(int)Math.floor(nearX)+dx*32,z=(int)Math.floor(nearZ)+dz*32;
            if(Math.abs((long)x)>29999970||Math.abs((long)z)>29999970)continue;
            int ox=x-(int)Math.floor(hostX),oz=z-(int)Math.floor(hostZ);
            // Match staging_origin's existing wire bounds on the host.
            if(Math.abs((long)ox)>131072||Math.abs((long)oz)>131072)continue;
            checked++;
            if(loadedAir(level,x,z,bottom,top)) {
                BridgeRuntime.log("staging origin selected from loaded chunks: offset="+ox+","+oz+
                        " candidates="+checked+" elapsedMs="+(System.nanoTime()-started)/1_000_000);
                return new Origin(ox,oz);
            }
        }
        throw new IllegalArgumentException("no loaded empty staging region; wait for nearby MC chunks to load or use a flat demo world; original terrain was left unchanged");
    }
    static boolean loadedAir(ServerLevel level,int x,int z,int bottom,int top) {
        int minX=(x-12)>>4,maxX=(x+12)>>4,minZ=(z-12)>>4,maxZ=(z+12)>>4;
        LevelChunk[][] chunks=new LevelChunk[maxX-minX+1][maxZ-minZ+1];
        for(int cx=minX;cx<=maxX;cx++)for(int cz=minZ;cz<=maxZ;cz++) {
            var chunk=level.getChunkSource().getChunkNow(cx,cz);
            if(chunk==null)return false;
            chunks[cx-minX][cz-minZ]=chunk;
        }
        var pos=new BlockPos.MutableBlockPos();
        for(int bx=x-12;bx<=x+12;bx++)for(int bz=z-12;bz<=z+12;bz++) {
            var chunk=chunks[(bx>>4)-minX][(bz>>4)-minZ];
            for(int y=bottom;y<=top;y++)if(!chunk.getBlockState(pos.set(bx,y,bz)).isAir())return false;
        }
        return true;
    }
    private BridgeStaging() { }
}
