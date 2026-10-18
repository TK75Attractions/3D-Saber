#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace phonesaber {
struct PixelPoint { int x = 0; int y = 0; };
using Endpoints = std::pair<PixelPoint, PixelPoint>;
enum class SaberColor { red = 0, blue = 1 };
enum class PixelFormat { bgra, rgba };
struct ColorThreshold {
    uint8_t brightness = 145, dominance = 25, saturation = 30;
};
// Pixels are already oriented like the iPhone's portrait capture buffer:
// x right, y down. Alpha is ignored. The caller owns storage during analyze().
struct PixelBuffer {
    const uint8_t* data = nullptr;
    int width = 0, height = 0;
    std::size_t row_stride = 0, size_bytes = 0;
    PixelFormat format = PixelFormat::rgba;
};
struct BoundingBox { int min_x = 0, min_y = 0, max_x = 0, max_y = 0; };
struct WarmNoDeepRed {
    int deep_count = 0, warm_count = 0, pixel_count = 0;
    double warm_fraction = 0;
    bool rejected = false;
};
struct BlueNoDeepSupport {
    int deep_count = 0, pixel_count = 0;
    bool rejected = false;
};
struct ScoreBreakdown {
    double proposal_penalty = 0, radiance = 0, length = 0, aspect = 0, extent = 0;
    double width_consistency = 0, area = 0, peak_brightness = 0, mean_brightness = 0;
    double high_brightness_ratio = 0, color_purity = 0, local_contrast = 0;
    double emitter_texture = 0, clipped_white = 0, longitudinal_high_coverage = 0;
    double core_support = 0, longitudinal_core_coverage = 0;
    double total() const;
};
// 2026-10-11 採用検証用の仮値。ビルド入口を問わず既定 ON、比較時だけ無効化。
struct CoreLineGuard {
#ifdef PHONESABER_DISABLE_CORELINE_GUARD
    static constexpr bool enabled = false;
#else
    static constexpr bool enabled = true;
#endif
    static constexpr double line_coverage = 0.60, neighbor_core = 0.40;
    static constexpr double neighbor_high_value = 0.35, neighbor_coverage = 0.90;
    static constexpr double endpoint_distance_px = 30.0;
};
struct CoreLineGuardVerdict {
    int supporting_index = 0;
    std::string supporting_source;
    Endpoints supporting_endpoints{}; // 元画像 px。index は現 frame の score 順のみ。
    double lit_coverage = 0, supporting_lit_coverage = 0;
};
struct Candidate {
    std::string source = "color-mask";
    double radiance = 0;
    Endpoints comparison_endpoints{}, endpoints{};
    BoundingBox bounding_box{};
    double score = 0;
    ScoreBreakdown score_breakdown{};
    bool eligible = false, compact_red = false;
    int peak_value = 0;
    double mean_value = 0, high_value_ratio = 0, color_purity = 0, clipped_white_ratio = 0;
    double brightness_variation = 0, local_contrast = 0, longitudinal_high_coverage = 0;
    double width_variation = 0, core_support = 0, longitudinal_core_coverage = 0;
    double longitudinal_continuity = 0;
    int largest_longitudinal_gap = 0;
    double retained_body_ratio = 0, raw_pca_span = 0;
    std::optional<Endpoints> robust_interval_endpoints;
    double robust_body_length = 0, axial_density = 0;
    int component_area = 0, point_count = 0;
    bool used_point_led_fallback = false;
    std::optional<CoreLineGuardVerdict> core_line_guard;
    std::optional<WarmNoDeepRed> warm_no_deep_red;
    std::optional<BlueNoDeepSupport> blue_no_deep_support;
};
struct FrameAnalysis {
    // Fixed red/blue order, including when a color is absent.
    std::array<std::vector<Candidate>, 2> candidates;
    std::array<std::optional<Endpoints>, 2> selected;
    // 選ばれた候補の範囲（解析フレームの px）。成分の bounding_box と両端を囲む箱。
    // 無視範囲（IgnoreZone）を作るためだけに使い、選択・送信には使わない。
    std::array<std::optional<BoundingBox>, 2> selected_extent;
};
// 会場に置かれたままの赤・青の物（コーンなど）を選ばないための範囲。iPhone の SaberIgnoreZone
// （7c5a1c1）と同じ意味。2026-10-18: 台B（AQUOS）で背後の赤いコーンがほぼ毎コマ赤の winner になり、
// 本物の剣が送られなかった。選ぶ前に外すので、同じコマで次の eligible 候補（剣）が選ばれる。
// 座標: analyze() に渡した PixelBuffer（解析フレーム）を 0..1 にしたもの。x = px / width,
// y = px / height（Candidate.endpoints と同じ空間）。Android では JNI が縦向きへ回転した後で、
// payload の 1920x1080 への拡大・左右/上下反転より前。反転設定を変えても範囲は動かない。
struct IgnoreZone {
    double min_x = 0, min_y = 0, max_x = 0, max_y = 0;
    static constexpr double margin = 0.04, minimum_size = 0.06;
    bool contains(PixelPoint point, int width, int height) const;
};
// 色ごと（red=0, blue=1）。範囲が無ければ選び方は従来と bit 単位で同じ。
using IgnoreZones = std::array<std::vector<IgnoreZone>, 2>;
// 両端とも同じ範囲に入る候補は使わない（剣がその範囲を横切るだけなら使う）。
bool saber_ignored(const Endpoints& endpoints, int width, int height, const std::vector<IgnoreZone>& zones);
// 選ばれた候補の範囲（selected_extent）を、各辺 margin 広げ、最小 minimum_size にした範囲。
IgnoreZone ignore_zone_around(const BoundingBox& extent, int width, int height);
FrameAnalysis analyze(const PixelBuffer& pixels, ColorThreshold red = {},
                      ColorThreshold blue = {}, int sample_step = 2,
                      const IgnoreZones& ignore_zones = {});
std::optional<Endpoints> principal_axis_endpoints(const std::vector<PixelPoint>& points);
PixelPoint scaled_point(PixelPoint point, int source_width, int source_height,
                        int output_width, int output_height, bool mirror_x, bool mirror_y);
std::string payload(Endpoints endpoints, int source_width, int source_height,
                    int output_width = 1920, int output_height = 1080,
                    bool mirror_x = false, bool mirror_y = false);
std::string timestamped_payload(const std::string& coordinates, double epoch_seconds);
struct OutputConfig {
    int width = 1920, height = 1080;
    bool mirror_x = false, mirror_y = false, measurement_mode = false;
};
struct FrameResult {
    SaberColor color;
    Endpoints endpoints;
    bool fresh = false, predicted = false;
    int port = 0;
    // Absent for preview-only held endpoints; no UDP datagram should be sent.
    std::optional<std::string> text;
};
// One instance per camera session, called serially. Monotonic processing time
// and send-time Unix epoch are supplied by the platform, never read by the core.
class FrameProcessor {
public:
    void reset();
    std::vector<FrameResult> process(const FrameAnalysis& analysis, int width, int height,
                                   double processing_time, const OutputConfig& output = {},
                                   const std::array<double, 2>& send_epochs = {});
    std::vector<FrameResult> process(const PixelBuffer& pixels, double processing_time,
                                   const OutputConfig& output = {},
                                   const std::array<double, 2>& send_epochs = {},
                                   ColorThreshold red = {}, ColorThreshold blue = {},
                                   const IgnoreZones& ignore_zones = {});
    // Call at the scheduled lastSeen + 0.18 deadline (iPhone expiry callback).
    // Returned results are preview-only and never contain datagrams.
    std::vector<FrameResult> expire(double now);
    std::optional<double> next_expiry() const;
private:
    struct Track {
        std::optional<Endpoints> endpoints, previous;
        double last_seen = 0;
        int missing = 0;
    };
    std::array<Track, 2> tracks_{};
    int width_ = 0, height_ = 0;
};
} // namespace phonesaber
