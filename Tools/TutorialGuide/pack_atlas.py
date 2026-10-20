"""python pack_atlas.py FRAMES OUTPUT_PNG (Pillow)"""
from PIL import Image
from pathlib import Path
import sys
atlas = Image.new("RGBA", (2048, 2048))
for index in range(16):
    with Image.open(Path(sys.argv[1]) / f"pose-{index:02}.png") as frame:
        atlas.paste(frame, (index % 4 * 512, index // 4 * 512))
atlas.save(sys.argv[2], optimize=True)
