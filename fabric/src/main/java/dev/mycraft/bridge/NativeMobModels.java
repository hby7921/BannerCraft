package dev.mycraft.bridge;

import com.mojang.blaze3d.platform.NativeImage;
import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;
import net.minecraft.client.Minecraft;
import net.minecraft.client.model.Model;
import net.minecraft.client.model.geom.ModelPart;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.EntityRenderer;
import net.minecraft.client.renderer.entity.state.EntityRenderState;
import net.minecraft.client.renderer.rendertype.RenderType;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.client.renderer.texture.UvMapping;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.client.resources.model.geometry.ItemQuads;
import net.minecraft.resources.Identifier;
import net.minecraft.world.entity.Entity;
import org.joml.Matrix4f;
import java.io.*;
import java.lang.reflect.*;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.*;

/** Client-thread capture of the actual renderer, including native model/layer/item animation.
 * The server submits only immutable ids. No server entity is rendered from a second thread. */
public final class NativeMobModels {
    private static final int MAX_REQUESTS=64, MAX_PARTS=256, MAX_VERTICES=16384, MAX_TEXTURES=8;
    private static final Map<String,Request> REQUESTS=new LinkedHashMap<>();
    private static final Set<String> ASSETS=new HashSet<>();
    private static final Map<String,Pixels> TEXTURES=new ConcurrentHashMap<>();
    private static final ThreadPoolExecutor IO=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,
        new ArrayBlockingQueue<>(64), r->{var t=new Thread(r,"BannerCraft native entity models");t.setDaemon(true);return t;},new ThreadPoolExecutor.AbortPolicy());
    private static String session="";
    private static int cursor;
    private static int projectileCursor;
    private static long sequence;
    private static int diagnosticCount;
    private static final int CLIENT_UNTRACKED=1, CLIENT_ID_MISMATCH=2, EMPTY_CAPTURE=4, IO_QUEUE_SKIPPED=8, WRITE_READY=16;
    private static Object modelSet;
    // Loading request() on a dedicated server must never initialize client render classes.
    private static final class ClientFields {
        static final Field CUBES=field(ModelPart.class,"cubes"), CHILDREN=field(ModelPart.class,"children"), TYPE_STATE=field(RenderType.class,"state");
        static final Field BINDINGS=field(net.minecraft.client.renderer.rendertype.RenderSetup.class,"textures");
    }
    private static final class Request {
        final String sid,key,uuid; final boolean projectile; volatile int id; volatile long requested,errorAt; long captured;Snapshot lastSnapshot;
        int diagnosticFlags, diagnostics;
        Request(String s,int i,String u,boolean p){sid=s;id=i;uuid=u;projectile=p;key=UUID.randomUUID().toString().replace("-","");requested=System.nanoTime();}
    }
    private record TextureSource(Identifier id,int width,int height) { }
    private record Pixels(int width,int height,byte[] rgba) { }
    private record Part(int material,float[] vertices,float[] matrix,boolean visible,int color) { }
    private record Snapshot(List<TextureSource> textures,List<Part> parts) { }
    private NativeMobModels(){ }

    public static synchronized String request(String sid,int nativeEntityId,String entityUuid) {return request(sid,nativeEntityId,entityUuid,false);}
    public static synchronized String requestProjectile(String sid,int nativeEntityId,String entityUuid) {return request(sid,nativeEntityId,entityUuid,true);}
    public static synchronized void retire(String entityUuid){REQUESTS.remove(entityUuid);}
    private static String request(String sid,int nativeEntityId,String entityUuid,boolean projectile) {
        if(!sid.matches("[a-f0-9]{32}") || !entityUuid.matches("[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}"))throw new IllegalArgumentException("Invalid entity model identity");
        if(!session.equals(sid)){String old=session;session=sid;REQUESTS.clear();ASSETS.clear();TEXTURES.clear();cursor=0;projectileCursor=0;diagnosticCount=0;if(!old.isEmpty())enqueue(()->cleanup(old));}
        Request r=REQUESTS.get(entityUuid);
        if(r==null){
            pruneRequests(System.nanoTime());
            if(projectile&&REQUESTS.values().stream().filter(q->q.projectile).count()>=48)return "";
            if(REQUESTS.size()>=MAX_REQUESTS){if(projectile)return "";String evict=REQUESTS.values().stream().filter(q->q.projectile).map(q->q.uuid).findFirst().orElse(null);if(evict==null)return "";REQUESTS.remove(evict);}
            r=new Request(sid,nativeEntityId,entityUuid,projectile);REQUESTS.put(entityUuid,r);
        }
        r.id=nativeEntityId;r.requested=System.nanoTime();return r.key;
    }
    public static void clientTick(Minecraft client) {
        final List<Request> requests; final String active=BridgeRuntime.getSessionId();
        synchronized(NativeMobModels.class){
            if(!session.isEmpty()&&!session.equals(active)){String old=session;session="";REQUESTS.clear();ASSETS.clear();TEXTURES.clear();enqueue(()->cleanup(old));}
            pruneRequests(System.nanoTime());requests=new ArrayList<>(REQUESTS.values());
        }
        if(client.level==null||requests.isEmpty())return;
        if(IO.getQueue().remainingCapacity()<8){
            for(Request r:requests)diagnose(r,IO_QUEUE_SKIPPED,"io_queue_skipped","queued="+IO.getQueue().size());
            return;
        }
        if(modelSet!=client.getEntityModels()){modelSet=client.getEntityModels();TEXTURES.clear();}
        var mobs=requests.stream().filter(r->!r.projectile).toList();var projectiles=requests.stream().filter(r->r.projectile).toList();var pending=new ArrayList<Request>();
        // Give up to sixteen creatures two batches per 100 ms even under a projectile volley.
        for(int i=0;i<Math.min(8,mobs.size());i++)pending.add(mobs.get(Math.floorMod(cursor++,mobs.size())));
        for(int i=0;i<Math.min(4,projectiles.size());i++)pending.add(projectiles.get(Math.floorMod(projectileCursor++,projectiles.size())));
        long now=System.nanoTime(),start=now;
        for(Request r:pending) {
            if(now-r.captured<100_000_000L||!r.sid.equals(active))continue;
            Entity e=client.level.getEntity(r.id);
            // Native client tracking can lag the authoritative spawn; waiting is normal.
            if(e==null||e.isRemoved()){
                diagnose(r,CLIENT_UNTRACKED,"client_untracked","clientRemoved="+(e!=null&&e.isRemoved())+" clientPaused="+client.isPaused());
                continue;
            }
            if(!e.getUUID().toString().equals(r.uuid)){
                diagnose(r,CLIENT_ID_MISMATCH,"client_id_uuid_mismatch","actualUuid="+e.getUUID()+" type="+e.getType());
                continue;
            }
            r.captured=now;
            try {
                Snapshot snapshot=capture(client,e);
                if(snapshot.parts.isEmpty())diagnose(r,EMPTY_CAPTURE,"empty_capture","type="+e.getType()+" nativePosition="+e.position());
                if(snapshot.parts.isEmpty()&&r.lastSnapshot!=null)snapshot=new Snapshot(r.lastSnapshot.textures,r.lastSnapshot.parts.stream().map(p->new Part(p.material,p.vertices,p.matrix,false,p.color)).toList());
                if(!snapshot.parts.isEmpty()){r.lastSnapshot=snapshot;long serial=++sequence;var resources=client.getResourceManager();final Snapshot ready=snapshot;final String position=e.position().toString();enqueue(r,()->write(r,ready,resources,serial,position));}
            }
            catch(Exception ex){if(now-r.errorAt>5_000_000_000L){r.errorAt=now;BridgeRuntime.log("native entity model unavailable: "+e.getType()+": "+ex.getMessage());}}
            if(System.nanoTime()-start>8_000_000L)break;
        }
    }
    private static void pruneRequests(long now){REQUESTS.values().removeIf(r->now-r.requested>3_000_000_000L);}
    private static void enqueue(Runnable action){enqueue(null,action);}
    private static void enqueue(Request request,Runnable action){
        try{IO.execute(action);}catch(RejectedExecutionException ignored){
            if(request!=null)diagnose(request,IO_QUEUE_SKIPPED,"io_queue_rejected","queued="+IO.getQueue().size());
        }
    }
    /** Bounded primitive-only diagnostics keep dedicated-server request/cleanup client-safe. */
    private static void diagnose(Request request,int flag,String stage,String detail){
        synchronized(NativeMobModels.class){
            if(!request.sid.equals(session)||(request.diagnosticFlags&flag)!=0||request.diagnostics>=4||diagnosticCount>=128)return;
            request.diagnosticFlags|=flag;request.diagnostics++;diagnosticCount++;
        }
        BridgeRuntime.log("Native entity model diagnostic stage="+stage+" sid="+request.sid+" uuid="+request.uuid
                +" nativeId="+request.id+" visualKey="+request.key+" projectile="+request.projectile+" "+detail);
    }

    @SuppressWarnings({"rawtypes","unchecked"})
    private static Snapshot capture(Minecraft client,Entity entity) throws Exception {
        EntityRenderer renderer=client.getEntityRenderDispatcher().getRenderer(entity);
        EntityRenderState state=renderer.createRenderState(entity,1);
        var capture=new Capture();
        var collector=(SubmitNodeCollector)Proxy.newProxyInstance(SubmitNodeCollector.class.getClassLoader(),new Class[]{SubmitNodeCollector.class},capture);
        capture.collector=collector;
        var stack=new PoseStack();var offset=renderer.getRenderOffset(state);stack.translate(offset.x,offset.y,offset.z);
        var camera=new CameraRenderState();camera.pos=client.gameRenderer.mainCamera().position();camera.orientation=client.gameRenderer.mainCamera().rotation();camera.initialized=true;
        renderer.submit(state,stack,collector,camera);
        return new Snapshot(List.copyOf(capture.textures),List.copyOf(capture.parts));
    }
    private static final class Capture implements InvocationHandler {
        final List<TextureSource> textures=new ArrayList<>();final List<Part> parts=new ArrayList<>();int vertices;
        SubmitNodeCollector collector;
        @Override public Object invoke(Object proxy,Method method,Object[] a) throws Exception {
            if(a==null)a=new Object[0];
            switch(method.getName()) {
                case "order": return collector;
                case "toString": return "BannerCraft native model collector";
                case "hashCode": return System.identityHashCode(this);
                case "equals": return proxy==a[0];
                case "submitModel": {
                    Model model=(Model)a[0];Object state=a[1];PoseStack stack=(PoseStack)a[2];
                    TextureSource source;UvMapping uv=null;int color=-1;
                    if(a[3] instanceof Number){var sprite=((net.minecraft.client.resources.model.sprite.SpriteGetter)a[7]).get((net.minecraft.client.resources.model.sprite.SpriteId)a[6]);source=spriteSource(sprite);color=(int)a[5];}
                    else {
                        source=new TextureSource(a[3] instanceof Identifier id?id:texture((RenderType)a[3]),0,0);
                        if(a.length>=9){color=(int)a[6];uv=(UvMapping)a[7];if(uv instanceof net.minecraft.client.renderer.texture.TextureAtlasSprite sprite){source=spriteSource(sprite);uv=null;}}
                    }
                    model.setupAnim(state);modelParts(model.root(),stack,source,uv,color,true);break;
                }
                case "submitModelPart": {
                    var p=(ModelPart)a[0];var stack=(PoseStack)a[1];var type=(RenderType)a[2];UvMapping uv=a.length>5?(UvMapping)a[5]:null;
                    modelParts(p,stack,texture(type),uv,a.length>6?(int)a[6]:-1,true);break;
                }
                case "submitItem": item((PoseStack)a[0],(int[])a[5],(ItemQuads)a[6]);break;
                case "submitBlockModel": {
                    var stack=(PoseStack)a[0];int[] tint=(int[])a[3];
                    for(Object value:(List<?>)a[2]) {
                        var part=(net.minecraft.client.renderer.block.dispatch.BlockStateModelPart)value;
                        for(var direction:net.minecraft.core.Direction.values())for(var quad:part.getQuads(direction))quad(stack,quad,tint);
                        for(var quad:part.getQuads(null))quad(stack,quad,tint);
                    }break;
                }
                case "submitCustomGeometry": {
                    var consumer=new GeometryConsumer();((SubmitNodeCollector.CustomGeometryRenderer)a[2]).render(new PoseStack().last(),consumer);
                    if(!consumer.vertices.isEmpty()){if(consumer.vertices.size()%4!=0)throw new IllegalArgumentException("Unsupported nonquad native custom geometry");float[] face=new float[consumer.vertices.size()*8];for(int i=0;i<consumer.vertices.size();i++)System.arraycopy(consumer.vertices.get(i),0,face,i*8,8);add(material(new TextureSource(texture((RenderType)a[1]),0,0)),face,(PoseStack)a[0],true,consumer.color);}
                    break;
                }
                // Shadows, nametags, leash/debug/particle nodes are not a creature's body geometry.
                default: break;
            }
            return null;
        }
        @SuppressWarnings("unchecked")
        void modelParts(ModelPart p,PoseStack stack,Identifier texture,UvMapping uv,int color,boolean parentVisible) throws Exception {
            modelParts(p,stack,new TextureSource(texture,0,0),uv,color,parentVisible);
        }
        @SuppressWarnings("unchecked")
        void modelParts(ModelPart p,PoseStack stack,TextureSource texture,UvMapping uv,int color,boolean parentVisible) throws Exception {
            stack.pushPose();p.translateAndRotate(stack);boolean visible=parentVisible&&p.visible;
            var faces=new ArrayList<Float>();
            if(!p.skipDraw)for(ModelPart.Cube cube:(List<ModelPart.Cube>)ClientFields.CUBES.get(p))for(var poly:cube.polygons)for(var v:poly.vertices()) {
                float u=uv==null?v.u():uv.getU(v.u()),w=uv==null?v.v():uv.getV(v.v());
                for(float f:new float[]{v.x()/16,v.y()/16,v.z()/16,poly.normal().x(),poly.normal().y(),poly.normal().z(),u,w})faces.add(f);
            }
            if(!faces.isEmpty())add(material(texture),floats(faces),stack,visible,color);
            var children=(Map<String,ModelPart>)ClientFields.CHILDREN.get(p);
            for(String name:new TreeSet<>(children.keySet()))modelParts(children.get(name),stack,texture,uv,color,visible);
            stack.popPose();
        }
        void item(PoseStack stack,int[] tint,ItemQuads quads){for(var q:quads.all())quad(stack,q,tint);}
        void quad(PoseStack stack,BakedQuad q,int[] tints){
            var sprite=q.materialInfo().sprite();var source=spriteSource(sprite);
            float[] face=new float[32];var n=q.direction();
            for(int i=0;i<4;i++){var p=q.position(i);long uv=q.packedUV(i);int j=i*8;face[j]=p.x();face[j+1]=p.y();face[j+2]=p.z();face[j+3]=n.getStepX();face[j+4]=n.getStepY();face[j+5]=n.getStepZ();face[j+6]=(net.minecraft.client.model.geom.builders.UVPair.unpackU(uv)-sprite.getU0())/(sprite.getU1()-sprite.getU0());face[j+7]=(net.minecraft.client.model.geom.builders.UVPair.unpackV(uv)-sprite.getV0())/(sprite.getV1()-sprite.getV0());}
            int tint=q.materialInfo().isTinted()&&q.materialInfo().tintIndex()<tints.length?tints[q.materialInfo().tintIndex()]:-1;
            add(material(source),face,stack,true,tint);
        }
        int material(TextureSource source){int i=textures.indexOf(source);if(i>=0)return i;if(textures.size()>=MAX_TEXTURES)throw new IllegalArgumentException("Entity exceeds original texture budget");textures.add(source);return textures.size()-1;}
        void add(int material,float[] geometry,PoseStack stack,boolean visible,int color){
            vertices+=geometry.length/8;if(vertices>MAX_VERTICES||parts.size()>=MAX_PARTS)throw new IllegalArgumentException("Entity exceeds original geometry budget");
            parts.add(new Part(material,geometry,matrix(stack.last().pose()),visible,color));
        }
    }
    private static TextureSource spriteSource(net.minecraft.client.renderer.texture.TextureAtlasSprite sprite){return new TextureSource(Identifier.fromNamespaceAndPath(sprite.contents().name().getNamespace(),"textures/"+sprite.contents().name().getPath()+".png"),sprite.contents().width(),sprite.contents().height());}
    private static final class GeometryConsumer implements VertexConsumer {
        final List<float[]> vertices=new ArrayList<>();float[] vertex;int color=-1;
        public VertexConsumer addVertex(float x,float y,float z){if(vertices.size()>=MAX_VERTICES)throw new IllegalArgumentException("Native custom vertex budget exceeded");vertex=new float[]{x,y,z,0,0,0,0,0};vertices.add(vertex);return this;}
        public VertexConsumer setColor(int r,int g,int b,int a){color=a<<24|r<<16|g<<8|b;return this;}public VertexConsumer setColor(int c){color=c;return this;}
        public VertexConsumer setUv(float u,float v){vertex[6]=u;vertex[7]=v;return this;}
        public VertexConsumer setUv1(int a,int b){return this;}public VertexConsumer setUv2(int a,int b){return this;}public VertexConsumer setUv3(float a,float b){return this;}public VertexConsumer setLineWidth(float w){return this;}
        public VertexConsumer setNormal(float x,float y,float z){vertex[3]=x;vertex[4]=y;vertex[5]=z;return this;}
    }
    private static Identifier texture(RenderType type) throws Exception {
        var bindings=(Map<?,?>)ClientFields.BINDINGS.get(ClientFields.TYPE_STATE.get(type));Object binding=bindings.get("Sampler0");if(binding==null)throw new IllegalArgumentException("Missing native diffuse texture "+type);
        Method location=binding.getClass().getDeclaredMethod("location");location.setAccessible(true);return (Identifier)location.invoke(binding);
    }
    public static float[] matrix(Matrix4f m){return new float[]{m.m00(),m.m10(),m.m20(),m.m30(),m.m01(),m.m11(),m.m21(),m.m31(),m.m02(),m.m12(),m.m22(),m.m32()};}
    private static float[] floats(List<Float> list){float[] f=new float[list.size()];for(int i=0;i<f.length;i++)f[i]=list.get(i);return f;}
    private static Field field(Class<?> type,String name){try{Field f=type.getDeclaredField(name);f.setAccessible(true);return f;}catch(Exception e){throw new ExceptionInInitializerError(e);}}

    private static void write(Request request,Snapshot snapshot,net.minecraft.server.packs.resources.ResourceManager resources,long serial,String nativePosition) {
        if(!request.sid.equals(BridgeRuntime.getSessionId()))return;
        try {
            var bytes=new ByteArrayOutputStream();try(var out=new DataOutputStream(bytes)) {
                integer(out,0x31414d45);integer(out,snapshot.textures.size());
                for(var texture:snapshot.textures) {
                    String cacheKey=texture.toString();Pixels p=TEXTURES.get(cacheKey);
                    if(p==null){var resource=resources.getResource(texture.id).orElseThrow(()->new IOException("Missing original entity texture "+texture.id));try(var stream=resource.open();var image=NativeImage.read(stream)){
                        int width=texture.width>0?texture.width:image.getWidth(),height=texture.height>0?texture.height:image.getHeight();
                        if(width<1||height<1||width>256||height>256||width>image.getWidth()||height>image.getHeight())throw new IOException("Entity texture exceeds budget");
                        byte[] rgba=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++){int pixel=image.getPixel(x,y),i=(y*width+x)*4;rgba[i]=(byte)(pixel>>>16);rgba[i+1]=(byte)(pixel>>>8);rgba[i+2]=(byte)pixel;rgba[i+3]=(byte)(pixel>>>24);}p=new Pixels(width,height,rgba);
                    }if(TEXTURES.size()<128)TEXTURES.put(cacheKey,p);}
                    integer(out,p.width);integer(out,p.height);out.write(p.rgba);
                }
                integer(out,snapshot.parts.size());for(var part:snapshot.parts){integer(out,part.material);integer(out,part.vertices.length/32);for(float f:part.vertices)scalar(out,f);}
            }
            byte[] asset=bytes.toByteArray();if(asset.length>4*1024*1024)throw new IOException("Entity asset exceeds byte budget");
            String appearance=HexFormat.of().formatHex(Arrays.copyOf(MessageDigest.getInstance("SHA-256").digest(asset),16));
            Path directory=Path.of(System.getenv("LOCALAPPDATA"),"Mycraft","entity-models",request.sid);Files.createDirectories(directory);
            synchronized(NativeMobModels.class){if(!request.sid.equals(session))return;if(!ASSETS.contains(appearance)){if(ASSETS.size()>=256)throw new IOException("Entity appearance budget reached");atomic(directory,appearance,"asset",asset);ASSETS.add(appearance);}}
            bytes.reset();try(var out=new DataOutputStream(bytes)){integer(out,0x31504d45);out.write(appearance.getBytes(java.nio.charset.StandardCharsets.US_ASCII));out.writeLong(Long.reverseBytes(serial));integer(out,snapshot.parts.size());for(var p:snapshot.parts){integer(out,p.visible?1:0);integer(out,p.color);for(float f:p.matrix)scalar(out,f);}}
            if(request.sid.equals(BridgeRuntime.getSessionId())){
                atomic(directory,request.key,"pose",bytes.toByteArray());
                long visible=snapshot.parts.stream().filter(p->p.visible).count();
                long alpha=snapshot.parts.stream().filter(p->(p.color>>>24)!=0).count();
                diagnose(request,WRITE_READY,"write_ready","parts="+snapshot.parts.size()+" visibleParts="+visible
                        +" nonzeroTintAlphaParts="+alpha+" textures="+snapshot.textures.size()+" appearance="+appearance
                        +" serial="+serial+" nativePosition="+nativePosition);
            }
        }catch(Exception e){long now=System.nanoTime();if(now-request.errorAt>5_000_000_000L){request.errorAt=now;BridgeRuntime.log("native entity asset write: "+e.getMessage());}}
    }
    private static void atomic(Path directory,String key,String suffix,byte[] bytes)throws IOException{Path temp=directory.resolve(key+"."+suffix+".tmp"),dest=directory.resolve(key+"."+suffix);Files.write(temp,bytes);try{Files.move(temp,dest,StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING);}catch(AtomicMoveNotSupportedException e){Files.move(temp,dest,StandardCopyOption.REPLACE_EXISTING);}}
    private static void cleanup(String sid){if(!sid.matches("[a-f0-9]{32}"))return;Path directory=Path.of(System.getenv("LOCALAPPDATA"),"Mycraft","entity-models",sid);try(var files=Files.newDirectoryStream(directory)){for(Path p:files)if(p.getFileName().toString().matches("[a-f0-9]{32}\\.(asset|pose)(\\.tmp)?"))Files.deleteIfExists(p);}catch(IOException ignored){ }try{Files.deleteIfExists(directory);}catch(IOException ignored){ }}
    private static void integer(DataOutputStream out,int value)throws IOException{out.writeInt(Integer.reverseBytes(value));}
    private static void scalar(DataOutputStream out,float value)throws IOException{if(!Float.isFinite(value))throw new IOException("Nonfinite native entity value");integer(out,Float.floatToIntBits(value));}
}
