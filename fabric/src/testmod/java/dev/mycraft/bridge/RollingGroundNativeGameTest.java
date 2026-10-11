package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import net.fabricmc.fabric.api.gametest.v1.GameTest;
import net.minecraft.core.BlockPos;
import net.minecraft.gametest.framework.GameTestHelper;
import net.minecraft.world.Difficulty;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.boss.wither.WitherBoss;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.entity.projectile.arrow.Arrow;
import net.minecraft.world.entity.vehicle.boat.AbstractBoat;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.LeverBlock;
import net.minecraft.world.level.block.state.properties.AttachFace;
import net.minecraft.world.phys.Vec3;
import java.util.ArrayList;
import java.util.UUID;

/** Genuine temporary terrain rolls while original native boss/TNT/boat/projectile collision stays alive. */
public final class RollingGroundNativeGameTest {
    @GameTest(maxTicks=1200,setupTicks=9000,skyAccess=true,padding=16)
    public void rollingOwnedGroundKeepsNativeCollision(GameTestHelper helper){
        var f=new ProjectileNativeFixture(helper);var level=helper.getLevel();
        Difficulty oldDifficulty=level.getDifficulty();level.getServer().setDifficulty(Difficulty.NORMAL,true);
        f.player.setGameMode(GameType.CREATIVE);
        MobRuntime.begin(ProjectileNativeFixture.SID,f.player,m->f.reliable.add(m.deepCopy()),m->f.states.add(m.deepCopy()));
        BoatRuntime.begin(ProjectileNativeFixture.SID,f.player,m->f.reliable.add(m.deepCopy()),m->f.states.add(m.deepCopy()));
        TntRuntime.begin(ProjectileNativeFixture.SID,f.player,m->f.reliable.add(m.deepCopy()),m->f.states.add(m.deepCopy()));
        BlockPos active=BlockPos.containing(f.origin.add(0,0,8));
        f.building.prepareNativeGround(patch(active,9,.25),active.above());
        WitherBoss boss=EntityTypes.WITHER.create(level,EntitySpawnReason.COMMAND);
        boss.setPos(f.origin.add(0,10,8));boss.makeInvulnerable();
        var boat=EntityTypes.OAK_BOAT.create(level,EntitySpawnReason.TRIGGERED);boat.setPos(f.origin.add(2,.25,5));
        var tnt=new PrimedTnt(level,f.origin.x-2,f.origin.y+.25,f.origin.z+5,f.player);
        var arrow=new Arrow(level,f.origin.x+1,f.origin.y+1,f.origin.z+6,new ItemStack(Items.ARROW),null);arrow.setOwner(f.player);
        for(var entity:java.util.List.of(boss,boat,tnt,arrow)){
            NativeWorldGuard.enter(level,entity.blockPosition());
            try{helper.assertTrue(level.addFreshEntity(entity)&&NativeWorldGuard.ownsEntity(entity),"actual native body is registered for ground retention: "+entity.getType());}
            finally{NativeWorldGuard.exit();}
        }
        // Keep the ownership/attachment lane beyond the actual native birth blast
        // and random skull lane; original Wither destruction stays unchanged.
        BlockPos attachment=BlockPos.containing(f.origin.add(-40,4,18));
        f.building.prepareNativeGround(patch(attachment,1,1),attachment.above());
        f.placeOwned(attachment.above(),Blocks.LEVER.defaultBlockState().setValue(LeverBlock.FACE,AttachFace.FLOOR));
        helper.assertTrue(level.getBlockState(attachment).equals(HostGroundBlock.state(1))
                        &&level.getBlockState(attachment.above()).canSurvive(level,attachment.above()),
                "actual visible floor lever starts on a valid full-height native support");
        BlockPos foreign=BlockPos.containing(f.origin.add(-3,3,18));
        f.building.prepareNativeGround(patch(foreign,1,.25),foreign.above());
        level.setBlock(foreign,Blocks.DIAMOND_BLOCK.defaultBlockState(),net.minecraft.world.level.block.Block.UPDATE_CLIENTS);
        int[] phase={0},samples={0};BlockPos[] last={null};boolean[] attachmentLost={false};
        NativeTestCleanup.register(helper,()->{
            MobRuntime.clear();BoatRuntime.clear();TntRuntime.clear();OwnedEntityTicks.clear();
            boss.discard();boat.discard();tnt.discard();arrow.discard();
            helper.assertTrue(f.building.supportCount()==0&&f.building.groundLeaseCount()==0,"real session cleanup releases all rolling ground records and retention metadata");
            helper.assertTrue(level.getBlockState(foreign).is(Blocks.DIAMOND_BLOCK),"real cleanup preserves independently replaced foreign diamond block");
            level.setBlock(foreign,Blocks.AIR.defaultBlockState(),net.minecraft.world.level.block.Block.UPDATE_CLIENTS);
            level.getServer().setDifficulty(oldDifficulty,true);
        });
        helper.onEachTick(()->{
            if(f.finished)return;
            MobRuntime.tick();BoatRuntime.tick();TntRuntime.tick();OwnedEntityTicks.tick();
            if(!attachmentLost[0]&&!level.getBlockState(attachment.above()).is(Blocks.LEVER)){
                attachmentLost[0]=true;BridgeRuntime.log("ROLLING_ATTACHMENT_FIRST_REMOVED birthRemaining="+boss.getInvulnerableTicks()+" bossTick="+boss.tickCount
                        +" tntFuse="+tnt.getFuse()+" tntRemoved="+tnt.isRemoved()+" bossPosition="+boss.position()+" attachment="+attachment
                        +" support="+level.getBlockState(attachment)+" visible="+f.building.ownsVisible(attachment.above()));
            }
            helper.assertTrue(f.building.supportCount()<=4096&&f.building.groundLeaseCount()==f.building.supportCount(),"world supports and lease metadata stay at the original hard4096 bound");
            if(phase[0]==0&&boss.tickCount>=30&&tnt.tickCount>=20&&boat.tickCount>=20&&arrow.tickCount>=20){
                helper.assertTrue(!tnt.isRemoved()&&tnt.getFuse()>0&&BoatRuntime.owns(boat)&&WeaponRuntime.ownsProjectile(arrow),
                        "retention test starts with genuinely ticking original TNT, boat and arrow bodies");
                BlockPos moving=BlockPos.containing(f.origin.add(32,0,8));
                for(int layer=0;layer<80;layer++){
                    BlockPos at=moving.above(layer%24);
                    f.building.prepareNativeGround(patch(at,9,.25),at.above());samples[0]+=81;last[0]=at;
                    helper.assertTrue(f.building.supportCount()<512&&f.building.groundLeaseCount()==f.building.supportCount(),
                            "native rolling heights retire obsolete actual host-ground blocks while real active floors remain bounded");
                }
                helper.assertTrue(samples[0]>4096&&level.getBlockState(last[0]).equals(HostGroundBlock.state(.25)),
                        "6480 genuine accepted world samples retain the final real quarter-height collision surface");
                helper.assertTrue(level.getBlockState(attachment).equals(HostGroundBlock.state(1))
                        &&level.getBlockState(attachment.above()).is(Blocks.LEVER)&&f.building.ownsVisible(attachment.above()),
                        "native rolling collection retains visible lever and its actual required support");
                helper.assertTrue(level.getBlockState(foreign).is(Blocks.DIAMOND_BLOCK)&&!f.building.ownsNative(foreign),
                        "rolling collection relinquishes externally replaced support metadata without removing foreign world terrain");
                int countBefore=f.building.supportCount();
                JsonObject invalid=patch(moving,1,.25);invalid.addProperty("ground0Y",moving.getY()+18);
                boolean rejected=false;try{f.building.prepareNativeGround(invalid,moving);}catch(IllegalArgumentException expected){rejected=expected.getMessage().contains("outside bounded TNT terrain patch");}
                helper.assertTrue(rejected&&f.building.supportCount()==countBefore,"original out-of-range ground packets remain rejected before rolling mutations");
                helper.assertTrue(boss.isAlive()&&MobRuntime.owns(boss)&&!tnt.isRemoved()&&!boat.isRemoved()&&!arrow.isRemoved(),
                        "real original active collision bodies survive beyond4096 cumulative sampled supports");
                BridgeRuntime.log("ROLLING_GROUND_NATIVE_SAMPLE_PASS accepted="+samples[0]+" current="+f.building.supportCount()+" nativeBossTick="+boss.tickCount);
                phase[0]=1;
            }else if(phase[0]==1&&boss.getInvulnerableTicks()==0&&boss.tickCount>=250){
                helper.assertTrue(boss.isAlive()&&MobRuntime.owns(boss)&&boss.getHealth()==300,
                        "actual naturally falling Wither retains original birth healing and live native physics after rolling ground collection");
                BridgeRuntime.log("ROLLING_ATTACHMENT_AFTER_BIRTH support="+level.getBlockState(attachment)+" visible="+f.building.ownsVisible(attachment.above())
                        +" actualBlock="+level.getBlockState(attachment.above())+" boss="+boss.position()+" nativeOwned="+f.building.ownsNative(attachment));
                helper.assertTrue(level.getBlockState(attachment).equals(HostGroundBlock.state(1))&&f.building.ownsVisible(attachment.above()),
                        "permanent visible attachment retains native support after transient sampling leases expire");
                helper.assertTrue(f.building.supportCount()<512&&f.building.groundLeaseCount()==f.building.supportCount(),
                        "continued original boss ticks keep rolling metadata bounded after native TNT explosion and native leases expire");
                BridgeRuntime.log("ROLLING_GROUND_NATIVE_PASS accepted="+samples[0]+" current="+f.building.supportCount()+" bossY="+boss.getY()+" hp="+boss.getHealth());helper.succeed();
            }
        });
    }
    private static JsonObject patch(BlockPos center,int width,double height){
        JsonObject m=Proto.msg(ProjectileNativeFixture.SID,1,"mob_ground");
        int radius=width/2,index=0;
        for(int x=-radius;x<=radius;x++)for(int z=-radius;z<=radius;z++){
            String p="ground"+index++;m.addProperty(p+"X",center.getX()+x);m.addProperty(p+"Y",center.getY());m.addProperty(p+"Z",center.getZ()+z);m.addProperty(p+"H",height);
        }
        m.addProperty("groundCount",index);return m;
    }
}
