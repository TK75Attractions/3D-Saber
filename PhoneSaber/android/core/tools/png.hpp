#pragma once
#include <cstdint>
#include <string>
#include <vector>
struct PNGImage {
    int width, height;
    std::vector<uint8_t> rgba;
};
// Lossless decoder for corpus PNGs: 8-bit RGB/RGBA, non-interlaced, filters 0–4.
// No color-management, premultiplication or gamma conversion (like ffmpeg BGRA).
PNGImage read_png(const std::string& path);
