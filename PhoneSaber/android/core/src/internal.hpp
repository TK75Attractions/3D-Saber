#pragma once
#include "phonesaber/core.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <set>

namespace phonesaber::detail {
using Mask = std::vector<uint8_t>;
using Points = std::vector<PixelPoint>;
inline double clamp01(double x) { return std::min(std::max(x, 0.0), 1.0); }
inline int rounded(double x) { return static_cast<int>(std::round(x)); }
inline bool line_source(const Candidate& c) { return c.source.compare(0, 9, "core-line") == 0; }
struct Evidence {
    SaberColor color;
    Mask radiance, value, chroma, color_mask, core_mask;
};
struct Scored {
    Candidate candidate;
    Points red_support;
};
Mask dilate(const Mask& mask, int width, int height, int radius);
Mask erode(const Mask& mask, int width, int height, int radius);
Mask close(const Mask& mask, int width, int height, int radius);
std::optional<Scored> score_component(const Points& points, int width, int height,
                                     const Mask* component_mask, const std::set<int>* indices,
                                     const Evidence& evidence, const std::string& source = "color-mask",
                                     int minimum_override = -1);
std::vector<Scored> components(const Mask& mask, int width, int height,
                               const Evidence& evidence, int* pixel_count = nullptr,
                               int minimum_override = -1);
} // namespace phonesaber::detail
