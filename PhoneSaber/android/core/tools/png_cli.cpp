#include "phonesaber/core.hpp"
#include "png.hpp"
#include <cstring>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <locale>
#include <stdexcept>
using namespace phonesaber;

static void point(PixelPoint p) { std::cout << "{\"x\":" << p.x << ",\"y\":" << p.y << "}"; }
static void endpoints(Endpoints e) { std::cout << '['; point(e.first); std::cout << ','; point(e.second); std::cout << ']'; }
static uint64_t double_bits(double value) {
    uint64_t bits;
    static_assert(sizeof(bits) == sizeof(value), "requires IEEE-754 binary64");
    std::memcpy(&bits,&value,sizeof(bits));
    return bits;
}
static void candidate(const Candidate& c) {
    std::cout << "{\"production\":{\"comparison_endpoints\":";
    endpoints(c.comparison_endpoints);
    std::cout << ",\"robust_endpoints\":";
    if (c.robust_interval_endpoints) endpoints(*c.robust_interval_endpoints); else std::cout << "null";
    std::cout << ",\"component_area\":" << c.component_area << ",\"compact_red\":" << c.compact_red << ",\"warm_fraction_bits\":";
    if (c.warm_no_deep_red) std::cout << '"' << double_bits(c.warm_no_deep_red->warm_fraction) << '"'; else std::cout << "null";
    const auto& b = c.score_breakdown;
    const double values[] = {c.score,b.total(),c.radiance,c.mean_value,c.high_value_ratio,c.color_purity,
        c.clipped_white_ratio,c.brightness_variation,c.local_contrast,c.longitudinal_high_coverage,
        c.width_variation,c.core_support,c.longitudinal_core_coverage,c.longitudinal_continuity,
        c.retained_body_ratio,c.raw_pca_span,c.robust_body_length,c.axial_density,
        b.proposal_penalty,b.radiance,b.length,b.aspect,b.extent,b.width_consistency,b.area,
        b.peak_brightness,b.mean_brightness,b.high_brightness_ratio,b.color_purity,b.local_contrast,
        b.emitter_texture,b.clipped_white,b.longitudinal_high_coverage,b.core_support,b.longitudinal_core_coverage};
    std::cout << ",\"double_bits\":[";
    for (std::size_t i = 0; i < sizeof(values)/sizeof(values[0]); ++i) {
        if (i) std::cout << ',';
        std::cout << '"' << double_bits(values[i]) << '"';
    }
    std::cout << "]},\"warmNoDeepRed\":";
    if (c.warm_no_deep_red) {
        auto v = *c.warm_no_deep_red;
        std::cout << "{\"applied\":true,\"deepCount\":" << v.deep_count << ",\"warmCount\":" << v.warm_count
                  << ",\"pixelCount\":" << v.pixel_count << ",\"warmFrac\":" << v.warm_fraction
                  << ",\"rejected\":" << v.rejected << ",\"rejectionReason\":" << (v.rejected ? "\"warmNoDeepRed\"" : "null") << '}';
    } else std::cout << "null";
    std::cout << ",\"blueNoDeepSupport\":";
    if (c.blue_no_deep_support) {
        auto v = *c.blue_no_deep_support;
        std::cout << "{\"applied\":true,\"deepCount\":" << v.deep_count
                  << ",\"pixelCount\":" << v.pixel_count << ",\"rejected\":" << v.rejected
                  << ",\"rejectionReason\":" << (v.rejected ? "\"blueNoDeepSupport\"" : "null") << '}';
    } else std::cout << "null";
    std::cout << ",\"source\":\"" << c.source << "\",\"score\":" << c.score << ",\"eligible\":" << c.eligible << ",\"endpoints\":";
    endpoints(c.endpoints);
    auto box = c.bounding_box;
    std::cout << ",\"bounding_box\":{\"min_x\":" << box.min_x << ",\"min_y\":" << box.min_y << ",\"max_x\":" << box.max_x << ",\"max_y\":" << box.max_y << '}';
    std::cout << ",\"peak_value\":" << c.peak_value << ",\"mean_value\":" << c.mean_value
        << ",\"high_value_ratio\":" << c.high_value_ratio << ",\"color_purity\":" << c.color_purity
        << ",\"local_contrast\":" << c.local_contrast << ",\"core_support\":" << c.core_support
        << ",\"longitudinal_core_coverage\":" << c.longitudinal_core_coverage
        << ",\"longitudinal_continuity\":" << c.longitudinal_continuity
        << ",\"largest_longitudinal_gap\":" << c.largest_longitudinal_gap
        << ",\"retained_body_ratio\":" << c.retained_body_ratio << ",\"raw_pca_span\":" << c.raw_pca_span
        << ",\"robust_body_length\":" << c.robust_body_length << ",\"axial_density\":" << c.axial_density
        << ",\"point_count\":" << c.point_count << ",\"used_point_led_fallback\":" << c.used_point_led_fallback << '}';
}
static void analysis_json(const FrameAnalysis& a, int frame) {
    std::cout << "{\"frame\":" << frame << ",\"colors\":{";
    for (int color = 0; color < 2; ++color) {
        if (color) std::cout << ',';
        std::cout << (color == 0 ? "\"red\":" : "\"blue\":") << "{\"selected\":";
        if (a.selected[color]) endpoints(*a.selected[color]); else std::cout << "null";
        std::cout << ",\"candidates\":[";
        for (std::size_t i = 0; i < a.candidates[color].size(); ++i) { if (i) std::cout << ','; candidate(a.candidates[color][i]); }
        std::cout << "]}";
    }
    std::cout << "}}\n";
}
int main(int argc, char** argv) {
    std::cout.imbue(std::locale::classic()); std::cout << std::boolalpha << std::setprecision(17);
    try {
        if (argc >= 2 && std::string(argv[1]) == "--raw") {
            if (argc != 7) throw std::runtime_error("usage: --raw WIDTH HEIGHT STEP rgba|bgra STRIDE");
            int w = std::stoi(argv[2]), h = std::stoi(argv[3]), step = std::stoi(argv[4]), stride = std::stoi(argv[6]);
            if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || stride < w*4 || std::size_t(stride)*h > 400000000) throw std::runtime_error("invalid raw dimensions");
            std::string format = argv[5];
            if (format != "rgba" && format != "bgra") throw std::runtime_error("invalid pixel format");
            std::vector<uint8_t> bytes(std::size_t(stride)*h);
            int frame = 0;
            while (std::cin.read(reinterpret_cast<char*>(bytes.data()),bytes.size())) {
                analysis_json(analyze({bytes.data(),w,h,std::size_t(stride),bytes.size(),format == "bgra" ? PixelFormat::bgra : PixelFormat::rgba},{},{},step),frame++);
            }
            if (std::cin.gcount() != 0) throw std::runtime_error("truncated raw frame");
        } else if (argc == 3 && std::string(argv[1]) == "--decode-bgra") {
            auto image = read_png(argv[2]);
            for (std::size_t i = 0; i < image.rgba.size(); i += 4) std::swap(image.rgba[i],image.rgba[i+2]);
            std::cout.write(reinterpret_cast<const char*>(image.rgba.data()),image.rgba.size());
        } else {
            if (argc < 2) throw std::runtime_error("usage: phonesaber-png PNG [PNG ...] (step=2)");
            for (int i = 1; i < argc; ++i) {
                auto image = read_png(argv[i]);
                analysis_json(analyze({image.rgba.data(),image.width,image.height,std::size_t(image.width)*4,image.rgba.size(),PixelFormat::rgba}),i-1);
            }
        }
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 2; }
}
