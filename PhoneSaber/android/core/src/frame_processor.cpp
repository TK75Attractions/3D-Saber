#include "internal.hpp"
#include <iomanip>
#include <locale>
#include <sstream>

namespace phonesaber {
PixelPoint scaled_point(PixelPoint p, int sw, int sh, int ow, int oh, bool mx, bool my) {
    double x = double(p.x)/double(std::max(sw-1,1))*double(std::max(ow-1,0));
    double y = double(p.y)/double(std::max(sh-1,1))*double(std::max(oh-1,0));
    return {detail::rounded(mx ? double(ow-1)-x : x), detail::rounded(my ? double(oh-1)-y : y)};
}
std::string payload(Endpoints e, int sw, int sh, int ow, int oh, bool mx, bool my) {
    auto a = scaled_point(e.first,sw,sh,ow,oh,mx,my), b = scaled_point(e.second,sw,sh,ow,oh,mx,my);
    return std::to_string(a.x)+","+std::to_string(a.y)+","+std::to_string(b.x)+","+std::to_string(b.y);
}
std::string timestamped_payload(const std::string& coordinates, double epoch) {
    // Classic locale matches the Swift reference's decimal point regardless of
    // the host's locale. Fixed precision matches String(format: "%.6f", epoch).
    std::ostringstream out;
    out.imbue(std::locale::classic());
    out << "ts=" << std::fixed << std::setprecision(6) << epoch << ";" << coordinates;
    return out.str();
}
void FrameProcessor::reset() { tracks_ = {}; track_lock_.reset(); width_ = 0; height_ = 0; }
static double distance(PixelPoint a, PixelPoint b) { return std::hypot(double(a.x-b.x),double(a.y-b.y)); }
std::vector<FrameResult> FrameProcessor::process(const FrameAnalysis& analysis, int width, int height,
                                               double now, const OutputConfig& config,
                                               const std::array<double,2>& epochs) {
    if (width_ != width || height_ != height) { tracks_ = {}; width_ = width; height_ = height; }
    std::vector<FrameResult> results;
    for (int color = 0; color < 2; ++color) {
        auto& track = tracks_[color];
        std::optional<Endpoints> endpoints;
        bool fresh = false, predicted = false;
        if (analysis.selected[color]) {
            Endpoints current = *analysis.selected[color];
            if (track.endpoints) {
                const auto& previous = *track.endpoints;
                double direct = distance(current.first,previous.first)+distance(current.second,previous.second);
                double reversed = distance(current.first,previous.second)+distance(current.second,previous.first);
                if (reversed < direct) std::swap(current.first,current.second);
            }
            track.previous = track.endpoints; track.endpoints = current;
            track.last_seen = now; track.missing = 0;
            endpoints = current; fresh = true;
        } else {
            ++track.missing;
            if (!analysis.track_lock_suppressed[color] && track.missing <= 3 && track.previous && track.endpoints) {
                auto extrapolate = [&](PixelPoint previous, PixelPoint latest) -> PixelPoint {
                    int x = latest.x+(latest.x-previous.x)*track.missing;
                    int y = latest.y+(latest.y-previous.y)*track.missing;
                    return {std::min(std::max(x,0),std::max(width-1,0)), std::min(std::max(y,0),std::max(height-1,0))};
                };
                endpoints = Endpoints{extrapolate(track.previous->first,track.endpoints->first),
                                      extrapolate(track.previous->second,track.endpoints->second)};
                fresh = true; predicted = true;
            } else if (track.endpoints && now-track.last_seen <= 0.18) endpoints = track.endpoints;
            else track = {};
        }
        if (!endpoints) continue;
        FrameResult result{static_cast<SaberColor>(color),*endpoints,fresh,predicted,color == 0 ? 5005 : 5006,std::nullopt};
        if (fresh) {
            auto coordinates = payload(*endpoints,width,height,config.width,config.height,config.mirror_x,config.mirror_y);
            result.text = config.measurement_mode ? timestamped_payload(coordinates,epochs[color]) : coordinates;
        }
        results.push_back(std::move(result));
    }
    return results;
}
std::vector<FrameResult> FrameProcessor::process(const PixelBuffer& p, double now, const OutputConfig& output,
                                               const std::array<double,2>& epochs, ColorThreshold red, ColorThreshold blue,
                                               const IgnoreZones& ignore_zones, const std::optional<PlayArea>& play_area, bool track_lock) {
    return process(analyze_frame(p,now,red,blue,ignore_zones,play_area,track_lock),p.width,p.height,now,output,epochs);
}
FrameAnalysis FrameProcessor::analyze_frame(const PixelBuffer& p, double now, ColorThreshold red,
                                             ColorThreshold blue, const IgnoreZones& zones,
                                             const std::optional<PlayArea>& area, bool track_lock) {
    if (!track_lock) track_lock_.reset();
    return analyze(p,red,blue,2,zones,area,track_lock ? &track_lock_ : nullptr,now);
}
std::vector<FrameResult> FrameProcessor::expire(double now) {
    std::vector<FrameResult> held;
    for (int color = 0; color < 2; ++color) {
        auto& t = tracks_[color];
        if (t.endpoints && now >= t.last_seen+0.18) t = {};
        if (t.endpoints) held.push_back({static_cast<SaberColor>(color),*t.endpoints,false,false,color == 0 ? 5005 : 5006,std::nullopt});
    }
    return held;
}
std::optional<double> FrameProcessor::next_expiry() const {
    std::optional<double> earliest;
    for (const auto& t : tracks_) if (t.endpoints && (!earliest || t.last_seen+0.18 < *earliest)) earliest = t.last_seen+0.18;
    return earliest;
}
} // namespace phonesaber
