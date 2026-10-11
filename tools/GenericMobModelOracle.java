import com.google.gson.*;
import com.mojang.blaze3d.vertex.*;
import com.mojang.math.Axis;
import net.minecraft.client.model.Model;
import net.minecraft.client.model.geom.*;
import net.minecraft.client.model.geom.builders.*;
import net.minecraft.client.model.monster.wither.WitherBossModel;
import net.minecraft.client.model.monster.creeper.CreeperModel;
import net.minecraft.client.model.monster.spider.SpiderModel;
import net.minecraft.client.model.monster.phantom.PhantomModel;
import net.minecraft.client.model.monster.ghast.GhastModel;
import net.minecraft.client.model.monster.slime.SlimeModel;
import net.minecraft.client.model.animal.pig.PigModel;
import net.minecraft.client.model.animal.cow.CowModel;
import net.minecraft.client.model.object.skull.SkullModel;
import net.minecraft.client.model.object.projectile.ArrowModel;
import net.minecraft.client.renderer.entity.state.*;
import net.minecraft.client.renderer.entity.*;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.resources.Identifier;
import dev.mycraft.bridge.NativeMobModels;
import org.joml.Matrix4f;
import java.lang.reflect.*;
import java.nio.file.*;
import java.io.*;
import java.util.*;
import java.util.zip.*;
import javax.imageio.ImageIO;

/** Native 26.3 setupAnim / ModelPart compilation oracle for the production capture pipeline. */
public final class GenericMobModelOracle {
    static JsonArray floats(float[] values){var a=new JsonArray();for(float v:values)a.add(v);return a;}
    static Object value(Object o,String name)throws Exception{var m=o.getClass().getDeclaredMethod(name);m.setAccessible(true);return m.invoke(o);}
    static final class Consumer implements VertexConsumer {
        final List<float[]> output=new ArrayList<>();float[] current;
        public VertexConsumer addVertex(float x,float y,float z){current=new float[]{x,y,z,0,0,0,0,0};output.add(current);return this;}
        public VertexConsumer setColor(int r,int g,int b,int a){return this;}public VertexConsumer setColor(int c){return this;}
        public VertexConsumer setUv(float u,float v){current[6]=u;current[7]=v;return this;}
        public VertexConsumer setUv1(int a,int b){return this;}public VertexConsumer setUv2(int a,int b){return this;}
        public VertexConsumer setUv3(float a,float b){return this;}public VertexConsumer setLineWidth(float w){return this;}
        public VertexConsumer setNormal(float x,float y,float z){current[3]=x;current[4]=y;current[5]=z;return this;}
    }
    static void integer(DataOutputStream o,int n)throws IOException{o.writeInt(Integer.reverseBytes(n));}
    static void scalar(DataOutputStream o,float f)throws IOException{integer(o,Float.floatToIntBits(f));}
    static Object uninitialized(Class<?> type)throws Exception {
        // Isolate renderer math from a GPU/context constructor. No game or production runtime uses Unsafe.
        var f=sun.misc.Unsafe.class.getDeclaredField("theUnsafe");f.setAccessible(true);return ((sun.misc.Unsafe)f.get(null)).allocateInstance(type);
    }
    static void set(Object object,Class<?> owner,String name,Object value)throws Exception{var f=owner.getDeclaredField(name);f.setAccessible(true);f.set(object,value);}
    @SuppressWarnings({"unchecked","rawtypes"})
    static void sample(Path destination,ZipFile archive,JsonArray samples,String name,Model model,Object state,String texture,boolean invulnerable)throws Exception {
        model.setupAnim(state instanceof WitherSkullRenderState skull?skull.modelState:state);
        var stack=new PoseStack();stack.rotateDegrees(Axis.YP,143);if(invulnerable)stack.scale(1.5833334f,1.5833334f,1.5833334f);stack.scale(-1,-1,1);stack.translate(0,-1.501f,0);
        var clazz=Class.forName("dev.mycraft.bridge.NativeMobModels$Capture");var ctor=clazz.getDeclaredConstructor();ctor.setAccessible(true);var capture=ctor.newInstance();
        var submit=clazz.getDeclaredMethod("modelParts",ModelPart.class,PoseStack.class,Identifier.class,net.minecraft.client.renderer.texture.UvMapping.class,int.class,boolean.class);submit.setAccessible(true);
        var nativeConsumer=new Consumer();
        if(state instanceof WitherRenderState||state instanceof WitherSkullRenderState||state instanceof ArrowRenderState) {
            EntityRenderer renderer;
            if(state instanceof WitherRenderState){renderer=(EntityRenderer)uninitialized(WitherBossRenderer.class);set(renderer,LivingEntityRenderer.class,"model",model);set(renderer,LivingEntityRenderer.class,"layers",List.of());}
            else if(state instanceof WitherSkullRenderState){renderer=(EntityRenderer)uninitialized(WitherSkullRenderer.class);set(renderer,WitherSkullRenderer.class,"model",model);}
            else {renderer=(EntityRenderer)uninitialized(TippableArrowRenderer.class);set(renderer,ArrowRenderer.class,"model",model);}
            var collector=(SubmitNodeCollector)Proxy.newProxyInstance(SubmitNodeCollector.class.getClassLoader(),new Class[]{SubmitNodeCollector.class},(proxy,method,a)->{
                Object result=((InvocationHandler)capture).invoke(proxy,method,a);
                if(method.getName().equals("submitModel")){((Model)a[0]).renderToBuffer((PoseStack)a[2],nativeConsumer,0,0,-1);}
                return result;
            });
            set(capture,clazz,"collector",collector);renderer.submit((EntityRenderState)state,new PoseStack(),collector,new CameraRenderState());
            var tf=clazz.getDeclaredField("textures");tf.setAccessible(true);var sources=(List<?>)tf.get(capture);String actual=value(sources.getFirst(),"id").toString();
            if(!actual.equals("minecraft:"+texture))throw new AssertionError("Native actual renderer texture "+actual+" vs "+texture);
        }else {
            submit.invoke(capture,model.root(),stack,Identifier.fromNamespaceAndPath("minecraft",texture),null,-1,true);
            model.renderToBuffer(stack,nativeConsumer,0,0,-1);
        }
        var pfield=clazz.getDeclaredField("parts");pfield.setAccessible(true);var parts=(List<?>)pfield.get(capture);
        // Native render ordering matches ModelPart's stable hierarchy; captured children are sorted,
        // so compare world vertex/UV multisets rather than relying on map iteration order.
        var nativePoints=new ArrayList<String>();for(var v:nativeConsumer.output)nativePoints.add(pointKey(v));
        var capturedPoints=new ArrayList<String>();
        for(var part:parts){float[] vertices=(float[])value(part,"vertices"),matrix=(float[])value(part,"matrix");for(int i=0;i<vertices.length;i+=8){float[] v=new float[8];for(int row=0;row<3;row++)v[row]=matrix[row*4]*vertices[i]+matrix[row*4+1]*vertices[i+1]+matrix[row*4+2]*vertices[i+2]+matrix[row*4+3];v[6]=vertices[i+6];v[7]=vertices[i+7];capturedPoints.add(pointKey(v));}}
        Collections.sort(nativePoints);Collections.sort(capturedPoints);if(!nativePoints.equals(capturedPoints))throw new AssertionError("Native compiled geometry mismatch "+name);
        String assetName=name+".asset",poseName=name+".pose";
        var image=ImageIO.read(archive.getInputStream(archive.getEntry("assets/minecraft/"+texture)));
        try(var out=new DataOutputStream(Files.newOutputStream(destination.resolve(assetName)))) {
            integer(out,0x31414d45);integer(out,1);integer(out,image.getWidth());integer(out,image.getHeight());
            for(int y=0;y<image.getHeight();y++)for(int x=0;x<image.getWidth();x++){int pixel=image.getRGB(x,y);out.writeByte(pixel>>>16);out.writeByte(pixel>>>8);out.writeByte(pixel);out.writeByte(pixel>>>24);}
            integer(out,parts.size());for(var part:parts){float[] verts=(float[])value(part,"vertices");integer(out,0);integer(out,verts.length/32);for(float f:verts)scalar(out,f);}
        }
        try(var out=new DataOutputStream(Files.newOutputStream(destination.resolve(poseName)))) {
            integer(out,0x31504d45);out.write("0123456789abcdef0123456789abcdef".getBytes(java.nio.charset.StandardCharsets.US_ASCII));out.writeLong(Long.reverseBytes(1));integer(out,parts.size());
            for(var part:parts){integer(out,(boolean)value(part,"visible")?1:0);integer(out,(int)value(part,"color"));for(float f:(float[])value(part,"matrix"))scalar(out,f);}
        }
        var sample=new JsonObject();sample.addProperty("name",name);sample.addProperty("partCount",parts.size());sample.addProperty("nativeVertexCount",nativeConsumer.output.size());var expected=new JsonArray();
        // Use the actual native compile result reordered by the captured vertex keys. Each point's
        // values come from renderToBuffer, not a second copy of the capture matrix computation.
        var points=new HashMap<String,ArrayDeque<float[]>>();for(var v:nativeConsumer.output)points.computeIfAbsent(pointKey(v),k->new ArrayDeque<>()).add(v);
        for(var part:parts){float[] verts=(float[])value(part,"vertices"),m=(float[])value(part,"matrix");for(int i=0;i<verts.length;i+=8){float[] v=new float[8];for(int row=0;row<3;row++)v[row]=m[row*4]*verts[i]+m[row*4+1]*verts[i+1]+m[row*4+2]*verts[i+2]+m[row*4+3];v[6]=verts[i+6];v[7]=verts[i+7];expected.add(floats(points.get(pointKey(v)).removeFirst()));}}
        sample.add("nativeVertices",expected);samples.add(sample);
    }
    static String pointKey(float[] v){return Math.round(v[0]*10000)+":"+Math.round(v[1]*10000)+":"+Math.round(v[2]*10000)+":"+Math.round(v[6]*100000)+":"+Math.round(v[7]*100000);}
    public static void main(String[] args)throws Exception {
        Path out=Path.of(args[0]);Files.createDirectories(out);var samples=new JsonArray();
        try(var jar=new ZipFile(args[1])) {
            for(int phase=0;phase<3;phase++){
                var w=new WitherRenderState();w.scale=1;w.ageScale=1;w.bodyRot=37;w.ageInTicks=phase*41;w.xRot=phase*17;w.yRot=phase*-23;w.xHeadRots[0]=phase*32;w.xHeadRots[1]=phase*-21;w.yHeadRots[0]=phase*47;w.yHeadRots[1]=phase*-55;w.invulnerableTicks=phase==2?200:0;
                sample(out,jar,samples,"wither_"+phase,new WitherBossModel(WitherBossModel.createBodyLayer(CubeDeformation.NONE).bakeRoot()),w,"textures/entity/wither/"+(phase==2?"wither_invulnerable":"wither")+".png",phase==2);
                var s=new LivingEntityRenderState();s.walkAnimationPos=phase*7;s.walkAnimationSpeed=.8f;s.xRot=phase*8;s.yRot=phase*-12;s.ageInTicks=phase*41;
                sample(out,jar,samples,"pig_"+phase,new PigModel(PigModel.createBodyLayer(CubeDeformation.NONE).bakeRoot()),s,"textures/entity/pig/pig_temperate.png",false);
                sample(out,jar,samples,"cow_"+phase,new CowModel(CowModel.createBodyLayer().bakeRoot()),s,"textures/entity/cow/cow_temperate.png",false);
                sample(out,jar,samples,"spider_"+phase,new SpiderModel(SpiderModel.createSpiderBodyLayer().bakeRoot()),s,"textures/entity/spider/spider.png",false);
                var c=new CreeperRenderState();c.walkAnimationPos=phase*7;c.walkAnimationSpeed=.8f;c.yRot=phase*13;c.xRot=phase*8;
                sample(out,jar,samples,"creeper_"+phase,new CreeperModel(CreeperModel.createBodyLayer(CubeDeformation.NONE).bakeRoot()),c,"textures/entity/creeper/creeper.png",false);
                var p=new PhantomRenderState();p.ageInTicks=phase*13;
                sample(out,jar,samples,"phantom_"+phase,new PhantomModel(PhantomModel.createBodyLayer().bakeRoot()),p,"textures/entity/phantom/phantom.png",false);
                var g=new GhastRenderState();g.ageInTicks=phase*41;
                sample(out,jar,samples,"ghast_"+phase,new GhastModel(GhastModel.createBodyLayer().bakeRoot()),g,"textures/entity/ghast/ghast.png",false);
            }
            sample(out,jar,samples,"slime",new SlimeModel(SlimeModel.createInnerBodyLayer().bakeRoot()),new EntityRenderState(),"textures/entity/slime/slime.png",false);
            sample(out,jar,samples,"baby_pig",new PigModel(PigModel.createBodyLayer(CubeDeformation.NONE).apply(PigModel.BABY_TRANSFORMER).bakeRoot()),new LivingEntityRenderState(),"textures/entity/pig/pig_temperate_baby.png",false);
            for(int phase=0;phase<2;phase++){
                var skull=new WitherSkullRenderState();skull.isDangerous=phase>0;skull.modelState.yRot=phase*37;skull.modelState.xRot=phase*-24;
                sample(out,jar,samples,"wither_skull_"+phase,new SkullModel(WitherSkullRenderer.createSkullLayer().bakeRoot()),skull,"textures/entity/wither/"+(phase>0?"wither_invulnerable":"wither")+".png",false);
                var arrow=new TippableArrowRenderState();arrow.xRot=phase*37;arrow.yRot=phase*-24;arrow.shake=phase*4;
                sample(out,jar,samples,"arrow_"+phase,new ArrowModel(ArrowModel.createBodyLayer().bakeRoot()),arrow,"textures/entity/projectiles/arrow.png",false);
            }
        }
        Files.writeString(out.resolve("native-geometry.json"),new GsonBuilder().setPrettyPrinting().create().toJson(samples));
        System.out.println("PASS "+samples.size()+" actual model/capture/native compilation geometry samples");
    }
}
