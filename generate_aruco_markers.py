"""Generate printable multi-face handle markers for aruco_camera.py."""

import argparse
from pathlib import Path

from python_runtime import reexec_with_cv2


reexec_with_cv2()

import cv2
import numpy as np


def parse_args():
    parser = argparse.ArgumentParser(description="Generate the saber ArUco marker set.")
    parser.add_argument("--output", type=Path, default=Path("aruco_markers"))
    parser.add_argument("--pixels", type=int, default=600)
    return parser.parse_args()


def generate_marker(dictionary, marker_id, pixels):
    if hasattr(cv2.aruco, "generateImageMarker"):
        return cv2.aruco.generateImageMarker(dictionary, marker_id, pixels)
    image = np.zeros((pixels, pixels), dtype=np.uint8)
    cv2.aruco.drawMarker(dictionary, marker_id, pixels, image, 1)
    return image


def marker_svg(marker, marker_id, x, y, size_mm):
    cells = marker.shape[0]
    cell = size_mm / cells
    parts = [
        f'<rect x="{x - 5:.3f}" y="{y - 5:.3f}" width="{size_mm + 10:.3f}" '
        f'height="{size_mm + 10:.3f}" fill="white" stroke="#bbb" stroke-width="0.2"/>'
    ]
    for row in range(cells):
        for column in range(cells):
            if marker[row, column] == 0:
                parts.append(
                    f'<rect x="{x + column * cell:.3f}" y="{y + row * cell:.3f}" '
                    f'width="{cell + 0.02:.3f}" height="{cell + 0.02:.3f}" fill="black"/>'
                )
    parts.append(
        f'<text x="{x + size_mm / 2:.3f}" y="{y - 7.5:.3f}" '
        f'text-anchor="middle" font-family="sans-serif" font-size="4">ID {marker_id}</text>'
    )
    return "\n".join(parts)


def write_svg_sheet(dictionary, output):
    marker_size = 45.0
    positions = [
        (35.0, 28.0),
        (130.0, 28.0),
        (35.0, 93.0),
        (130.0, 93.0),
        (35.0, 168.0),
        (130.0, 168.0),
        (35.0, 233.0),
        (130.0, 233.0),
    ]
    body = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<svg xmlns="http://www.w3.org/2000/svg" width="210mm" height="297mm" '
        'viewBox="0 0 210 297">',
        '<rect width="210" height="297" fill="white"/>',
        '<text x="105" y="10" text-anchor="middle" font-family="sans-serif" '
        'font-size="5" font-weight="bold">ArUco 4x4 handle markers - print at 100%</text>',
        '<text x="105" y="16" text-anchor="middle" font-family="sans-serif" '
        'font-size="3.5">Red handle: IDs 0-3 / Blue handle: IDs 4-7 / black square = 45 mm</text>',
        '<line x1="10" y1="158" x2="200" y2="158" stroke="#888" '
        'stroke-width="0.25" stroke-dasharray="2,2"/>',
    ]
    for marker_id, (x, y) in enumerate(positions):
        marker = generate_marker(dictionary, marker_id, 6)
        body.append(marker_svg(marker, marker_id, x, y, marker_size))
    body.append('</svg>')
    sheet = output / "aruco_handle_markers_A4.svg"
    sheet.write_text("\n".join(body) + "\n", encoding="utf-8")
    print(sheet)


def main():
    args = parse_args()
    if not hasattr(cv2, "aruco"):
        raise RuntimeError("The installed OpenCV has no ArUco support.")
    args.output.mkdir(parents=True, exist_ok=True)
    dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    for marker_id in range(8):
        marker = generate_marker(dictionary, marker_id, max(100, args.pixels))
        # A white quiet zone is required around every marker.
        border = max(20, args.pixels // 8)
        printable = cv2.copyMakeBorder(
            marker, border, border, border, border, cv2.BORDER_CONSTANT, value=255
        )
        group = "red" if marker_id < 4 else "blue"
        path = args.output / f"{group}_handle_id_{marker_id}.png"
        if not cv2.imwrite(str(path), printable):
            raise OSError(f"Could not write {path}")
        print(path)
    write_svg_sheet(dictionary, args.output)


if __name__ == "__main__":
    main()
