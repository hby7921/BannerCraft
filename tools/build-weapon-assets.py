"""Extract the original 26.3 held spear and projectile textures for native meshes."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile

from PIL import Image


def main():
    workspace = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-jar", type=Path, default=workspace / "runtime/minecraft/downloads/minecraft-26.3-client.jar")
    parser.add_argument("--output", type=Path, default=workspace / "src/Mycraft/_Module/Visuals/weapons")
    args = parser.parse_args()
    sources = {
        "trident": "assets/minecraft/textures/entity/trident/trident.png",
        "arrow": "assets/minecraft/textures/entity/projectiles/arrow.png",
        "spectral_arrow": "assets/minecraft/textures/entity/projectiles/arrow_spectral.png",
        "tipped_arrow": "assets/minecraft/textures/entity/projectiles/arrow_tipped.png",
    }
    for material in ("wooden", "stone", "copper", "iron", "golden", "diamond", "netherite"):
        name = material + "_spear_in_hand"
        sources[name] = "assets/minecraft/textures/item/" + name + ".png"
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = {"version": "26.3", "textures": []}
    with zipfile.ZipFile(args.client_jar) as archive:
        for name, source in sources.items():
            original = archive.read(source)
            with Image.open(io.BytesIO(original)) as image:
                if image.size != (32, 32):
                    raise ValueError(f"Expected original 32x32 texture: {source}: {image.size}")
                rgba = image.convert("RGBA").tobytes()
            (args.output / (name + ".rgba")).write_bytes(rgba)
            manifest["textures"].append({
                "name": name, "source": source, "width": 32, "height": 32,
                "sourceSha256": hashlib.sha256(original).hexdigest(),
                "rgbaSha256": hashlib.sha256(rgba).hexdigest(),
            })
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Extracted {len(sources)} original weapon textures to {args.output}")


if __name__ == "__main__":
    main()
