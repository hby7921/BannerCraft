import java.net.*;
import java.nio.file.*;

/** Ensures an authoritative server request is usable with all client classes absent. */
public final class GenericMobServerLoadProbe {
    public static final class Driver {
        public static void run() {
            String sid="0123456789abcdef0123456789abcdef",uuid="00000000-0000-0000-0000-000000000001";
            String key=dev.mycraft.bridge.NativeMobModels.request(sid,7,uuid);
            if(!key.matches("[0-9a-f]{32}"))throw new AssertionError("No native visual key");
            if(!key.equals(dev.mycraft.bridge.NativeMobModels.request(sid,9,uuid)))throw new AssertionError("UUID visual identity changed");
            for(int i=2;i<=49;i++){String value=dev.mycraft.bridge.NativeMobModels.requestProjectile(sid,i,new java.util.UUID(0,i).toString());if(value.length()!=32)throw new AssertionError("Projectile slot refused early");}
            if(!dev.mycraft.bridge.NativeMobModels.requestProjectile(sid,50,new java.util.UUID(0,50).toString()).isEmpty())throw new AssertionError("Unbounded projectile visual slots");
            for(int i=51;i<=65;i++)if(dev.mycraft.bridge.NativeMobModels.request(sid,i,new java.util.UUID(0,i).toString()).length()!=32)throw new AssertionError("Projectile volley starved creature models");
            dev.mycraft.bridge.NativeMobModels.retire(uuid);
            String replacement=dev.mycraft.bridge.NativeMobModels.request(sid,10,uuid);if(replacement.isEmpty()||replacement.equals(key))throw new AssertionError("Retired visual request not released");
        }
    }
    public static void main(String[] args)throws Exception {
        String[] locations=System.getProperty("java.class.path").split(java.io.File.pathSeparator);URL[] urls=new URL[locations.length];for(int i=0;i<urls.length;i++)urls[i]=Path.of(locations[i]).toUri().toURL();
        try(var loader=new URLClassLoader(urls,ClassLoader.getPlatformClassLoader()) {
            @Override protected Class<?> loadClass(String name,boolean resolve)throws ClassNotFoundException {
                if(name.startsWith("net.minecraft.client.")||name.startsWith("com.mojang.blaze3d."))throw new ClassNotFoundException("Server deliberately has no client classes: "+name);
                return super.loadClass(name,resolve);
            }
        }){Class.forName("GenericMobServerLoadProbe$Driver",true,loader).getMethod("run").invoke(null);}
        System.out.println("PASS native visual request/priority/retire with all client classes absent");
    }
}
