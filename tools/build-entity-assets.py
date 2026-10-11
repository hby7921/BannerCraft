"""Export original Minecraft 26.3 baked entity layers, UVs, textures and pose oracles."""
import argparse
import hashlib
import io
import json
import struct
import subprocess
from pathlib import Path
import zipfile
from PIL import Image


def digest(data):
    return hashlib.sha256(data).hexdigest()


def model_binary(parts):
    data = bytearray(b"EMD1")
    data += struct.pack("<i", len(parts))
    for p in parts:
        path = p["path"].encode("utf-8")
        data += struct.pack("<i", len(path)) + path
        data += struct.pack("<9f", *p["pose"])
        data += struct.pack("<i", len(p["faces"]))
        for face in p["faces"]:
            if len(face) != 4:
                raise ValueError("Expected a native cube quad")
            for v in face:
                data += struct.pack("<8f", *v)
    return bytes(data)


def main():
    workspace = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-jar", type=Path, default=workspace / "runtime/minecraft/downloads/minecraft-26.3-client.jar")
    parser.add_argument("--jdk", type=Path, default=workspace / ".toolchain/jdk25/jdk-25.0.4.1+1")
    parser.add_argument("--libraries", type=Path, default=workspace / "runtime/minecraft/PrismLauncher/libraries")
    parser.add_argument("--output", type=Path, default=workspace / "src/Mycraft/_Module/Visuals/entities")
    args = parser.parse_args()
    scratch = workspace / ".build-game/entity-exporter"
    scratch.mkdir(parents=True, exist_ok=True)
    jars = sorted(p for p in args.libraries.rglob("*.jar") if "/com/mojang/minecraft/" not in p.as_posix())
    cp = ";".join(p.resolve().as_posix() for p in [args.client_jar] + jars)
    javac_args = scratch / "javac.args"
    javac_args.write_text(f'-classpath\n"{cp}"\n-d\n"{scratch.as_posix()}"\n"{(workspace / "tools/EntityModelExporter.java").as_posix()}"\n', encoding="utf-8")
    subprocess.run([str(args.jdk / "bin/javac.exe"), "@" + str(javac_args)], check=True)
    model_out = scratch / "models"
    java_args = scratch / "java.args"
    java_args.write_text(f'-classpath\n"{cp};{scratch.as_posix()}"\nEntityModelExporter\n"{model_out.as_posix()}"\n', encoding="utf-8")
    subprocess.run([str(args.jdk / "bin/java.exe"), "@" + str(java_args)], check=True)
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = {"version": "26.3", "clientJarSha256": digest(args.client_jar.read_bytes()), "models": [], "textures": [], "boatKinds": {}}
    models = {"wither_skeleton": "SkeletonModel.createBodyLayer().apply(MeshTransformer.scaling(1.2f))",
              "boat": "BoatModel.createBoatModel()", "chest_boat": "BoatModel.createChestBoatModel()",
              "raft": "RaftModel.createRaftModel()", "chest_raft": "RaftModel.createChestRaftModel()"}
    for name, authority in models.items():
        data = model_binary(json.loads((model_out / (name + ".json")).read_text()))
        (args.output / (name + ".model")).write_bytes(data)
        manifest["models"].append({"name": name, "authority": authority, "sha256": digest(data)})
    with zipfile.ZipFile(args.client_jar) as archive:
        sources = {"wither_skeleton": "assets/minecraft/textures/entity/skeleton/wither_skeleton.png"}
        for folder in ("boat", "chest_boat"):
            for source in archive.namelist():
                if source.startswith("assets/minecraft/textures/entity/" + folder + "/") and source.endswith(".png"):
                    wood = Path(source).stem
                    raft = wood == "bamboo"
                    kind = wood + ("_chest_raft" if raft and folder == "chest_boat" else "_raft" if raft else "_" + folder)
                    sources[kind] = source
                    model = ("chest_raft" if folder == "chest_boat" else "raft") if raft else folder
                    manifest["boatKinds"][kind] = {"model": model, "texture": kind}
        for material in ("wooden", "stone", "copper", "iron", "golden", "diamond", "netherite"):
            sources[material + "_sword"] = "assets/minecraft/textures/item/" + material + "_sword.png"
        for name, source in sources.items():
            original = archive.read(source)
            with Image.open(io.BytesIO(original)) as image:
                w, h = image.size
                rgba = image.convert("RGBA").tobytes()
            data = struct.pack("<ii", w, h) + rgba
            (args.output / (name + ".texture")).write_bytes(data)
            manifest["textures"].append({"name": name, "source": source, "width": w, "height": h, "sourceSha256": digest(original), "rgbaSha256": digest(rgba), "sha256": digest(data)})
        # The held transform is authored in item/handheld.json, inherited by all swords.
        manifest["swordThirdPerson"] = json.loads(archive.read("assets/minecraft/models/item/handheld.json"))["display"]["thirdperson_righthand"]
    fixtures = workspace / "tests/EntityVisuals/Fixtures"
    fixtures.mkdir(parents=True, exist_ok=True)
    for name in ("skeleton-pose-oracle", "boat-pose-oracle"):
        samples = json.loads((model_out / (name + ".json")).read_text())
        for sample in samples:
            for part in sample["parts"]:
                part.pop("faces", None)
        (fixtures / (name + ".json")).write_text(json.dumps(samples, indent=2) + "\n", encoding="utf-8")
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Exported {len(models)} native layers, {len(sources)} original textures and {len(manifest['boatKinds'])} boat kinds")


if __name__ == "__main__":
    main()
