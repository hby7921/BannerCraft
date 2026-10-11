// Read-only verification against the real26.3 renderer. Run as a Java source
// file with the pinned client jar and its normal library jars on the classpath.
import java.lang.reflect.*;
import java.nio.*;
import java.security.*;
import java.util.*;
import net.minecraft.util.RandomSource;
import net.minecraft.client.renderer.entity.state.LightningBoltRenderState;
import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.VertexConsumer;

class LightningOracle {
    public static void main(String[] args) throws Exception {
        for(String seedHex : new String[]{"0000000000000000","0000000000000001","0123456789abcdef","ffffffffffffffff"}) {
            long seed=Long.parseUnsignedLong(seedHex,16);
            RandomSource random=RandomSource.createThreadLocalInstance(seed);
            float[] xs=new float[8],zs=new float[8];float x=0,z=0;
            for(int i=7;i>=0;i--){xs[i]=x;zs[i]=z;x+=random.nextInt(11)-5;z+=random.nextInt(11)-5;}
            LightningBoltRenderState state=new LightningBoltRenderState();state.seed=seed;
            List<float[]> vertices=new ArrayList<>();
            VertexConsumer consumer=(VertexConsumer)Proxy.newProxyInstance(VertexConsumer.class.getClassLoader(),new Class<?>[]{VertexConsumer.class},
                (proxy,method,values)->{
                    if(method.getName().equals("addVertex")) {
                        // Original quad calls the Matrix4fc overload using this
                        // identity Pose. Convert its MCxyz to native x,z,y.
                        int offset=values.length==4?1:0;
                        vertices.add(new float[]{(float)values[offset],(float)values[offset+2],(float)values[offset+1]});
                    }
                    if(method.getName().equals("setColor")&&values[0] instanceof Float&&
                        ((float)values[0]!=.45f||(float)values[1]!=.45f||(float)values[2]!=.5f||(float)values[3]!=.3f))
                        throw new AssertionError("Native lightning vertex color changed");
                    return proxy;
                });
            Class<?> renderer=Class.forName("net.minecraft.client.renderer.entity.LightningBoltRenderer");
            Method render=renderer.getDeclaredMethod("lambda$submit$0",LightningBoltRenderState.class,float[].class,float.class,float[].class,float.class,
                PoseStack.Pose.class,VertexConsumer.class);render.setAccessible(true);
            render.invoke(null,state,xs,x,zs,z,new PoseStack.Pose(),consumer);
            ByteBuffer bytes=ByteBuffer.allocate(vertices.size()*12).order(ByteOrder.LITTLE_ENDIAN);
            for(float[] vertex:vertices)for(float coordinate:vertex)bytes.putFloat(coordinate);
            String hash=HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes.array()));
            System.out.println(seedHex+" "+vertices.size()+" "+hash);
        }
    }
}
