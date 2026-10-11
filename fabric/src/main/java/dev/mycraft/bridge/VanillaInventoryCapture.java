package dev.mycraft.bridge;

import com.mojang.blaze3d.pipeline.RenderTarget;
import com.mojang.blaze3d.pipeline.TextureTarget;
import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.renderpearl.api.GpuFormat;
import com.mojang.renderpearl.api.buffers.GpuBuffer;
import com.mojang.renderpearl.api.textures.FilterMode;
import net.minecraft.client.renderer.RenderPipelines;
import org.joml.Vector4f;
import java.util.Optional;
import java.util.function.Consumer;

/** The vanilla Screenshot helper forces opaque alpha. Read the actual GUI render target instead. */
final class VanillaInventoryCapture {
    record Pixels(int width, int height, byte[] bottomUp) {}
    private static TextureTarget reduced;

    static void request(RenderTarget source, Consumer<Pixels> complete, Consumer<RuntimeException> failed) {
        RenderSystem.assertOnRenderThread();
        int[] size=VanillaInventoryPixels.boundedSize(source.width,source.height);
        int width=size[0],height=size[1];
        if(reduced==null || reduced.width!=width || reduced.height!=height) {
            release();
            reduced=new TextureTarget("Mycraft native inventory readback",width,height,GpuFormat.RGBA8_UNORM,null);
        }
        var encoder=RenderSystem.getDevice().createCommandEncoder();
        // TRACY_BLIT is vanilla's unblended RGBA screen copy (all four channels, nearest sampling).
        try(var pass=encoder.createRenderPass(()->"Mycraft inventory downsample",reduced.getColorTextureView(),Optional.of(new Vector4f()))) {
            pass.setPipeline(RenderSystem.getCompiledPipeline(RenderPipelines.TRACY_BLIT));
            RenderSystem.bindDefaultUniforms(pass);
            pass.setUniform("InSampler",source.getColorTextureView(),RenderSystem.getSamplerCache().getClampToEdge(FilterMode.NEAREST));
            pass.draw(3,1,0,0);
        }
        GpuBuffer readback=RenderSystem.getDevice().createBuffer(()->"Mycraft bounded inventory pixels",
                GpuBuffer.USAGE_MAP_READ|GpuBuffer.USAGE_COPY_DST,(long)width*height*4);
        try {
            encoder.copyTextureToBuffer(reduced.getColorTexture(),readback,0,()-> {
                try {
                    byte[] bytes=new byte[width*height*4];
                    try(var map=readback.map(true,false)){map.data().get(bytes);}
                    complete.accept(new Pixels(width,height,bytes));
                } catch(RuntimeException e){failed.accept(e);}
                finally {readback.close();}
            },0);
        } catch(RuntimeException e){readback.close();throw e;}
    }
    static void release() {
        if(reduced!=null){reduced.destroyBuffers();reduced=null;}
    }
    private VanillaInventoryCapture() {}
}
