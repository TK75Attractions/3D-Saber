// 一時コピーの pipeline.cpp にだけ挿入する観測器。本番の判定には使わない。
#include <iostream>
#include <iomanip>
#include "internal.hpp"

namespace phonesaber::detail {
static void shadow_support(const Scored& scored, const Evidence& e, int w, int step) {
    const auto& points = scored.support_points;
    double mx = 0, my = 0, xx = 0, yy = 0, xy = 0;
    for (auto p : points) { mx += p.x; my += p.y; }
    mx /= points.size(); my /= points.size();
    for (auto p : points) {
        double dx = p.x-mx, dy = p.y-my;
        xx += dx*dx; yy += dy*dy; xy += dx*dy;
    }
    double angle = 0.5*std::atan2(2*xy,xx-yy), ax = std::cos(angle), ay = std::sin(angle);
    std::vector<double> projections;
    for (auto p : points) projections.push_back((p.x-mx)*ax+(p.y-my)*ay);
    double lo = *std::min_element(projections.begin(),projections.end());
    double hi = *std::max_element(projections.begin(),projections.end());
    int bins = std::max(1,int(std::ceil(hi-lo))+1), core_count = 0, lit_count = 0;
    std::vector<int> core(bins), lit(bins);
    for (std::size_t i = 0; i < points.size(); ++i) {
        auto p = points[i]; int index = p.y*w+p.x;
        int bin = std::min(bins-1,std::max(0,rounded(projections[i]-lo)));
        if (e.core_mask[index]) { ++core[bin]; ++core_count; }
        if (e.core_mask[index] || e.color_mask[index]) { ++lit[bin]; ++lit_count; }
    }
    auto coverage = [bins](const std::vector<int>& v) {
        return double(std::count_if(v.begin(),v.end(),[](int n){return n>0;}))/bins;
    };
    auto gap = [](const std::vector<int>& v) {
        int run = 0, largest = 0;
        for (int n : v) { run = n ? 0 : run+1; largest = std::max(largest,run); }
        return largest;
    };
    // stderr の順序は色ごとの score ソート済み候補順。stdout は変更しない。
    std::cerr << std::setprecision(17) << "{\"color\":\""
        << (e.color == SaberColor::red ? "red" : "blue")
        << "\",\"grid_pixels\":" << bins << ",\"core_pixels\":" << core_count
        << ",\"lit_pixels\":" << lit_count
        << ",\"core_coverage\":" << coverage(core)
        << ",\"lit_coverage\":" << coverage(lit)
        << ",\"core_gap_px\":" << gap(core)*step
        << ",\"lit_gap_px\":" << gap(lit)*step
        << ",\"span_per_core\":" << (hi-lo+1)/std::max(core_count,1)
        << ",\"span_px\":" << (hi-lo+1)*step << "}\n";
}
} // namespace phonesaber::detail
