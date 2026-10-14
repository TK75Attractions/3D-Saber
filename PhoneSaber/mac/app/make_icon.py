#!/usr/bin/env python3
"""ゲームと起動アプリのアイコン（1024px PNG と .icns）を作る。交差する赤と青のセイバー。"""
import math
from pathlib import Path
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 1024
HERE = Path(__file__).resolve().parent


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def background():
    image = Image.new("RGB", (SIZE, SIZE))
    pixels = image.load()
    for y in range(SIZE):
        for x in range(SIZE):
            # 中央が少し明るい濃紺。四隅は黒に近づける。
            d = math.hypot(x - SIZE * 0.5, y - SIZE * 0.42) / (SIZE * 0.75)
            t = max(0.0, 1.0 - d)
            pixels[x, y] = (int(8 + 22 * t), int(10 + 18 * t), int(24 + 48 * t))
    return image


def blade(start, end, colour, width):
    """光る刃: 太いぼかし（色）＋細い芯（白に近い）。加算合成用の黒地レイヤーを返す。"""
    layer = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    for blur, w, strength in ((60, width * 3.2, 0.55), (26, width * 1.8, 0.9), (8, width * 1.0, 1.0)):
        glow = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
        ImageDraw.Draw(glow).line((start, end), fill=tuple(int(c * strength) for c in colour), width=int(w))
        layer = ImageChops.add(layer, glow.filter(ImageFilter.GaussianBlur(blur)))
    core = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    draw = ImageDraw.Draw(core)
    tint = tuple(min(255, 200 + c // 4) for c in colour)
    draw.line((start, end), fill=tint, width=int(width * 0.55))
    for point in (start, end):
        r = width * 0.275
        draw.ellipse((point[0] - r, point[1] - r, point[0] + r, point[1] + r), fill=tint)
    return ImageChops.add(layer, core.filter(ImageFilter.GaussianBlur(1.2)))


def hilt(at, direction, colour):
    """握り: 刃の根元に短い金属色の柄。"""
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    dx, dy = direction
    length, width = 150, 58
    end = (at[0] - dx * length, at[1] - dy * length)
    draw.line((at, end), fill=(70, 74, 86, 255), width=width)
    draw.line((at, end), fill=(120, 126, 140, 255), width=int(width * 0.35))
    ring = (at[0] - dx * 18, at[1] - dy * 18)
    r = width * 0.62
    draw.ellipse((ring[0] - r, ring[1] - r, ring[0] + r, ring[1] + r), fill=colour + (255,))
    return layer


def main():
    out_png = HERE / "icon_1024.png"
    image = background()
    m = SIZE * 0.2
    # 赤: 左下→右上、青: 右下→左上。柄は下側。
    red_base, red_tip = (m + 40, SIZE - m - 20), (SIZE - m + 10, m - 10)
    blue_base, blue_tip = (SIZE - m - 40, SIZE - m - 20), (m - 10, m - 10)
    image = ImageChops.add(image, blade(red_base, red_tip, (255, 40, 60), 46))
    image = ImageChops.add(image, blade(blue_base, blue_tip, (40, 140, 255), 46))
    # 交点の火花（緑寄りの白）。
    spark = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    c = (SIZE * 0.5, SIZE * 0.45)
    ImageDraw.Draw(spark).ellipse((c[0] - 70, c[1] - 70, c[0] + 70, c[1] + 70), fill=(150, 255, 190))
    image = ImageChops.add(image, spark.filter(ImageFilter.GaussianBlur(40)))
    image = image.convert("RGBA")
    for base, tip, colour in ((red_base, red_tip, (255, 60, 80)), (blue_base, blue_tip, (60, 150, 255))):
        length = math.hypot(tip[0] - base[0], tip[1] - base[1])
        direction = ((tip[0] - base[0]) / length, (tip[1] - base[1]) / length)
        image = Image.alpha_composite(image, hilt(base, direction, colour))
    # macOS のアイコン形（余白つきの角丸四角）。
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    inner = int(SIZE * 0.8)
    body = image.resize((inner, inner), Image.LANCZOS)
    body.putalpha(rounded_mask(inner, int(inner * 0.225)))
    shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    offset = (SIZE - inner) // 2
    ImageDraw.Draw(shadow).rounded_rectangle((offset, offset + 14, offset + inner, offset + inner + 14),
                                             radius=int(inner * 0.225), fill=(0, 0, 0, 120))
    icon = Image.alpha_composite(icon, shadow.filter(ImageFilter.GaussianBlur(18)))
    icon.alpha_composite(body, (offset, offset))
    icon.save(out_png)
    with tempfile.TemporaryDirectory() as directory:
        iconset = Path(directory) / "icon.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            icon.resize((size, size), Image.LANCZOS).save(iconset / f"icon_{size}x{size}.png")
            icon.resize((size * 2, size * 2), Image.LANCZOS).save(iconset / f"icon_{size}x{size}@2x.png")
        subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(HERE / "icon.icns")], check=True)
    print(out_png, HERE / "icon.icns")


if __name__ == "__main__":
    sys.exit(main())
