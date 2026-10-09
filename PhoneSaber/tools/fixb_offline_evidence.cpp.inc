// Inserted ONLY into a temporary copy of detection.cpp, after production Body.
// All existing production computations finish before this observer is called.
static OfflineTailEvidence offline_measure(const Points& points, const Body& body,
                                          const Evidence& e, int w,
                                          double raw_angle, const std::vector<double>& projections) {
    OfflineTailEvidence out;
    const Points& selected = body.point_count == points.size() ? points : body.points;
    out.body_points = int(selected.size());
    out.body_pca = principal_axis_endpoints(selected);
    if (selected.size() < 2) return out;
    double mx = 0, my = 0;
    for (auto p : selected) mx += double(p.x);
    for (auto p : selected) my += double(p.y);
    mx /= double(selected.size()); my /= double(selected.size());
    double xx = 0, yy = 0, xy = 0;
    for (auto p : selected) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        xx += dx*dx; yy += dy*dy; xy += dx*dy;
    }
    double angle = 0.5*std::atan2(2.0*xy,xx-yy);
    double ax = std::cos(angle), ay = std::sin(angle), nx = -ay, ny = ax;
    double lo = std::numeric_limits<double>::max(), hi = -lo, minor_lo = lo, minor_hi = -lo;
    Mask membership(e.value.size());
    for (auto p : selected) {
        double dx = double(p.x)-mx, dy = double(p.y)-my;
        double major = dx*ax+dy*ay, minor = dx*nx+dy*ny;
        lo = std::min(lo,major); hi = std::max(hi,major);
        minor_lo = std::min(minor_lo,minor); minor_hi = std::max(minor_hi,minor);
        int index = p.y*w+p.x;
        membership[index] = 1;
        out.body_core += e.core_mask[index] != 0;
        out.body_high += e.value[index] >= 220;
    }
    double major_length = hi-lo+1.0, minor_length = minor_hi-minor_lo+1.0;
    out.body_aspect = major_length/std::max(minor_length,1.0);
    out.body_extent = double(selected.size())/std::max(major_length*minor_length,1.0);
    out.axis_alignment = std::abs(std::cos(raw_angle-angle));
    double raw_lo = *std::min_element(projections.begin(),projections.end());
    double raw_hi = *std::max_element(projections.begin(),projections.end());
    std::vector<int> core_bins(std::max(1,int(std::ceil(raw_hi-raw_lo))+1));
    for (std::size_t i = 0; i < points.size(); ++i) {
        auto p = points[i];
        int index = p.y*w+p.x;
        if (!membership[index]) {
            ++out.tail_points;
            out.tail_core += e.core_mask[index] != 0;
            out.tail_high += e.value[index] >= 220;
        }
        if (e.core_mask[index]) {
            int bin = std::min(int(core_bins.size())-1,std::max(0,rounded(projections[i]-raw_lo)));
            ++core_bins[bin];
        }
    }
    // A proxy veto for separated emitters, not a semantic LED classifier:
    // support groups separated by >=3 empty raw-axis bins, >=3 core pixels each.
    int support = 0, empty = 0;
    for (int count : core_bins) {
        if (count) {
            if (empty >= 3) { out.core_groups += support >= 3; support = 0; }
            support += count; empty = 0;
        } else ++empty;
    }
    out.core_groups += support >= 3;
    return out;
}
