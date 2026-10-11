package dev.mycraft.bridge;

import com.mojang.blaze3d.platform.NativeImage;
import com.mojang.blaze3d.vertex.PoseStack;
import net.minecraft.client.Minecraft;
import net.minecraft.client.model.object.skull.SkullModelBase;
import net.minecraft.client.model.geom.builders.UVPair;
import net.minecraft.client.renderer.block.dispatch.BlockStateModelPart;
import net.minecraft.client.renderer.blockentity.SkullBlockRenderer;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.SkullBlock;
import net.minecraft.world.level.block.WallSkullBlock;
import net.minecraft.world.level.block.AbstractSkullBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.AABB;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;

/** Actual baked Minecraft geometry, transferred locally; never replaces a model with a stone cube. */
public final class NativeBlockModels {
    private record Request(String sid, String key, BlockState state, BlockPos pos,
                           List<AABB> collision, List<AABB> outline) { }
    private record Face(int material, int tint, float[] vertices, float nx, float ny, float nz) { }
    private record TextureSource(Identifier id, int width, int height) { }
    private static final ArrayBlockingQueue<Request> REQUESTS = new ArrayBlockingQueue<>(512);
    private static final Map<String,String> KEYS = new LinkedHashMap<>();
    private static volatile String session = "";
    private static final ThreadPoolExecutor IO = new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,
            new ArrayBlockingQueue<>(64), runnable -> { var thread=new Thread(runnable,"Mycraft block models"); thread.setDaemon(true); return thread; },
            new ThreadPoolExecutor.AbortPolicy());
    private NativeBlockModels() { }

    public static synchronized String request(String sid, ServerLevel level, BlockPos pos, BlockState state) {
        if (!sid.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("Invalid model session");
        if (!session.equals(sid)) { session=sid; KEYS.clear(); REQUESTS.clear(); }
        var collision=List.copyOf(state.getCollisionShape(level,pos).toAabbs());
        var outline=List.copyOf(state.getShape(level,pos).toAabbs());
        if(collision.size()>64 || outline.size()>64) throw new IllegalArgumentException("Block shape exceeds bridge budget");
        String signature=Block.getId(state)+":"+collision+":"+outline;
        String existing=KEYS.get(signature);
        if(existing!=null)return existing;
        if(KEYS.size()>=2048)throw new IllegalArgumentException("Block model budget reached");
        String key=UUID.randomUUID().toString().replace("-","");
        if(!REQUESTS.offer(new Request(sid,key,state,pos.immutable(),collision,outline)))
            throw new IllegalArgumentException("Block model queue full");
        KEYS.put(signature,key);
        return key;
    }

    public static void clientTick(Minecraft client) {
        String sid=BridgeRuntime.getSessionId();
        if(!session.isEmpty() && !session.equals(sid)) {
            final String old;
            synchronized(NativeBlockModels.class){old=session;session="";KEYS.clear();REQUESTS.clear();}
            try {IO.execute(()->cleanup(old));}catch(RejectedExecutionException ignored){ }
        }
        if(client.level==null || IO.getQueue().remainingCapacity()==0)return;
        // Model access is client-thread only; texture decoding and disk writes are worker-thread only.
        for(int i=0;i<2;i++) {
            Request request=REQUESTS.poll();
            if(request==null)return;
            if(!request.sid.equals(BridgeRuntime.getSessionId()))continue;
            try { bake(client,request); }
            catch(Exception e){ failure(request,e); }
        }
    }

    private static void bake(Minecraft client, Request request) {
        if (request.state.getBlock() instanceof AbstractSkullBlock skull
                && skull.getType() == SkullBlock.Types.WITHER_SKELETON) {
            bakeWitherSkull(client, request);
            return;
        }
        var parts=new ArrayList<BlockStateModelPart>();
        client.getModelManager().getBlockStateModelSet().get(request.state)
                .collectParts(RandomSource.create(42),parts);
        var quads=new ArrayList<BakedQuad>();
        for(var part:parts) {
            quads.addAll(part.getQuads(null));
            for(var direction:Direction.values())quads.addAll(part.getQuads(direction));
        }
        if(quads.isEmpty() || quads.size()>2048)
            throw new IllegalArgumentException("Block requires a special renderer: "+request.state);
        var textures=new ArrayList<TextureSource>();
        var faces=new ArrayList<Face>();
        for(var quad:quads) {
            var sprite=quad.materialInfo().sprite();
            var source=new TextureSource(sprite.contents().name(),sprite.contents().width(),sprite.contents().height());
            int material=textures.indexOf(source);
            if(material<0){material=textures.size();textures.add(source);}
            if(textures.size()>64 || source.width>256 || source.height>256)
                throw new IllegalArgumentException("Block textures exceed bridge budget");
            int tint=0xFFFFFFFF;
            if(quad.materialInfo().isTinted()) {
                var tintSource=client.getBlockColors().getTintSource(request.state,quad.materialInfo().tintIndex());
                if(tintSource!=null) tint=0xFF000000 | tintSource.colorInWorld(request.state,client.level,request.pos);
            }
            float[] vertices=new float[20];
            for(int i=0;i<4;i++) {
                var p=quad.position(i); long uv=quad.packedUV(i);
                vertices[i*5]=p.x(); vertices[i*5+1]=p.y(); vertices[i*5+2]=p.z();
                vertices[i*5+3]=(UVPair.unpackU(uv)-sprite.getU0())/(sprite.getU1()-sprite.getU0());
                vertices[i*5+4]=(UVPair.unpackV(uv)-sprite.getV0())/(sprite.getV1()-sprite.getV0());
            }
            var normal=quad.direction();
            faces.add(new Face(material,tint,vertices,normal.getStepX(),normal.getStepY(),normal.getStepZ()));
        }
        writeSnapshot(client, request, textures, faces);
    }

    /** Heads have no baked block quads. Use the native special model and its actual world transform. */
    private static void bakeWitherSkull(Minecraft client, Request request) {
        var model = SkullBlockRenderer.createModel(client.getEntityModels(), SkullBlock.Types.WITHER_SKELETON);
        model.setupAnim(new SkullModelBase.State());
        var stack = new PoseStack();
        stack.mulPose(request.state.hasProperty(WallSkullBlock.FACING)
                ? SkullBlockRenderer.TRANSFORMATIONS.wallTransformation(request.state.getValue(WallSkullBlock.FACING))
                : SkullBlockRenderer.TRANSFORMATIONS.freeTransformations(request.state.getValue(SkullBlock.ROTATION)));
        var faces = new ArrayList<Face>();
        model.root().visit(stack, (pose, path, index, cube) -> {
            for (var polygon : cube.polygons) {
                float[] vertices = new float[20];
                for (int i = 0; i < 4; i++) {
                    var vertex = polygon.vertices()[i];
                    var point = pose.pose().transformPosition(vertex.worldX(), vertex.worldY(), vertex.worldZ(), new org.joml.Vector3f());
                    vertices[i * 5] = point.x; vertices[i * 5 + 1] = point.y; vertices[i * 5 + 2] = point.z;
                    vertices[i * 5 + 3] = vertex.u(); vertices[i * 5 + 4] = vertex.v();
                }
                var normal = pose.transformNormal(polygon.normal(), new org.joml.Vector3f());
                faces.add(new Face(0, 0xFFFFFFFF, vertices, normal.x, normal.y, normal.z));
            }
        });
        if (faces.size() != 6) throw new IllegalArgumentException("Native wither skull layer changed");
        // SkullBlockRenderer.SKIN_BY_TYPE uses this original 64x32 resource, also honoring resource packs.
        // Entity skin UVs address the entire resource image. A higher-resolution
        // pack must not be cropped to the vanilla 64x32 dimensions.
        var textures = List.of(new TextureSource(Identifier.withDefaultNamespace("entity/skeleton/wither_skeleton"), 0, 0));
        writeSnapshot(client, request, textures, faces);
    }

    private static void writeSnapshot(Minecraft client, Request request, List<TextureSource> textures, List<Face> faces) {
        var resources=client.getResourceManager();
        IO.execute(()->{
            if(!request.sid.equals(BridgeRuntime.getSessionId()))return;
            try {
                var bytes=new ByteArrayOutputStream();
                try(var output=new DataOutputStream(bytes)) {
                    integer(output,0x4D43424D); integer(output,1); integer(output,textures.size());
                    for(var texture:textures) {
                        Identifier id=Identifier.fromNamespaceAndPath(texture.id.getNamespace(),"textures/"+texture.id.getPath()+".png");
                        var resource=resources.getResource(id).orElseThrow(()->new IOException("Missing texture "+id));
                        try(var stream=resource.open();var image=NativeImage.read(stream)) {
                            int width=texture.width==0?image.getWidth():texture.width;
                            int height=texture.height==0?image.getHeight():texture.height;
                            if(width<1 || height<1 || width>256 || height>256
                                    || image.getWidth()<width || image.getHeight()<height)throw new IOException("Invalid texture frame");
                            integer(output,width);integer(output,height);
                            // First frame, real resource pack pixels; raw RGBA, top-down.
                            for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
                                int pixel=image.getPixel(x,y);
                                output.writeByte(pixel>>>16);output.writeByte(pixel>>>8);output.writeByte(pixel);output.writeByte(pixel>>>24);
                            }
                        }
                    }
                    integer(output,faces.size());
                    for(var face:faces) {
                        integer(output,face.material);integer(output,face.tint);
                        for(float value:face.vertices)scalar(output,value);
                        scalar(output,face.nx);scalar(output,face.ny);scalar(output,face.nz);
                    }
                    boxes(output,request.collision);boxes(output,request.outline);
                }
                if(bytes.size()>4*1024*1024)throw new IOException("Block snapshot too large");
                Path directory=Path.of(System.getenv("LOCALAPPDATA"),"Mycraft","block-models",request.sid);
                Files.createDirectories(directory);
                Path temporary=directory.resolve(request.key+".tmp"), destination=directory.resolve(request.key+".bin");
                Files.write(temporary,bytes.toByteArray());
                try {Files.move(temporary,destination,StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING);}
                catch(AtomicMoveNotSupportedException e){Files.move(temporary,destination,StandardCopyOption.REPLACE_EXISTING);}
            } catch(Exception e){failure(request,e);}
        });
    }
    private static void failure(Request request,Exception error) {
        BridgeRuntime.log("block model unavailable: "+request.state+": "+error.getMessage());
        try {IO.execute(()->{
            if(!request.sid.equals(BridgeRuntime.getSessionId()))return;
            try {
                Path directory=Path.of(System.getenv("LOCALAPPDATA"),"Mycraft","block-models",request.sid);
                Files.createDirectories(directory);
                String message="Minecraft model unavailable: "+request.state;
                Files.writeString(directory.resolve(request.key+".error"),message.substring(0,Math.min(1000,message.length())));
            }catch(IOException ignored){ }
        });}catch(RejectedExecutionException ignored){ }
    }
    private static void cleanup(String sid) {
        if(!sid.matches("[a-f0-9]{32}"))return;
        Path directory=Path.of(System.getenv("LOCALAPPDATA"),"Mycraft","block-models",sid);
        try(var files=Files.newDirectoryStream(directory)) {
            for(Path file:files)if(file.getFileName().toString().matches("[a-f0-9]{32}\\.(bin|tmp|error)"))Files.deleteIfExists(file);
        }catch(IOException ignored){ }
        try{Files.deleteIfExists(directory);}catch(IOException ignored){ }
    }
    private static void integer(DataOutputStream output,int value)throws IOException {output.writeInt(Integer.reverseBytes(value));}
    private static void scalar(DataOutputStream output,float value)throws IOException {integer(output,Float.floatToIntBits(value));}
    private static void boxes(DataOutputStream output,List<AABB> boxes)throws IOException {
        integer(output,boxes.size());
        for(var box:boxes)for(double value:new double[]{box.minX,box.minY,box.minZ,box.maxX,box.maxY,box.maxZ})scalar(output,(float)value);
    }
}
