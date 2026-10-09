// Production path from BGRADetection.swift; no diagnostic observers/profiling.
#include <climits>
#include "internal.hpp"

namespace phonesaber::detail {
struct HSV { double hue, saturation, value, chroma; };
static HSV hsv(int r, int g, int b) {
    double rd = r, gd = g, bd = b;
    double maximum = std::max(rd,std::max(gd,bd)), minimum = std::min(rd,std::min(gd,bd));
    double delta = maximum-minimum, hue = 0;
    if (delta != 0) {
        if (maximum == rd) hue = 60.0*std::fmod((gd-bd)/delta,6.0);
        else if (maximum == gd) hue = 60.0*((bd-rd)/delta+2.0);
        else hue = 60.0*((rd-gd)/delta+4.0);
    }
    return {hue < 0 ? hue+360.0 : hue,maximum > 0 ? delta*255.0/maximum : 0,maximum,delta};
}
static bool strict(HSV hsv, SaberColor color, ColorThreshold t) {
    if (hsv.value < double(t.brightness) || hsv.chroma < double(t.dominance) || hsv.saturation < double(t.saturation)) return false;
    return color == SaberColor::red ? hsv.hue >= 340.0 || hsv.hue <= 20.0 : hsv.hue >= 198.0 && hsv.hue <= 248.0;
}
static bool diffuser(int r, int g, int b, HSV hsv, ColorThreshold t) {
    int brightness = std::max(110,int(t.brightness)-25), dominance = std::max(8,int(t.dominance)-17);
    int green_dominance = std::max(2,dominance/4);
    return b >= brightness && b-r >= dominance && b-g >= green_dominance
        && hsv.saturation >= double(std::max(10,int(t.saturation)-20)) && hsv.hue >= 190.0 && hsv.hue <= 260.0;
}
static double point_distance(PixelPoint a, PixelPoint b) { return std::hypot(double(a.x-b.x),double(a.y-b.y)); }
static double axis_distance(const Candidate& a, const Candidate& b) {
    double forward = point_distance(a.comparison_endpoints.first,b.comparison_endpoints.first)
        + point_distance(a.comparison_endpoints.second,b.comparison_endpoints.second);
    double reverse = point_distance(a.comparison_endpoints.first,b.comparison_endpoints.second)
        + point_distance(a.comparison_endpoints.second,b.comparison_endpoints.first);
    return std::min(forward,reverse)/2.0;
}
static bool subsegment(const Candidate& short_c, const Candidate& long_c) {
    double dx = double(long_c.comparison_endpoints.second.x-long_c.comparison_endpoints.first.x);
    double dy = double(long_c.comparison_endpoints.second.y-long_c.comparison_endpoints.first.y);
    double length = std::hypot(dx,dy);
    double sx = double(short_c.comparison_endpoints.second.x-short_c.comparison_endpoints.first.x);
    double sy = double(short_c.comparison_endpoints.second.y-short_c.comparison_endpoints.first.y);
    double sl = std::hypot(sx,sy);
    if (length < sl*1.50 || sl <= 0) return false;
    double ax = dx/length, ay = dy/length, sax = sx/sl, say = sy/sl;
    if (std::abs(ax*sax+ay*say) < 0.94) return false;
    double nx = -ay, ny = ax;
    for (auto p : {short_c.comparison_endpoints.first,short_c.comparison_endpoints.second}) {
        double px = double(p.x-long_c.comparison_endpoints.first.x), py = double(p.y-long_c.comparison_endpoints.first.y);
        double along = px*ax+py*ay, across = std::abs(px*nx+py*ny);
        if (!(along >= -6.0 && along <= length+6.0 && across <= 6.0)) return false;
    }
    return true;
}
static bool sufficient_line(const Candidate& c) {
    return !line_source(c) || (c.color_purity >= 0.25 && c.retained_body_ratio >= 0.35
        && c.longitudinal_continuity >= 0.70 && c.high_value_ratio >= 0.35);
}
static bool independent_blue(const Candidate& c) {
    if (c.color_purity >= 0.25 && c.retained_body_ratio >= 0.35 && c.longitudinal_continuity >= 0.70 && c.high_value_ratio >= 0.35) return true;
    return c.source == "connected-core" && c.color_purity >= 0.13 && c.longitudinal_continuity >= 0.85
        && c.core_support >= 0.55 && c.high_value_ratio >= 0.75;
}
static double robust_penalty(const Candidate& c, int minimum, int dimension) {
    if (!line_source(c) || c.raw_pca_span <= 0) return 0;
    double span = c.raw_pca_span/double(std::max(dimension,1));
    double long_weight = clamp01((span-0.32)/0.10);
    double span_discard = std::max(0.0,1-c.robust_body_length/c.raw_pca_span);
    double point_discard = std::max(0.0,1-c.retained_body_ratio);
    double body_count = double(c.point_count)*c.retained_body_ratio;
    double support = std::min(body_count/double(std::max(minimum,1)),1.0);
    double continuity = clamp01(c.longitudinal_continuity), density = clamp01(c.axial_density/2.0);
    return 55.0*long_weight*span_discard*point_discard*support*continuity*density;
}
static bool trusted_blue(const Candidate& c, int dimension) {
    if (!c.eligible || c.longitudinal_continuity < 0.80) return false;
    if (c.source == "core-line") return c.retained_body_ratio >= 0.75 && c.high_value_ratio >= 0.70
        && c.color_purity >= 0.55 && c.core_support >= 0.30 && c.largest_longitudinal_gap <= 1;
    if (c.source == "core-halo") return c.longitudinal_continuity >= 0.90 && c.retained_body_ratio >= 0.95
        && c.longitudinal_core_coverage >= 0.95 && c.core_support >= 0.35
        && c.raw_pca_span/double(std::max(dimension,1)) >= 0.18;
    if ((c.source != "color-emitter" && c.source != "connected-core") || c.longitudinal_continuity < 0.85
        || c.core_support < 0.55 || c.high_value_ratio < 0.75) return false;
    return c.color_purity >= 0.35 || (c.source == "connected-core" && c.color_purity >= 0.13 && c.local_contrast >= 0.30);
}
static void prefer_trusted(std::vector<Scored>& candidates, int dimension) {
    std::optional<std::size_t> trusted;
    for (std::size_t i = 0; i < candidates.size(); ++i)
        if (trusted_blue(candidates[i].candidate,dimension)
            && (!trusted || candidates[*trusted].candidate.score < candidates[i].candidate.score)) trusted = i;
    if (!trusted) return;
    const Candidate& t = candidates[*trusted].candidate;
    for (std::size_t i = 0; i < candidates.size(); ++i) {
        auto& c = candidates[i].candidate;
        if (i == *trusted || !c.eligible || c.core_support >= 0.50 || c.high_value_ratio >= 0.80 || c.score < t.score) continue;
        bool long_weak = c.raw_pca_span/double(std::max(dimension,1)) >= 0.37;
        bool stronger = t.high_value_ratio >= c.high_value_ratio+0.20 && t.core_support >= c.core_support+0.10
            && t.color_purity >= c.color_purity+0.05;
        if (!long_weak && !stronger) continue;
        double penalty = c.score-t.score+1.0;
        c.score_breakdown.proposal_penalty -= penalty; c.score = c.score_breakdown.total();
    }
}
struct Peak { double ax, ay; int rho, votes; };
static std::vector<Points> core_lines(const Mask& core, const Mask& color, int w, int h) {
    Points points;
    for (int i = 0; i < int(core.size()); ++i) if (core[i]) points.push_back({i%w,i/w});
    if (points.size() < 4) return {};
    int diagonal = int(std::ceil(std::hypot(double(w),double(h))));
    int minimum_votes = std::max(4,int(double(std::min(w,h))*0.025));
    std::vector<Peak> peaks;
    std::vector<int> votes(diagonal*2+1);
    constexpr double pi = 0x1.921fb54442d18p+1;
    for (int degrees = 0; degrees < 180; degrees += 10) {
        double radians = double(degrees)*pi/180.0;
        double ax = std::cos(radians), ay = std::sin(radians), nx = -ay, ny = ax;
        std::fill(votes.begin(),votes.end(),0);
        for (auto p : points) ++votes[rounded(double(p.x)*nx+double(p.y)*ny)+diagonal];
        for (int i = 0; i < int(votes.size()); ++i) if (votes[i] >= minimum_votes) peaks.push_back({ax,ay,i-diagonal,votes[i]});
    }
    std::stable_sort(peaks.begin(),peaks.end(),[](const Peak& a,const Peak& b){return a.votes>b.votes;});
    std::vector<Points> proposals;
    std::vector<Peak> accepted;
    Points neighborhood, inliers;
    neighborhood.reserve(points.size()); inliers.reserve(points.size());
    int examined = 0;
    for (auto initial : peaks) {
        if (proposals.size() >= 18 || examined >= 180) break;
        ++examined;
        neighborhood.clear();
        for (auto p : points) if (std::abs(double(p.x)*-initial.ay+double(p.y)*initial.ax-double(initial.rho)) <= 6) neighborhood.push_back(p);
        if (int(neighborhood.size()) < minimum_votes) continue;
        double mx = 0, my = 0;
        for (auto p : neighborhood) mx += double(p.x);
        for (auto p : neighborhood) my += double(p.y);
        mx /= double(neighborhood.size()); my /= double(neighborhood.size());
        double xx = 0, yy = 0, xy = 0;
        for (auto p : neighborhood) {
            double dx = double(p.x)-mx, dy = double(p.y)-my;
            xx += dx*dx; yy += dy*dy; xy += dx*dy;
        }
        double angle = 0.5*std::atan2(2*xy,xx-yy), ax = std::cos(angle), ay = std::sin(angle);
        Peak peak{ax,ay,rounded(-ay*mx+ax*my),initial.votes};
        if (std::any_of(accepted.begin(),accepted.end(),[&](const Peak& p){return std::abs(p.rho-peak.rho)<=3 && std::abs(p.ax*peak.ay-p.ay*peak.ax)<0.18;})) continue;
        double nx = -peak.ay, ny = peak.ax;
        inliers.clear();
        for (auto p : points) if (std::abs(double(p.x)*nx+double(p.y)*ny-double(peak.rho)) <= 2.0) inliers.push_back(p);
        if (int(inliers.size()) < minimum_votes) continue;
        double min_t = std::numeric_limits<double>::max(), max_t = -min_t;
        for (auto p : inliers) {
            double t = double(p.x)*peak.ax+double(p.y)*peak.ay;
            min_t = std::min(min_t,t); max_t = std::max(max_t,t);
        }
        if (max_t-min_t < std::max(double(std::min(w,h))*0.06,24.0)) continue;
        int nearby = 0;
        for (auto p : points) {
            double t = double(p.x)*peak.ax+double(p.y)*peak.ay;
            double distance = std::abs(double(p.x)*nx+double(p.y)*ny-double(peak.rho));
            if (t >= min_t && t <= max_t && distance <= 8.0) ++nearby;
        }
        if (double(inliers.size())/double(std::max(nearby,1)) < 0.28) continue;
        accepted.push_back(peak);
        int min_x = w, min_y = h, max_x = 0, max_y = 0;
        for (auto p : inliers) {
            min_x = std::min(min_x,p.x); min_y = std::min(min_y,p.y);
            max_x = std::max(max_x,p.x); max_y = std::max(max_y,p.y);
        }
        min_x = std::max(0,min_x-4); max_x = std::min(w-1,max_x+4);
        min_y = std::max(0,min_y-4); max_y = std::min(h-1,max_y+4);
        Points proposal;
        for (int y = min_y; y <= max_y; ++y) for (int x = min_x; x <= max_x; ++x) {
            // 非支持画素は候補に入らないので、投影の前に整数 mask で除外する。
            int index = y*w+x;
            if (!core[index] && !color[index]) continue;
            double t = double(x)*peak.ax+double(y)*peak.ay;
            if (t < min_t-1 || t > max_t+1) continue;
            double distance = std::abs(double(x)*nx+double(y)*ny-double(peak.rho));
            if (distance <= 3.0) proposal.push_back({x,y});
        }
        double t = min_t;
        while (t <= max_t) {
            int x = rounded(nx*double(peak.rho)+peak.ax*t), y = rounded(ny*double(peak.rho)+peak.ay*t);
            if (x >= 0 && x < w && y >= 0 && y < h) proposal.push_back({x,y});
            t += 0.75;
        }
        proposals.push_back(std::move(proposal));
    }
    return proposals;
}
// dilate1 領域の重複判定。bounding box 上の bitmap(std::set と同じ判定・同じ走査順で、速い)。
struct Dilate1Visited {
    int x0 = 0, y0 = 0, w = 0;
    std::vector<unsigned char> bits;
    Dilate1Visited(const Points& points, const PixelBuffer& p, int step) {
        int min_x = INT_MAX, min_y = INT_MAX, max_x = INT_MIN, max_y = INT_MIN;
        for (auto point : points) {
            int x = point.x*step, y = point.y*step;
            if (x < 0 || x >= p.width || y < 0 || y >= p.height) continue;
            min_x = std::min(min_x,x); max_x = std::max(max_x,x); min_y = std::min(min_y,y); max_y = std::max(max_y,y);
        }
        if (min_x > max_x) return;
        x0 = std::max(0,min_x-1); y0 = std::max(0,min_y-1);
        w = std::min(p.width-1,max_x+1)-x0+1;
        int h = std::min(p.height-1,max_y+1)-y0+1;
        bits.assign(std::size_t(w)*std::size_t(h),0);
    }
    bool insert(int x, int y) {
        auto& bit = bits[std::size_t(y-y0)*std::size_t(w)+std::size_t(x-x0)];
        if (bit) return false;
        bit = 1;
        return true;
    }
};
static WarmNoDeepRed warm_support(const Points& points, const PixelBuffer& p, int step) {
    Dilate1Visited seen(points,p,step);
    WarmNoDeepRed v;
    int red_offset = p.format == PixelFormat::bgra ? 2 : 0, blue_offset = 2-red_offset;
    for (auto point : points) {
        int x = point.x*step, y = point.y*step;
        if (x < 0 || x >= p.width || y < 0 || y >= p.height) continue;
        for (int yy = std::max(0,y-1); yy <= std::min(p.height-1,y+1); ++yy)
            for (int xx = std::max(0,x-1); xx <= std::min(p.width-1,x+1); ++xx) {
                if (!seen.insert(xx,yy)) continue;
                const auto* pixel = p.data+std::size_t(yy)*p.row_stride+xx*4;
                int r = pixel[red_offset], g = pixel[1], b = pixel[blue_offset];
                ++v.pixel_count;
                if (r >= 180 && g*100 < 40*r && b*100 < 65*r) ++v.deep_count;
                if (r >= 120 && g*100 >= 45*r && g*100 <= 92*r && b*100 <= 95*g) ++v.warm_count;
            }
    }
    v.warm_fraction = v.pixel_count > 0 ? double(v.warm_count)/double(v.pixel_count) : 0;
    v.rejected = v.deep_count == 0 && v.pixel_count > 0 && int64_t(v.warm_count)*100 >= int64_t(v.pixel_count)*30;
    return v;
}
// 赤と同じ元画像 dilate1 領域。重複判定は Dilate1Visited。
static BlueNoDeepSupport blue_support(const Points& points, const PixelBuffer& p, int step) {
    Dilate1Visited seen(points,p,step);
    BlueNoDeepSupport v;
    int red_offset = p.format == PixelFormat::bgra ? 2 : 0, blue_offset = 2-red_offset;
    for (auto point : points) {
        int x = point.x*step, y = point.y*step;
        if (x < 0 || x >= p.width || y < 0 || y >= p.height) continue;
        for (int yy = std::max(0,y-1); yy <= std::min(p.height-1,y+1); ++yy)
            for (int xx = std::max(0,x-1); xx <= std::min(p.width-1,x+1); ++xx) {
                if (!seen.insert(xx,yy)) continue;
                const auto* pixel = p.data+std::size_t(yy)*p.row_stride+xx*4;
                int r = pixel[red_offset], g = pixel[1], b = pixel[blue_offset];
                ++v.pixel_count;
                if (b >= 180 && r*100 < 40*b && g*100 < 65*b) ++v.deep_count;
            }
    }
    v.rejected = v.deep_count == 0;
    return v;
}
static bool any(const Mask& mask) { return std::find(mask.begin(),mask.end(),1) != mask.end(); }
} // namespace phonesaber::detail

namespace phonesaber {
FrameAnalysis analyze(const PixelBuffer& p, ColorThreshold red, ColorThreshold blue, int sample_step) {
    using namespace detail;
    FrameAnalysis analysis;
    // Reject invalid buffers without reading them. Limit dimensions so Swift Int
    // products and C++ indices cannot overflow for any accepted camera buffer.
    if (!p.data || p.width <= 0 || p.height <= 0 || p.width > 32768 || p.height > 32768
        || p.row_stride < std::size_t(p.width)*4 || p.row_stride > p.size_bytes/std::size_t(p.height)) return analysis;
    int step = std::max(sample_step,1);
    int w = (p.width-1)/step+1, h = (p.height-1)/step+1;
    std::array<Mask,2> masks{Mask(w*h),Mask(w*h)}, emitters{Mask(w*h),Mask(w*h)};
    Mask relaxed(w*h), bright(w*h), value(w*h), chroma(w*h), radiance(w*h);
    int red_offset = p.format == PixelFormat::bgra ? 2 : 0, blue_offset = 2-red_offset;
    int diffuser_brightness = std::max(110,int(blue.brightness)-25);
    int diffuser_dominance = std::max(8,int(blue.dominance)-17);
    int diffuser_green_dominance = std::max(2,diffuser_dominance/4);
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        const auto* pixel = p.data+std::size_t(y*step)*p.row_stride+x*step*4;
        int r = pixel[red_offset], g = pixel[1], b = pixel[blue_offset], i = y*w+x;
        // 整数の明度・chroma は丸めた HSV 値と同じ。実際の述語の先頭条件で hue 計算を絞る。
        int maximum = std::max(r,std::max(g,b)), minimum = std::min(r,std::min(g,b));
        int delta = maximum-minimum, second = r+g+b-maximum-minimum;
        radiance[i] = uint8_t(second); value[i] = uint8_t(maximum); chroma[i] = uint8_t(delta);
        if (maximum >= 235 && second >= 100) bright[i] = 1;
        bool possible_red = maximum >= red.brightness && delta >= red.dominance;
        bool possible_blue = maximum >= blue.brightness && delta >= blue.dominance;
        bool possible_diffuser = b >= diffuser_brightness && b-r >= diffuser_dominance
            && b-g >= diffuser_green_dominance;
        if (!possible_red && !possible_blue && !possible_diffuser) continue;
        auto hsv_value = hsv(r,g,b);
        bool emitter = hsv_value.value >= 215 && hsv_value.chroma/std::max(hsv_value.value,1.0) >= 0.35;
        if (possible_red && strict(hsv_value,SaberColor::red,red)) { masks[0][i] = 1; if (emitter) emitters[0][i] = 1; }
        if (possible_blue && strict(hsv_value,SaberColor::blue,blue)) { masks[1][i] = 1; if (emitter) emitters[1][i] = 1; }
        if (possible_diffuser && diffuser(r,g,b,hsv_value,blue)) relaxed[i] = 1;
    }
    Mask support = dilate(masks[1],w,h,2);
    for (int i = 0; i < w*h; ++i) if (relaxed[i] && support[i]) masks[1][i] = 1;
    int close_radius = std::min(w,h) >= 16 ? 2 : 1;
    for (int color_index = 0; color_index < 2; ++color_index) {
        const Mask& raw = masks[color_index]; const Mask& emitter = emitters[color_index];
        if (!any(raw)) continue;
        SaberColor color = static_cast<SaberColor>(color_index);
        Mask neighborhood = dilate(raw,w,h,2), core(w*h);
        for (int i = 0; i < w*h; ++i) if (bright[i] && neighborhood[i]) core[i] = 1;
        Mask core_neighborhood = dilate(core,w,h,3), halo = core;
        for (int i = 0; i < w*h; ++i) if (raw[i] && core_neighborhood[i]) halo[i] = 1;
        Mask closed = close(raw,w,h,close_radius), cleaned = dilate(erode(closed,w,h,1),w,h,1);
        Evidence evidence{color,radiance,value,chroma,raw,core};
        int morphology_count = 0;
        auto candidates = components(cleaned,w,h,evidence,&morphology_count);
        auto duplicate = [&](const Candidate& c, double distance) {
            return std::any_of(candidates.begin(),candidates.end(),[&](const Scored& existing){return axis_distance(existing.candidate,c) <= distance;});
        };
        auto add_unique = [&](std::vector<Scored> added, const std::string& source) {
            for (auto& c : added) if (!duplicate(c.candidate,3.0)) { c.candidate.source = source; candidates.push_back(std::move(c)); }
        };
        add_unique(components(closed,w,h,evidence),"color-close");
        int standard = std::max(4,int(double(w*h)*0.0005)), sparse = std::max(6,standard/3);
        int mask_count = int(std::count(raw.begin(),raw.end(),1));
        if (mask_count >= sparse && int64_t(morphology_count)*5 < int64_t(mask_count)*3) {
            for (auto& c : components(raw,w,h,evidence,nullptr,sparse)) {
                c.candidate.source = "color-sparse-raw";
                auto existing = std::find_if(candidates.begin(),candidates.end(),[&](const Scored& e){return axis_distance(e.candidate,c.candidate) <= 3.0;});
                if (existing == candidates.end()) candidates.push_back(std::move(c));
                else if (c.candidate.eligible && !existing->candidate.eligible) *existing = std::move(c);
            }
        }
        if (any(emitter)) add_unique(components(close(emitter,w,h,close_radius),w,h,evidence),"color-emitter");
        if (any(core)) {
            add_unique(components(close(halo,w,h,close_radius),w,h,evidence),"core-halo");
            for (auto& c : components(close(core,w,h,4),w,h,evidence)) {
                double length = point_distance(c.candidate.comparison_endpoints.second,c.candidate.comparison_endpoints.first);
                if (length < std::max(12.0,double(std::min(w,h))*0.10)) continue;
                c.candidate.source = "connected-core"; candidates.push_back(std::move(c));
            }
        }
        auto proposals = core_lines(core,raw,w,h);
        // 採点だけが読む bitmap。候補間で使用した位置だけ消し、全画像の再確保を避ける。
        Mask proposal_mask;
        if (!proposals.empty()) proposal_mask.resize(w*h);
        for (const auto& proposal : proposals) {
            // Swift Set is only used for membership. Its scoring order is sorted row-major.
            std::vector<int> indices;
            indices.reserve(proposal.size());
            for (auto point : proposal) if (point.x >= 0 && point.x < w && point.y >= 0 && point.y < h) indices.push_back(point.y*w+point.x);
            std::sort(indices.begin(),indices.end());
            indices.erase(std::unique(indices.begin(),indices.end()),indices.end());
            Points unique;
            unique.reserve(indices.size());
            for (int index : indices) { unique.push_back({index%w,index/w}); proposal_mask[index] = 1; }
            auto scored = score_component(unique,w,h,&proposal_mask,evidence,"core-line");
            for (int index : indices) proposal_mask[index] = 0;
            if (!scored) continue;
            auto& c = scored->candidate;
            if (std::any_of(candidates.begin(),candidates.end(),[&](const Scored& e){return axis_distance(e.candidate,c)<=10.0 || subsegment(c,e.candidate);})) continue;
            c.score_breakdown.proposal_penalty -= (1.0-c.longitudinal_core_coverage)*45.0;
            c.score_breakdown.proposal_penalty -= robust_penalty(c,standard,std::min(w,h));
            c.score = c.score_breakdown.total();
            if (!sufficient_line(c)) { c.eligible = false; c.source = "core-line-low-confidence"; }
            else if (c.longitudinal_core_coverage < 0.30 && c.core_support < 0.18) { c.eligible = false; c.source = "core-line-sparse"; }
            bool overlaps = std::any_of(candidates.begin(),candidates.end(),[&](const Scored& e){
                const auto& existing = e.candidate;
                if (existing.source != "connected-core") return false;
                auto a = existing.comparison_endpoints.first, b = existing.comparison_endpoints.second;
                double dx = double(b.x-a.x), dy = double(b.y-a.y), length = std::hypot(dx,dy);
                double cx = double(c.comparison_endpoints.first.x+c.comparison_endpoints.second.x)/2-double(a.x);
                double cy = double(c.comparison_endpoints.first.y+c.comparison_endpoints.second.y)/2-double(a.y);
                double along = (cx*dx+cy*dy)/length, across = std::abs(cx*dy-cy*dx)/length;
                return along >= -4 && along <= length+4 && across <= 8;
            });
            if (overlaps) {
                c.eligible = false; c.source = "core-line-overlap";
                c.score_breakdown.proposal_penalty = -c.score*0.5; c.score = c.score_breakdown.total();
            }
            candidates.push_back(std::move(*scored));
        }
        if (color == SaberColor::blue) {
            prefer_trusted(candidates,std::min(w,h));
            std::optional<double> rejected_score, eligible_score;
            bool has_trusted = false;
            for (const auto& scored : candidates) {
                const auto& c = scored.candidate;
                if (c.source == "core-line-low-confidence" && (!rejected_score || c.score > *rejected_score)) rejected_score = c.score;
                if (c.eligible && (!eligible_score || c.score > *eligible_score)) eligible_score = c.score;
                if (trusted_blue(c,std::min(w,h))) has_trusted = true;
            }
            if (rejected_score && eligible_score && *rejected_score >= *eligible_score && !has_trusted)
                for (auto& scored : candidates) if (scored.candidate.eligible && !independent_blue(scored.candidate)) {
                    scored.candidate.eligible = false; scored.candidate.source += "-unsupported-core-line-candidate";
                }
            for (auto& scored : candidates) {
                auto& c = scored.candidate;
                if (c.eligible && c.source == "color-emitter" && double(c.point_count)>double(w*h)*0.03
                    && c.core_support < 0.05 && c.axial_density > 8.0) {
                    c.eligible = false; c.source = "color-emitter-broad-coreless";
                }
            }
            for (std::size_t i = 0; i < candidates.size(); ++i) {
                const Candidate& line = candidates[i].candidate;
                if (!line.eligible || line.source != "core-line" || !line.used_point_led_fallback
                    || line.raw_pca_span < line.robust_body_length*4.0 || line.retained_body_ratio >= 0.50 || line.core_support >= 0.30) continue;
                bool local_body = std::any_of(candidates.begin(),candidates.end(),[&](const Scored& scored){
                    const auto& body = scored.candidate;
                    if (!body.eligible || body.core_support < line.core_support+0.10 || body.high_value_ratio < 0.35
                        || body.robust_body_length < line.robust_body_length*0.60) return false;
                    for (auto end : {line.endpoints.first,line.endpoints.second})
                        for (auto supported : {body.endpoints.first,body.endpoints.second}) if (point_distance(end,supported) <= 30.0) return true;
                    return false;
                });
                if (local_body) { candidates[i].candidate.eligible = false; candidates[i].candidate.source = "core-line-weak-raw-tail"; }
            }
        }
        if (color == SaberColor::red) {
            std::vector<Candidate> connected;
            for (const auto& c : candidates) if (c.candidate.eligible && !line_source(c.candidate)) connected.push_back(c.candidate);
            for (auto& scored : candidates) {
                auto& c = scored.candidate;
                if (!c.eligible) continue;
                if (c.source == "core-line" && c.raw_pca_span >= c.robust_body_length*1.8 && c.retained_body_ratio < 0.65
                    && std::any_of(connected.begin(),connected.end(),[&](const Candidate& b){return b.raw_pca_span >= c.robust_body_length*0.6
                        && b.color_purity >= c.color_purity-0.10 && b.high_value_ratio >= c.high_value_ratio-0.20
                        && b.longitudinal_continuity >= c.longitudinal_continuity && b.axial_density >= c.axial_density;})) {
                    c.eligible = false; c.source = "core-line-weak-bridge";
                } else if (c.source == "core-halo" && std::any_of(connected.begin(),connected.end(),[&](const Candidate& b){return b.source == "color-mask"
                    && b.raw_pca_span >= c.raw_pca_span*1.4 && b.high_value_ratio >= 0.50 && b.color_purity >= 0.50
                    && c.bounding_box.min_x >= b.bounding_box.min_x-4 && c.bounding_box.max_x <= b.bounding_box.max_x+4
                    && c.bounding_box.min_y >= b.bounding_box.min_y-4 && c.bounding_box.max_y <= b.bounding_box.max_y+4;})) {
                    c.eligible = false; c.source = "core-halo-short-subsegment";
                }
            }
        }
        // eligibility の snapshot は維持し、ここでは読まない支持点列を複製しない。
        std::vector<Candidate> complete;
        complete.reserve(candidates.size());
        for (const auto& scored : candidates) complete.push_back(scored.candidate);
        for (auto& scored : candidates) {
            auto& c = scored.candidate;
            if (!c.eligible || c.source == "connected-core") continue;
            if (color == SaberColor::blue && trusted_blue(c,std::min(w,h))) continue;
            if (std::any_of(complete.begin(),complete.end(),[&](const Candidate& e){return e.eligible && !line_source(e) && subsegment(c,e);})) {
                c.eligible = false; c.source += "-subsegment";
                c.score_breakdown.proposal_penalty = -c.score*0.5; c.score = c.score_breakdown.total();
            }
        }
        std::stable_sort(candidates.begin(),candidates.end(),[](const Scored& a,const Scored& b){return a.candidate.score>b.candidate.score;});
        for (auto& scored : candidates) {
            auto& c = scored.candidate;
            if (color == SaberColor::red && c.eligible) {
                c.warm_no_deep_red = warm_support(scored.support_points,p,step);
                if (c.warm_no_deep_red->rejected) c.eligible = false;
            }
            if (color == SaberColor::blue && c.eligible) {
                c.blue_no_deep_support = blue_support(scored.support_points,p,step);
                if (c.blue_no_deep_support->rejected) c.eligible = false;
            }
            auto scale = [step](PixelPoint p) -> PixelPoint { return {p.x*step,p.y*step}; };
            c.comparison_endpoints = {scale(c.comparison_endpoints.first),scale(c.comparison_endpoints.second)};
            c.endpoints = {scale(c.endpoints.first),scale(c.endpoints.second)};
            c.bounding_box.min_x *= step; c.bounding_box.min_y *= step; c.bounding_box.max_x *= step; c.bounding_box.max_y *= step;
            c.raw_pca_span *= double(step);
            if (c.robust_interval_endpoints) c.robust_interval_endpoints = Endpoints{scale(c.robust_interval_endpoints->first),scale(c.robust_interval_endpoints->second)};
            c.robust_body_length *= double(step); c.axial_density /= double(std::max(step,1));
            c.component_area = c.component_area*step*step;
            if (c.eligible && !analysis.selected[color_index]) analysis.selected[color_index] = c.endpoints;
            analysis.candidates[color_index].push_back(std::move(c));
        }
    }
    return analysis;
}
} // namespace phonesaber
