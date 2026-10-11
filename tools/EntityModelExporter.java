import com.google.gson.*;
import java.lang.reflect.*;
import java.nio.file.*;
import java.util.*;
import net.minecraft.client.model.geom.*;
import net.minecraft.client.model.geom.builders.*;
import net.minecraft.client.model.monster.skeleton.SkeletonModel;
import net.minecraft.client.model.object.boat.*;
import net.minecraft.client.model.HumanoidModel;
import net.minecraft.client.renderer.entity.state.*;
import net.minecraft.world.entity.*;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.item.component.SwingAnimation;
import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Axis;
import net.minecraft.util.Mth;
import org.joml.Matrix4f;
import org.joml.Quaternionf;

/** Calls the pinned client's actual layer builders; no geometry/UV reconstruction. */
public final class EntityModelExporter {
    private static final Field CUBES, CHILDREN;
    static {
        try { CUBES=ModelPart.class.getDeclaredField("cubes"); CUBES.setAccessible(true);
              CHILDREN=ModelPart.class.getDeclaredField("children"); CHILDREN.setAccessible(true); }
        catch(Exception e) { throw new RuntimeException(e); }
    }
    static JsonArray floats(float... values) { JsonArray a=new JsonArray(); for(float v:values)a.add(v);return a; }
    static void part(JsonArray parts,String path,ModelPart p) throws Exception {
        JsonObject o=new JsonObject();o.addProperty("path",path);
        o.add("pose",floats(p.x,p.y,p.z,p.xRot,p.yRot,p.zRot,p.xScale,p.yScale,p.zScale));
        JsonArray faces=new JsonArray();
        for(Object value:(List<?>)CUBES.get(p)) {
            ModelPart.Cube cube=(ModelPart.Cube)value;
            for(ModelPart.Polygon poly:cube.polygons) {
                JsonArray face=new JsonArray();
                for(ModelPart.Vertex v:poly.vertices())face.add(floats(v.x(),v.y(),v.z(),poly.normal().x(),poly.normal().y(),poly.normal().z(),v.u(),v.v()));
                faces.add(face);
            }
        }
        o.add("faces",faces);parts.add(o);
        Map<String,ModelPart> children=(Map<String,ModelPart>)CHILDREN.get(p);
        for(String name:new TreeSet<>(children.keySet()))part(parts,path+"/"+name,children.get(name));
    }
    static void model(Path out,String name,LayerDefinition layer) throws Exception {
        JsonArray parts=new JsonArray();part(parts,"",layer.bakeRoot());
        Files.writeString(out.resolve(name+".json"),new GsonBuilder().setPrettyPrinting().create().toJson(parts));
    }
    static JsonArray matrix(PoseStack stack) {
        Matrix4f m=stack.last().pose();return floats(m.m00(),m.m10(),m.m20(),m.m30(),m.m01(),m.m11(),m.m21(),m.m31(),m.m02(),m.m12(),m.m22(),m.m32());
    }
    static void frames(JsonObject output,String path,ModelPart p,PoseStack stack) throws Exception {
        stack.pushPose();p.translateAndRotate(stack);output.add(path,matrix(stack));
        Map<String,ModelPart> children=(Map<String,ModelPart>)CHILDREN.get(p);
        for(String name:new TreeSet<>(children.keySet()))frames(output,path+"/"+name,children.get(name),stack);
        stack.popPose();
    }
    static PoseStack skeletonRoot(float bodyYaw,float deathTime) {
        PoseStack stack=new PoseStack();stack.rotateDegrees(Axis.YP,180-bodyYaw);
        if(deathTime>0)stack.rotateDegrees(Axis.ZP,Math.min(1,Mth.sqrt((deathTime-1)/20*1.6f))*90);
        stack.scale(-1,-1,1);stack.translate(0,-1.501f,0);return stack;
    }
    static void poses(Path out) throws Exception {
        JsonArray samples=new JsonArray();
        float[][] inputs={{0,0,0,0,0,0,0},{15,30,2,.5f,0,17,0},{-24,-80,7,.83f,.2f,25,1},{8,75,14,.3f,.5f,57,1},{0,0,0,0,.75f,101,1},{12,-10,3,.65f,.4f,201,0}};
        for(float[] i:inputs) {
            ModelPart root=SkeletonModel.createBodyLayer().apply(MeshTransformer.scaling(1.2f)).bakeRoot();
            SkeletonModel<SkeletonRenderState> m=new SkeletonModel<>(root);
            SkeletonRenderState s=new SkeletonRenderState();s.xRot=i[0];s.yRot=i[1];s.walkAnimationPos=i[2];s.walkAnimationSpeed=i[3];s.swingAnimation=i[4];s.ageInTicks=i[5];s.isAggressive=i[6]>0;
            s.mainArm=HumanoidArm.RIGHT;s.rightArmPose=HumanoidModel.ArmPose.ITEM;s.leftArmPose=HumanoidModel.ArmPose.EMPTY;
            s.currentSwing=new LivingEntity.SwingDescription(InteractionHand.MAIN_HAND,SwingAnimation.DEFAULT,6);
            m.setupAnim(s);
            JsonObject sample=new JsonObject();sample.add("input",floats(i));JsonArray parts=new JsonArray();part(parts,"",root);sample.add("parts",parts);
            JsonObject transforms=new JsonObject();frames(transforms,"",root,skeletonRoot(37,0));sample.add("frames",transforms);samples.add(sample);
        }
        Files.writeString(out.resolve("skeleton-pose-oracle.json"),new GsonBuilder().setPrettyPrinting().create().toJson(samples));
    }
    static void boatPoses(Path out) throws Exception {
        JsonArray samples=new JsonArray();float[][] input={{0,0,0,0,0,0,1,0},{-179,.5f,1.2f,7,9,13,-1,0},{75,6,3,2,17,-27,1,0},{45,4,4,9,9,27,1,1}};
        for(String name:new String[]{"boat","chest_boat","raft","chest_raft"})for(float[] i:input) {
            LayerDefinition layer=switch(name){case "boat"->BoatModel.createBoatModel();case "chest_boat"->BoatModel.createChestBoatModel();case "raft"->RaftModel.createRaftModel();default->RaftModel.createChestRaftModel();};
            ModelPart root=layer.bakeRoot();AbstractBoatModel m=name.contains("raft")?new RaftModel(root):new BoatModel(root);
            BoatRenderState s=new BoatRenderState();s.rowingTimeLeft=i[1];s.rowingTimeRight=i[2];m.setupAnim(s);
            PoseStack stack=new PoseStack();stack.translate(0,.375f,0);stack.rotateDegrees(Axis.YP,180-i[0]);
            if(i[3]>0)stack.rotateDegrees(Axis.XP,Mth.sin(i[3])*i[3]*Math.max(0,i[4])/10*i[6]);
            if(i[7]==0&&!Mth.equal(i[5],0))stack.rotate(new Quaternionf().setAngleAxis(i[5]*.017453292f,1,0,1));
            stack.scale(-1,-1,1);stack.rotateDegrees(Axis.YP,90);
            JsonObject sample=new JsonObject();sample.addProperty("model",name);sample.add("input",floats(i));JsonArray parts=new JsonArray();part(parts,"",root);sample.add("parts",parts);
            JsonObject transforms=new JsonObject();frames(transforms,"",root,stack);sample.add("frames",transforms);samples.add(sample);
        }
        Files.writeString(out.resolve("boat-pose-oracle.json"),new GsonBuilder().setPrettyPrinting().create().toJson(samples));
    }
    public static void main(String[] args) throws Exception {
        Path out=Path.of(args[0]);Files.createDirectories(out);
        model(out,"wither_skeleton",SkeletonModel.createBodyLayer().apply(MeshTransformer.scaling(1.2f)));
        model(out,"boat",BoatModel.createBoatModel());
        model(out,"chest_boat",BoatModel.createChestBoatModel());
        model(out,"raft",RaftModel.createRaftModel());
        model(out,"chest_raft",RaftModel.createChestRaftModel());
        poses(out);
        boatPoses(out);
    }
}
