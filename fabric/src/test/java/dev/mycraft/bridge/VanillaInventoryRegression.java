package dev.mycraft.bridge;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.nio.file.Files;
import java.nio.file.Path;

/** Input compatibility and transport boundaries, without starting a client or touching a world. */
public final class VanillaInventoryRegression {
    private static int checks;
    private static void check(boolean value, String message) { checks++; if(!value) throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        check(VanillaInventoryInput.button(0)==1,"26.3 SDL left button");
        check(VanillaInventoryInput.button(1)==3,"26.3 SDL right button splits stacks");
        check(VanillaInventoryInput.button(2)==2,"26.3 SDL middle button clones stacks");
        check(VanillaInventoryInput.key(69,0).key()==8,"E maps to SDL inventory key");
        check(VanillaInventoryInput.key(256,0).isEscape(),"Escape is recognized by actual MC InputWithModifiers");
        check(VanillaInventoryInput.key(257,0).isConfirmation(),"Enter is recognized by actual MC input");
        check(VanillaInventoryInput.key(263,0).isLeft(),"Left arrow edits search");
        check(VanillaInventoryInput.key(265,0).isUp(),"Up arrow navigates screen");
        check(VanillaInventoryInput.key(259,0).key()==42,"Backspace edits search");
        check(VanillaInventoryInput.key(65,0).keycode()==97,"lowercase SDL symbol preserves Ctrl-A");
        var shifted=VanillaInventoryInput.key(49,VanillaInventoryInput.modifiers(1));
        check(shifted.key()==30 && shifted.hasShiftDown() && !shifted.hasControlDown(),"Shift and hotbar key");
        var control=VanillaInventoryInput.key(86,VanillaInventoryInput.modifiers(2));
        check(control.keycode()==118 && control.hasControlDown() && !control.hasShiftDown(),"Ctrl-V does not become Shift");
        check(VanillaInventoryInput.key(65,VanillaInventoryInput.modifiers(4)).hasAltDown(),"Alt modifier");
        boolean invalid=false; try{VanillaInventoryInput.key(999,0);}catch(IllegalArgumentException e){invalid=true;}
        check(invalid,"unknown keys rejected");
        check(VanillaInventoryScreenPolicy.mayAdopt(true,true,false,true,true),"active local player's vanilla container is adopted");
        check(!VanillaInventoryScreenPolicy.mayAdopt(true,true,false,false,true),"chat/pause is never adopted");
        check(!VanillaInventoryScreenPolicy.mayAdopt(true,true,false,true,false),"stale/foreign container menu is never adopted");
        check(!VanillaInventoryScreenPolicy.mayAdopt(false,true,false,true,true),"disconnected bridge never adopts a screen");
        check(!VanillaInventoryScreenPolicy.mayAdopt(true,false,false,true,true),"death/world teardown blocks adoption");
        check(!VanillaInventoryScreenPolicy.mayAdopt(true,true,true,true,true),"spectator does not gain inventory access");
        check(!VanillaInventoryScreenPolicy.inputMatchesScreen(42,43,45),"old screen click is dropped on menu transition");
        check(VanillaInventoryScreenPolicy.inputMatchesScreen(43,43,45),"first displayed frame of new container authorizes input");
        check(!VanillaInventoryScreenPolicy.inputMatchesScreen(46,43,45),"unpublished future frame cannot authorize input");
        check(!VanillaInventoryScreenPolicy.inputMatchesScreen(42,Long.MAX_VALUE,45),"new container awaits its first render before input");
        int[] captureSize=VanillaInventoryPixels.boundedSize(3840,2160);
        check(captureSize[0]==1280 && captureSize[1]==720,"4K screen readback is reduced on GPU before CPU transfer");
        int[] portraitSize=VanillaInventoryPixels.boundedSize(600,1200);
        check(portraitSize[0]==360 && portraitSize[1]==720,"capture keeps full portrait canvas aspect ratio");
        byte[] bottomUp={(byte)0,(byte)64,(byte)0,(byte)128,(byte)255,0,0,(byte)255};
        byte[] rgba=new byte[8];
        VanillaInventoryPixels.copyStraightRgba(bottomUp,1,2,rgba,0);
        check((rgba[0]&255)==255 && (rgba[3]&255)==255,"GPU bottom-up rows are corrected for host texture");
        check((rgba[5]&255)==128 && (rgba[7]&255)==128,"native translucent UI retains alpha and is unpremultiplied");
        byte[] transparent=new byte[4];
        VanillaInventoryPixels.copyStraightRgba(new byte[]{55,66,77,0},1,1,transparent,0);
        check(java.util.Arrays.equals(transparent,new byte[4]),"transparent pixels cannot retain world RGB");
        check(!VanillaInventoryFiles.validId("../other") && !VanillaInventoryFiles.validId("A".repeat(32)),"path/session validation");
        String sid="1".repeat(32),token="2".repeat(32);
        Path dir=Files.createTempDirectory("mycraft-inventory-regression-");
        try {
            JsonObject root=new JsonObject();root.addProperty("sid",sid);root.addProperty("token",token);root.addProperty("open",true);root.addProperty("time",10000);
            JsonArray events=new JsonArray();root.add("events",events);
            write(dir,root);
            check(VanillaInventoryFiles.read(dir,sid,10000)!=null,"valid lease");
            check(VanillaInventoryFiles.read(dir,sid,14000)==null,"stale lease rejected");
            check(VanillaInventoryFiles.read(dir,"3".repeat(32),10000)==null,"wrong session rejected");
            root.addProperty("time",Long.MIN_VALUE);write(dir,root);
            check(VanillaInventoryFiles.read(dir,sid,10000)==null,"timestamp underflow cannot bypass freshness");
            root.addProperty("time",12000);write(dir,root);
            check(VanillaInventoryFiles.read(dir,sid,10000)==null,"future lease rejected");
            root.addProperty("time",10000);
            for(int i=0;i<256;i++)events.add(new JsonObject());write(dir,root);
            check(VanillaInventoryFiles.read(dir,sid,10000).events().size()==256,"bounded retransmission window accepted");
            events.add(new JsonObject());write(dir,root);
            check(VanillaInventoryFiles.read(dir,sid,10000)==null,"overfull retransmission window rejected");
            Files.write(dir.resolve("input.json"),new byte[VanillaInventoryFiles.MAX_INPUT_BYTES+1]);
            check(VanillaInventoryFiles.read(dir,sid,10000)==null,"oversized input rejected before allocation");
        } finally { Files.deleteIfExists(dir.resolve("input.json"));Files.deleteIfExists(dir); }
        System.out.println("PASS: vanilla inventory " + checks + " input/transport assertions");
    }
    private static void write(Path dir,JsonObject data) throws Exception { Files.writeString(dir.resolve("input.json"),data.toString()); }
}
