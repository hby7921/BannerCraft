import com.google.gson.*;
import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.math.Transformation;
import com.mojang.serialization.JsonOps;
import net.minecraft.client.model.object.skull.SkullModel;
import net.minecraft.client.model.object.skull.SkullModelBase;
import org.joml.Vector3f;
import java.nio.file.*;
import java.util.zip.ZipFile;

/** Original SkullModel layer and the pinned item's own special transformation, exported for GUI projection. */
public final class SkullModelExporter {
    public static void main(String[] args) throws Exception {
        JsonObject dispatch;
        try (var archive = new ZipFile(args[0]); var reader = new java.io.InputStreamReader(
                archive.getInputStream(archive.getEntry("assets/minecraft/items/wither_skeleton_skull.json")), java.nio.charset.StandardCharsets.UTF_8)) {
            dispatch = JsonParser.parseReader(reader).getAsJsonObject().getAsJsonObject("model");
        }
        if (!dispatch.get("type").getAsString().equals("minecraft:special")
                || !dispatch.getAsJsonObject("model").get("kind").getAsString().equals("wither_skeleton"))
            throw new IllegalArgumentException("Wither skull item dispatch changed");
        var model = new SkullModel(SkullModel.createMobHeadLayer().bakeRoot());
        model.setupAnim(new SkullModelBase.State());
        var stack = new PoseStack();
        stack.mulPose(Transformation.CODEC.parse(JsonOps.INSTANCE, dispatch.get("transformation")).getOrThrow());
        var faces = new JsonArray();
        model.root().visit(stack, (pose, path, index, cube) -> {
            for (var polygon : cube.polygons) {
                var face = new JsonObject(); var vertices = new JsonArray();
                for (var vertex : polygon.vertices()) {
                    var point = pose.pose().transformPosition(vertex.worldX(), vertex.worldY(), vertex.worldZ(), new Vector3f());
                    for (float value : new float[]{point.x, point.y, point.z, vertex.u(), vertex.v()}) vertices.add(value);
                }
                var normal = pose.transformNormal(polygon.normal(), new Vector3f());
                var normals = new JsonArray(); normals.add(normal.x); normals.add(normal.y); normals.add(normal.z);
                face.add("vertices", vertices); face.add("normal", normals); faces.add(face);
            }
        });
        if (faces.size() != 6) throw new IllegalArgumentException("Native wither skull layer changed");
        var output = new JsonObject(); output.addProperty("authority", "SkullModel.createMobHeadLayer + item special transformation");
        output.addProperty("clientJarSha256", java.util.HexFormat.of().formatHex(java.security.MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(Path.of(args[0])))));
        output.add("transformation", dispatch.get("transformation")); output.add("faces", faces);
        Path destination = Path.of(args[1]); Files.createDirectories(destination.getParent());
        Files.writeString(destination, new GsonBuilder().setPrettyPrinting().create().toJson(output));
    }
}
