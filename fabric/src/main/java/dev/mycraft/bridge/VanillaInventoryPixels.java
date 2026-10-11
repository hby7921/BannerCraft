package dev.mycraft.bridge;

/** Conversion from Minecraft's premultiplied GPU render target to the host's straight RGBA texture. */
final class VanillaInventoryPixels {
    static int[] boundedSize(int width, int height) {
        if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new IllegalArgumentException("frame dimensions");
        double ratio = Math.min(1, Math.min((double)VanillaInventoryFiles.MAX_WIDTH / width, (double)VanillaInventoryFiles.MAX_HEIGHT / height));
        return new int[]{Math.max(1, (int)(width * ratio)), Math.max(1, (int)(height * ratio))};
    }
    static void copyStraightRgba(byte[] bottomUp, int width, int height, byte[] target, int offset) {
        if (width < 1 || height < 1 || width > VanillaInventoryFiles.MAX_WIDTH || height > VanillaInventoryFiles.MAX_HEIGHT
                || bottomUp.length != width * height * 4 || offset < 0 || target.length - offset != bottomUp.length)
            throw new IllegalArgumentException("RGBA buffer size");
        for (int y=0;y<height;y++) for(int x=0;x<width;x++) {
            int source=((height-1-y)*width+x)*4, dest=offset+(y*width+x)*4;
            int alpha=bottomUp[source+3]&255;
            for(int channel=0;channel<3;channel++)
                target[dest+channel]=(byte)(alpha==0?0:Math.min(255,((bottomUp[source+channel]&255)*255+alpha/2)/alpha));
            target[dest+3]=(byte)alpha;
        }
    }
    private VanillaInventoryPixels() {}
}
