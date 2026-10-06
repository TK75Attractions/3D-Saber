// Direct C++17 translation of DetectionCore.swift's production component path.
#include "internal.hpp"
#include <numeric>

namespace phonesaber {
double ScoreBreakdown::total() const {
    return proposal_penalty + radiance + length + aspect + extent + width_consistency + area
        + peak_brightness + mean_brightness + high_brightness_ratio
        + color_purity + local_contrast + emitter_texture + clipped_white
        + longitudinal_high_coverage + core_support + longitudinal_core_coverage;
}
std::optional<Endpoints> principal_axis_endpoints(const std::vector<PixelPoint>& points) {
    if (points.empty()) return std::nullopt;
    if (points.size() == 1) return Endpoints{points.front(), points.front()};
    double mx = 0, my = 0;
    for (auto p : points) mx += double(p.x);
    for (auto p : points) my += double(p.y);
    mx /= double(points.size()); my /= double(points.size());
    double xx = 0, yy = 0, xy = 0;
    for (auto p : points) xx += (double(p.x) - mx) * (double(p.x) - mx);
    for (auto p : points) yy += (double(p.y) - my) * (double(p.y) - my);
    for (auto p : points) xy += (double(p.x) - mx) * (double(p.y) - my);
    double angle = 0.5 * std::atan2(2.0 * xy, xx - yy);
    double ax = std::cos(angle), ay = std::sin(angle);
    double lo = std::numeric_limits<double>::max(), hi = -lo;
    std::size_t li = 0, hi_index = 0;
    for (std::size_t i = 0; i < points.size(); ++i) {
        double t = double(points[i].x) * ax + double(points[i].y) * ay;
        if (t < lo) { lo = t; li = i; }
        if (t > hi) { hi = t; hi_index = i; }
    }
    return Endpoints{points[li], points[hi_index]};
}
namespace detail {
Mask dilate(const Mask& input, int w, int h, int radius) {
    if (radius <= 0) return input;
    Mask out(input.size());
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) if (input[y*w+x]) {
        for (int ny = std::max(0,y-radius); ny <= std::min(h-1,y+radius); ++ny)
            for (int nx = std::max(0,x-radius); nx <= std::min(w-1,x+radius); ++nx)
                if (std::abs(nx-x)+std::abs(ny-y) <= radius) out[ny*w+nx] = 1;
    }
    return out;
}
Mask erode(const Mask& input, int w, int h, int radius) {
    if (radius <= 0) return input;
    Mask out(input.size());
    if (w <= radius*2 || h <= radius*2) return out;
    for (int y = radius; y < h-radius; ++y) for (int x = radius; x < w-radius; ++x) {
        bool survives = true;
        for (int ny = y-radius; ny <= y+radius; ++ny) {
            for (int nx = x-radius; nx <= x+radius; ++nx) {
                if (std::abs(nx-x)+std::abs(ny-y) > radius) continue;
                if (input[ny*w+nx] == 0) { survives = false; break; }
            }
            if (!survives) break;
        }
        if (survives) out[y*w+x] = 1;
    }
    return out;
}
Mask close(const Mask& input, int w, int h, int radius) {
    return erode(dilate(input,w,h,radius),w,h,radius);
}
struct Body {
    Points points;
    double continuity = 1;
    int gap = 0;
    double retained = 1;
    std::optional<Endpoints> endpoints;
    double length = 0, density = 0;
};
static int zero_run(const std::vector<int>& counts, int start, int end) {
    int current = 0, largest = 0;
    for (int i = start; i < end; ++i) {
        if (counts[i] == 0) { ++current; largest = std::max(largest,current); }
        else current = 0;
    }
    return largest;
}
static Body dominant_body(const Points& points, double mx, double my, double ax, double ay) {
    Body body; body.points = points;
    if (points.size() < 6) return body;
    std::vector<double> projections;
    for (auto p : points) projections.push_back((double(p.x)-mx)*ax+(double(p.y)-my)*ay);
    double lo = *std::min_element(projections.begin(),projections.end());
    double hi = *std::max_element(projections.begin(),projections.end());
    int bins = std::max(1,int(std::ceil(hi-lo))+1);
    if (bins < 8) return body;
    std::vector<int> counts(bins), point_bins(points.size());
    for (std::size_t i = 0; i < points.size(); ++i) {
        int bin = std::min(bins-1,std::max(0,rounded(projections[i]-lo)));
        point_bins[i] = bin; ++counts[bin];
    }
    int peak = *std::max_element(counts.begin(),counts.end());
    auto endpoints = [&](int start, int end) -> Endpoints {
        double first = lo+double(start), last = lo+double(end);
        return {{rounded(mx+ax*first),rounded(my+ay*first)},
                {rounded(mx+ax*last),rounded(my+ay*last)}};
    };
    body.endpoints = endpoints(0,bins-1);
    body.length = double(bins); body.density = double(points.size())/double(bins);
    if (peak < 4) {
        int occupied = int(std::count_if(counts.begin(),counts.end(),[](int n){return n>0;}));
        body.continuity = double(occupied)/double(bins); body.gap = zero_run(counts,0,bins);
        return body;
    }
    int dense_threshold = std::max(2,int(std::ceil(double(peak)*0.30)));
    std::vector<int> dense;
    for (int i = 0; i < bins; ++i) if (counts[i] >= dense_threshold) dense.push_back(i);
    if (dense.empty()) { body.continuity = 0; body.gap = bins; return body; }
    std::vector<std::pair<int,int>> groups;
    int start = dense.front(), previous = start;
    for (std::size_t i = 1; i < dense.size(); ++i) {
        int bin = dense[i];
        if (bin-previous-1 > 2) { groups.emplace_back(start,previous); start = bin; }
        previous = bin;
    }
    groups.emplace_back(start,previous);
    auto group_score = [&](std::pair<int,int> range) {
        int support = std::accumulate(counts.begin()+range.first,counts.begin()+range.second+1,0);
        return double(support)*std::sqrt(double(range.second-range.first+1));
    };
    auto best = groups.front();
    // Swift max(by:) keeps the first element for equal scores.
    for (auto group : groups) if (group_score(best) < group_score(group)) best = group;
    int allowance = std::min(3,std::max(1,rounded(double(peak)*0.22)));
    int body_start = std::max(0,best.first-allowance), body_end = std::min(bins-1,best.second+allowance);
    Points selected;
    for (std::size_t i = 0; i < points.size(); ++i)
        if (point_bins[i] >= body_start && point_bins[i] <= body_end) selected.push_back(points[i]);
    if (selected.size() < 4) { body.continuity = 0; body.gap = bins; return body; }
    int dense_count = 0;
    for (int i = body_start; i <= body_end; ++i) if (counts[i] >= dense_threshold) ++dense_count;
    body.points = std::move(selected);
    body.continuity = double(dense_count)/double(body_end-body_start+1);
    body.gap = zero_run(counts,body_start,body_end+1);
    body.retained = double(body.points.size())/double(points.size());
    body.endpoints = endpoints(body_start,body_end);
    body.length = double(body_end-body_start+1);
    body.density = double(body.points.size())/double(body_end-body_start+1);
    return body;
}
std::optional<Scored> score_component(const Points& points, int w, int h,
                                     const Mask* component_mask, const std::set<int>* indices,
                                     const Evidence& e, const std::string& source, int minimum_override) {
    int standard_minimum = std::max(4,minimum_override >= 0 ? minimum_override : int(double(w*h)*0.0005));
    int minimum = e.color == SaberColor::red ? std::min(standard_minimum,20) : standard_minimum;
    if (int(points.size()) < minimum) return std::nullopt;
    double area_ratio = double(points.size())/std::max(double(w*h),1.0);
    if (area_ratio > 0.22) return std::nullopt;
    double mx = 0, my = 0;
    for (auto p : points) mx += double(p.x);
    for (auto p : points) my += double(p.y);
    mx /= double(points.size()); my /= double(points.size());
    double xx = 0, yy = 0, xy = 0;
    for (auto p : points) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        xx += dx*dx; yy += dy*dy; xy += dx*dy;
    }
    double angle = 0.5*std::atan2(2.0*xy,xx-yy);
    double ax = std::cos(angle), ay = std::sin(angle), nx = -ay, ny = ax;
    double min_major = std::numeric_limits<double>::max(), max_major = -min_major;
    double min_minor = min_major, max_minor = -min_major;
    for (auto p : points) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        double major = dx*ax+dy*ay, minor = dx*nx+dy*ny;
        min_major = std::min(min_major,major); max_major = std::max(max_major,major);
        min_minor = std::min(min_minor,minor); max_minor = std::max(max_minor,minor);
    }
    double major_length = max_major-min_major+1.0, minor_length = max_minor-min_minor+1.0;
    double aspect = major_length/std::max(minor_length,1.0);
    bool compact = e.color == SaberColor::red && (int(points.size()) < standard_minimum || aspect < 1.5);
    double extent = double(points.size())/std::max(major_length*minor_length,1.0);
    if (major_length < std::max(4.0,double(std::min(w,h))*0.025)
        || aspect < (e.color == SaberColor::red ? 1.0 : 1.5) || extent < 0.10) return std::nullopt;
    Body body = dominant_body(points,mx,my,ax,ay);
    int bins = std::max(4,std::min(12,int(std::ceil(major_length))));
    std::vector<double> bin_min(bins,std::numeric_limits<double>::max()), bin_max(bins,-std::numeric_limits<double>::max());
    auto axial_bin = [&](PixelPoint p) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        double major = dx*ax+dy*ay;
        double normalized = clamp01((major-min_major)/std::max(max_major-min_major,1.0));
        return std::min(bins-1,int(normalized*double(bins)));
    };
    for (auto p : points) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        double minor = dx*nx+dy*ny;
        int bin = axial_bin(p);
        bin_min[bin] = std::min(bin_min[bin],minor); bin_max[bin] = std::max(bin_max[bin],minor);
    }
    std::vector<double> widths;
    for (int i = 0; i < bins; ++i) if (bin_max[i] >= bin_min[i]) widths.push_back(bin_max[i]-bin_min[i]+1.0);
    double mean_width = std::accumulate(widths.begin(),widths.end(),0.0)/double(std::max<std::size_t>(widths.size(),1));
    double variance_width = 0;
    for (double width : widths) variance_width += std::pow(width-mean_width,2);
    variance_width /= double(std::max<std::size_t>(widths.size(),1));
    double width_variation = std::sqrt(variance_width)/std::max(mean_width,1.0);
    int raw_count = 0, peak = 0, high_count = 0, outside_count = 0, core_count = 0;
    double value_sum = 0, value_square_sum = 0, purity_sum = 0, outside_sum = 0, radiance_sum = 0;
    std::set<int> clipped;
    std::vector<bool> high_bins(bins), core_bins(bins);
    const std::array<PixelPoint,4> neighbors{{{-2,0},{2,0},{0,-2},{0,2}}};
    for (auto p : points) {
        int index = p.y*w+p.x;
        radiance_sum += std::pow(double(e.radiance[index])/255.0,2);
        int value = e.value[index], chroma = e.chroma[index];
        if (e.core_mask[index]) { ++core_count; core_bins[axial_bin(p)] = true; }
        if (e.color_mask[index]) {
            ++raw_count; value_sum += double(value); value_square_sum += double(value*value);
            purity_sum += double(chroma)/double(std::max(value,1)); peak = std::max(peak,value);
            if (value >= 220) { ++high_count; high_bins[axial_bin(p)] = true; }
        } else if (value >= 245 && chroma <= 38) { clipped.insert(index); high_bins[axial_bin(p)] = true; }
        for (int dy = -2; dy <= 2; ++dy) for (int dx = -2; dx <= 2; ++dx) {
            if (std::abs(dx)+std::abs(dy) > 2) continue;
            int px = p.x+dx, py = p.y+dy;
            if (px < 0 || px >= w || py < 0 || py >= h) continue;
            int neighbor = py*w+px;
            if (!e.color_mask[neighbor] && e.value[neighbor] >= 245 && e.chroma[neighbor] <= 45) clipped.insert(neighbor);
        }
        for (auto offset : neighbors) {
            int px = p.x+offset.x, py = p.y+offset.y;
            if (px < 0 || px >= w || py < 0 || py >= h) continue;
            int neighbor = py*w+px;
            bool belongs = component_mask ? (*component_mask)[neighbor] != 0 : indices && indices->count(neighbor);
            if (belongs) continue;
            outside_sum += double(e.value[neighbor]); ++outside_count;
        }
    }
    if (raw_count < 3) return std::nullopt;
    double mean = value_sum/double(raw_count), high = double(high_count)/double(raw_count);
    double purity = purity_sum/double(raw_count);
    double variance = std::max(0.0,value_square_sum/double(raw_count)-mean*mean);
    double variation = clamp01(std::sqrt(variance)/55.0);
    double texture = clamp01((variation-0.22)/0.10)*clamp01((0.65-variation)/0.20);
    double outside_mean = outside_count > 0 ? outside_sum/double(outside_count) : 0;
    double contrast = clamp01((mean-outside_mean)/100.0);
    double high_coverage = double(std::count(high_bins.begin(),high_bins.end(),true))/double(bins);
    double core_support = double(core_count)/double(std::max<std::size_t>(points.size(),1));
    double core_coverage = double(std::count(core_bins.begin(),core_bins.end(),true))/double(bins);
    double clipped_ratio = std::min(double(clipped.size())/double(raw_count+int(clipped.size())),0.25)/0.25;
    double emitter_score = clamp01((double(peak)-200.0)/55.0)*0.32
        + clamp01((mean-160.0)/95.0)*0.23 + high*0.28 + purity*0.12 + clipped_ratio*0.05;
    bool has_core = high >= 0.08 || (peak >= 242 && mean >= 190) || !clipped.empty();
    bool eligible = peak >= 218 && has_core && emitter_score >= 0.42;
    if (compact) eligible = eligible && peak >= 230 && high >= 0.50 && purity >= 0.50;
    Endpoints raw{{rounded(mx+ax*min_major),rounded(my+ay*min_major)},
                  {rounded(mx+ax*max_major),rounded(my+ay*max_major)}};
    bool established = source != "core-line" && body.continuity >= 0.90;
    bool dense_line = source == "core-line" && body.gap <= 2 && body.density >= 4.0;
    bool trimmed_line = source == "core-line" && body.retained <= 0.30 && body.gap <= 2 && int(body.points.size()) >= minimum;
    bool blue_body = e.color == SaberColor::blue && source == "core-line" && body.retained < 0.65
        && body.continuity >= 0.80 && body.gap <= 2 && int(body.points.size()) >= minimum
        && purity >= 0.40 && core_support >= 0.35;
    Endpoints final = raw;
    bool fallback = true;
    if (int(body.points.size()) >= minimum && body.retained < 0.85 && (established || dense_line || trimmed_line || blue_body)) {
        if (auto endpoints = principal_axis_endpoints(body.points)) { final = *endpoints; fallback = false; }
    }
    BoundingBox box{std::numeric_limits<int>::max(),std::numeric_limits<int>::max(),
                    std::numeric_limits<int>::min(),std::numeric_limits<int>::min()};
    for (auto p : points) {
        box.min_x = std::min(box.min_x,p.x); box.min_y = std::min(box.min_y,p.y);
        box.max_x = std::max(box.max_x,p.x); box.max_y = std::max(box.max_y,p.y);
    }
    double diagonal = std::hypot(double(w),double(h));
    double peak_normalized = clamp01((double(peak)-200.0)/55.0), mean_normalized = clamp01((mean-160.0)/95.0);
    double normalized_core = clamp01(core_support);
    double contrast_weight = 8.0+normalized_core*16.0, texture_weight = 13.0*(1.0-normalized_core);
    double length_support = clamp01((major_length/double(std::max(std::min(w,h),1))-0.08)/0.22);
    ScoreBreakdown b;
    b.radiance = radiance_sum/double(points.size())*65.0*length_support;
    b.length = std::min(major_length/std::max(diagonal,1.0),1.0)*8.0;
    b.aspect = std::min(std::log2(std::max(aspect,1.0)),4.0)*2.0;
    b.extent = extent*2.0; b.width_consistency = -std::min(width_variation,2.0)*4.0;
    b.area = -area_ratio*8.0;
    b.peak_brightness = peak_normalized*0.32*38.0; b.mean_brightness = mean_normalized*0.23*38.0;
    b.high_brightness_ratio = high*(0.28*38.0+14.0); b.color_purity = purity*(0.12*38.0+6.0);
    b.local_contrast = contrast*contrast_weight; b.emitter_texture = texture*texture_weight;
    b.clipped_white = clipped_ratio*(0.05*38.0+72.0)*length_support;
    b.longitudinal_high_coverage = high_coverage*3.0;
    b.core_support = core_support*12.0*length_support; b.longitudinal_core_coverage = core_coverage*8.0*length_support;
    Candidate c;
    c.source = source; c.radiance = radiance_sum/double(points.size());
    c.comparison_endpoints = raw; c.endpoints = final; c.bounding_box = box;
    c.score = b.total(); c.score_breakdown = b; c.eligible = eligible; c.compact_red = compact;
    c.peak_value = peak; c.mean_value = mean; c.high_value_ratio = high; c.color_purity = purity;
    c.clipped_white_ratio = clipped_ratio; c.brightness_variation = variation; c.local_contrast = contrast;
    c.longitudinal_high_coverage = high_coverage; c.width_variation = width_variation;
    c.core_support = core_support; c.longitudinal_core_coverage = core_coverage;
    c.longitudinal_continuity = body.continuity; c.largest_longitudinal_gap = body.gap;
    c.retained_body_ratio = body.retained; c.raw_pca_span = major_length;
    c.robust_interval_endpoints = body.endpoints; c.robust_body_length = body.length; c.axial_density = body.density;
    c.component_area = int(points.size()); c.point_count = int(points.size()); c.used_point_led_fallback = fallback;
    return Scored{c,points};
}
std::vector<Scored> components(const Mask& mask, int w, int h, const Evidence& e,
                               int* pixel_count, int minimum_override) {
    Mask remaining = mask;
    std::vector<Scored> candidates;
    std::vector<int> queue;
    for (int seed = 0; seed < int(remaining.size()); ++seed) if (remaining[seed]) {
        remaining[seed] = 0; queue.clear(); queue.push_back(seed);
        std::size_t head = 0; Points points;
        while (head < queue.size()) {
            int value = queue[head++], x = value%w, y = value/w;
            points.push_back({x,y});
            for (int ny = std::max(0,y-1); ny <= std::min(h-1,y+1); ++ny)
                for (int nx = std::max(0,x-1); nx <= std::min(w-1,x+1); ++nx) {
                    int neighbor = ny*w+nx;
                    if (remaining[neighbor]) { remaining[neighbor] = 0; queue.push_back(neighbor); }
                }
        }
        if (pixel_count) *pixel_count += int(points.size());
        if (auto c = score_component(points,w,h,&mask,nullptr,e,"color-mask",minimum_override)) candidates.push_back(std::move(*c));
    }
    std::stable_sort(candidates.begin(),candidates.end(),[](const auto& a,const auto& b){return a.candidate.score>b.candidate.score;});
    return candidates;
}
} // namespace detail
} // namespace phonesaber
