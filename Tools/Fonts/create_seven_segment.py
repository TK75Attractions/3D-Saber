"""Generate the original digits and LV Saber Seven Segment font.

Requires fonttools==4.60.1 (authoring only; Unity loads the resulting TTF).
Run from the Unity repository root: python Tools/Fonts/create_seven_segment.py
An optional first argument overrides the output font path.
"""

from datetime import datetime, timezone
from pathlib import Path
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont


def polygon(pen, points):
    # TrueTypeの外周は時計回り。各セグメントを独立した輪郭にする。
    signed_area = sum(
        a[0] * b[1] - b[0] * a[1]
        for a, b in zip(points, points[1:] + points[:1])
    )
    if signed_area > 0:
        points = list(reversed(points))
    pen.moveTo(points[0])
    for point in points[1:]:
        pen.lineTo(point)
    pen.closePath()


def horizontal(cy):
    return [(46, cy), (79, cy + 33), (341, cy + 33),
            (374, cy), (341, cy - 33), (79, cy - 33)]


def vertical(cx, bottom, top):
    return [(cx, top), (cx + 33, top - 33), (cx + 33, bottom + 33),
            (cx, bottom), (cx - 33, bottom + 33), (cx - 33, top - 33)]


# 上、右上、右下、下、左下、左上、中央。7と9は参照画像と同じ字形。
SEGMENTS = [horizontal(727), vertical(387, 391, 716),
            vertical(387, 44, 369), horizontal(33),
            vertical(33, 44, 369), vertical(33, 391, 716), horizontal(380)]
MASKS = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x27, 0x7F, 0x67]


def build(output):
    builder = FontBuilder(1000, isTTF=True)
    order = ['.notdef', 'space', 'hyphen', 'L', 'V'] + ['digit' + str(i) for i in range(10)]
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap({32: 'space', 45: 'hyphen', 76: 'L', 86: 'V',
                               **{48 + i: 'digit' + str(i) for i in range(10)}})
    glyphs = {}
    for name in order:
        pen = TTGlyphPen(None)
        if name.startswith('digit'):
            mask = MASKS[int(name[-1])]
            for i, segment in enumerate(SEGMENTS):
                if mask & (1 << i):
                    polygon(pen, segment)
        elif name in ('L', 'V'):
            # Lは左上下と底、Vは下側の左右と底を使う7セグの字形。
            mask = 0x38 if name == 'L' else 0x1C
            for i, segment in enumerate(SEGMENTS):
                if mask & (1 << i):
                    polygon(pen, segment)
        elif name == 'hyphen':
            polygon(pen, SEGMENTS[6])
        elif name == '.notdef':
            polygon(pen, [(0, 0), (0, 760), (420, 760), (420, 0)])
        glyphs[name] = pen.glyph()
    builder.setupGlyf(glyphs)
    builder.setupHorizontalMetrics({
        name: (260, 0) if name == 'space' else
              (500, min(x for x, y in glyphs[name].coordinates))
        for name in order
    })
    builder.setupHorizontalHeader(ascent=850, descent=-150)
    builder.setupNameTable({
        'familyName': 'Saber Seven Segment',
        'styleName': 'Regular',
        'uniqueFontIdentifier': '3D-Saber:SaberSevenSegment:1.1',
        'fullName': 'Saber Seven Segment Regular',
        'psName': 'SaberSevenSegment-Regular',
        'version': 'Version 1.100',
        'copyright': 'Original geometric digit and LV designs for the 3D-Saber project, 2026.',
        'description': 'Seven segment digits and LV with chamfered ends. No third-party font outlines.',
    })
    builder.setupOS2(sTypoAscender=850, sTypoDescender=-150, sTypoLineGap=0,
                    usWinAscent=850, usWinDescent=150, sxHeight=760, sCapHeight=760,
                    usWeightClass=400, fsType=0)
    builder.setupPost()
    builder.setupMaxp()
    stamp = int((datetime(2026, 9, 20, tzinfo=timezone.utc) -
                 datetime(1904, 1, 1, tzinfo=timezone.utc)).total_seconds())
    builder.setupHead(created=stamp, modified=stamp)
    output.parent.mkdir(parents=True, exist_ok=True)
    builder.save(output)
    # 完成バイナリを読み直し、0〜9とダッシュ、桁の等幅を確認する。
    font = TTFont(output)
    cmap = font.getBestCmap()
    assert set(cmap) == {32, 45, 76, 86, *range(48, 58)}
    for i, mask in enumerate(MASKS):
        glyph = cmap[48 + i]
        assert font['glyf'][glyph].numberOfContours == mask.bit_count()
        assert font['hmtx'][glyph][0] == 500
    for code in (76, 86):
        assert font['glyf'][cmap[code]].numberOfContours == 3
        assert font['hmtx'][cmap[code]][0] == 500
    font.close()
    print(f'Wrote {output}: 10 digits, LV, hyphen, space; validated.')


if __name__ == '__main__':
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else (
        Path(__file__).resolve().parents[2] / 'Assets/Resources/Fonts/SaberSevenSegment-Regular.ttf')
    build(destination)
