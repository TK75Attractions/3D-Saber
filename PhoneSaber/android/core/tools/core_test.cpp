#include "../src/internal.hpp"
#include "../../app/src/main/cpp/rgba_rotation.hpp"
#include <cassert>
#include <cstring>
#include <iostream>
#include <cstdlib>
#include <new>

namespace { bool count_allocations = false; std::size_t allocation_count = 0; }
void* operator new(std::size_t size) {
    void* p = std::malloc(size ? size : 1);
    if (!p) throw std::bad_alloc();
    if (count_allocations) ++allocation_count;
    return p;
}
void* operator new[](std::size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
// GCC の最適化後も、計測用 new と対応する解放関数の境界を保つ。
[[gnu::noinline]] void operator delete(void* p, std::size_t) noexcept { ::operator delete(p); }
[[gnu::noinline]] void operator delete[](void* p, std::size_t) noexcept { ::operator delete[](p); }

// 面積 gate で落ちる大成分でも、小容量からの FIFO 成長を繰り返さない。
static void component_allocation_tests() {
    using namespace phonesaber;
    using namespace phonesaber::detail;
    constexpr int w = 256, h = 256;
    Mask mask(w*h,1), values(w*h,255);
    Evidence evidence{SaberColor::red,values,values,values,mask,mask};
    allocation_count = 0; count_allocations = true;
    int pixels = 0;
    auto result = components(mask,w,h,evidence,&pixels);
    count_allocations = false;
    assert(result.empty() && pixels == w*h);
    assert(allocation_count <= 10); // remaining 1回、FIFO は256点から最大9回。
}
using namespace phonesaber;
static FrameAnalysis detection(Endpoints e) { FrameAnalysis a; a.selected[0] = e; return a; }
// 元画素の非 sample 支持、白芯、次候補への選択と RED の維持を確認する。
static void blue_support_tests() {
    constexpr int width = 480, height = 640, stride = width*4+13;
    auto image = [](int r, int g, int b) {
        std::vector<uint8_t> pixels(stride*height,0);
        for (int y = 300; y < 320; ++y) for (int x = 60; x < 420; ++x) {
            auto* pixel = pixels.data()+y*stride+x*4;
            bool white = y >= 306 && y < 314;
            pixel[0] = white ? 255 : b; pixel[1] = white ? 255 : g;
            pixel[2] = white ? 255 : r; pixel[3] = 255;
        }
        return pixels;
    };
    auto detect = [](const std::vector<uint8_t>& pixels) {
        return analyze({pixels.data(),width,height,stride,pixels.size(),PixelFormat::bgra});
    };
    auto pale = image(140,170,250);
    auto rejected = detect(pale);
    assert(!rejected.selected[1] && !rejected.candidates[1].empty());
    assert(rejected.candidates[1].front().blue_no_deep_support->rejected);
    auto deep = detect(image(30,40,250));
    assert(deep.selected[1] && deep.candidates[1].front().blue_no_deep_support->deep_count > 0);
    auto one_deep = pale;
    auto* pixel = one_deep.data()+301*stride+61*4;
    pixel[0] = 180; pixel[1] = 116; pixel[2] = 71;
    auto kept = detect(one_deep);
    assert(kept.selected[1] && kept.candidates[1].front().blue_no_deep_support->deep_count == 1);
    for (auto rgb : {std::array<int,3>{0,0,179}, {80,129,200}, {79,130,200}}) {
        pixel[0] = rgb[2]; pixel[1] = rgb[1]; pixel[2] = rgb[0];
        assert(!detect(one_deep).selected[1]);
    }
    auto red = detect(image(250,40,30));
    assert(red.selected[0]);
    for (const auto& c : red.candidates[0]) assert(!c.blue_no_deep_support);
    assert(!detect(image(250,170,140)).selected[0]);
    for (int y = 400; y < 416; ++y) for (int x = 60; x < 160; ++x) {
        auto* p = pale.data()+y*stride+x*4;
        p[0] = 250; p[1] = 40; p[2] = 30; p[3] = 255;
    }
    auto fallback = detect(pale);
    assert(fallback.candidates[1].front().blue_no_deep_support->rejected);
    assert(fallback.selected[1] && fallback.selected[1]->first.y > 390);
    for (const auto& c : fallback.candidates[1]) if (c.eligible) {
        assert(c.blue_no_deep_support->deep_count > 0);
        assert(fallback.selected[1]->first.x == c.endpoints.first.x);
        assert(fallback.selected[1]->first.y == c.endpoints.first.y);
        break;
    }
}
// 元の菱形走査を独立した参照にして、小画像を全列挙し境界も確認する。
static detail::Mask reference_morphology(const detail::Mask& input, int w, int h, int radius, bool erosion) {
    if (radius <= 0) return input;
    detail::Mask out(input.size());
    for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
        if (erosion && (x < radius || y < radius || x >= w-radius || y >= h-radius)) continue;
        bool value = erosion;
        for (int dy = -radius; dy <= radius; ++dy) for (int dx = -radius; dx <= radius; ++dx) {
            if (std::abs(dx)+std::abs(dy) > radius) continue;
            int px = x+dx, py = y+dy;
            bool on = px >= 0 && px < w && py >= 0 && py < h && input[py*w+px] != 0;
            value = erosion ? value && on : value || on;
        }
        out[y*w+x] = value;
    }
    return out;
}
static void morphology_tests() {
    uint32_t random = 0x51ab3u;
    for (int h : {1,2,3,5,9,17}) for (int w : {1,2,3,5,9,17}) {
        int cases = w*h <= 9 ? 1 << (w*h) : 64;
        for (int pattern = 0; pattern < cases; ++pattern) {
            detail::Mask input(w*h);
            for (int i = 0; i < w*h; ++i) {
                random = random*1664525u+1013904223u;
                input[i] = w*h <= 9 ? (pattern >> i)&1
                    : (random >> 24) < unsigned(pattern*4) ? uint8_t(random >> 16)|1 : 0;
            }
            for (int radius = 0; radius <= 4; ++radius) {
                assert(detail::dilate(input,w,h,radius) == reference_morphology(input,w,h,radius,false));
                assert(detail::erode(input,w,h,radius) == reference_morphology(input,w,h,radius,true));
            }
        }
    }
}
// 独立した整数 FIFO 参照で、BFS 点順・同点順・不採用成分を含む pixel_count を確認する。
static void component_order_tests() {
    using namespace detail;
    uint32_t random = 0x51ab3u;
    for (int w : {1,2,3,5,9,17,31}) {
        int h = w+2;
        Mask value(w*h), chroma(w*h), radiance(w*h), core(w*h);
        for (int i = 0; i < w*h; ++i) {
            value[i] = uint8_t(215+i%41); chroma[i] = uint8_t(80+i%110);
            radiance[i] = uint8_t(i%256); core[i] = i%3 == 0;
        }
        for (int pattern = 0; pattern < 640; ++pattern) {
            Mask mask(w*h);
            for (int i = 0; i < w*h; ++i) {
                random = random*1664525u+1013904223u;
                if (pattern >= 512 && (random >> 24) < 24) mask[i] = uint8_t(random >> 16)|1;
            }
            if (pattern < 512) {
                int x0 = pattern%3 == 0 ? 0 : std::max(0,w-3), y0 = pattern%2 == 0 ? 0 : h-3;
                for (int y = 0; y < 3; ++y) for (int x = 0; x < std::min(w,3); ++x)
                    mask[(y0+y)*w+x0+x] = (pattern >> (y*3+x))&1;
            } else {
                for (int y = 0; y < h; ++y) mask[y*w+(pattern%2 ? w/2 : y%w)] = 1;
            }
            for (auto color : {SaberColor::red,SaberColor::blue}) {
                Evidence evidence{color,radiance,value,chroma,mask,core};
                Mask remaining = mask;
                std::vector<Scored> expected;
                int expected_count = 0;
                for (int seed = 0; seed < w*h; ++seed) if (remaining[seed]) {
                    remaining[seed] = 0;
                    std::vector<int> queue{seed};
                    Points points;
                    for (std::size_t head = 0; head < queue.size(); ++head) {
                        int index = queue[head], x = index%w, y = index/w;
                        points.push_back({x,y});
                        for (int yy = std::max(0,y-1); yy <= std::min(h-1,y+1); ++yy)
                            for (int xx = std::max(0,x-1); xx <= std::min(w-1,x+1); ++xx) {
                                int next = yy*w+xx;
                                if (remaining[next]) { remaining[next] = 0; queue.push_back(next); }
                            }
                    }
                    expected_count += int(points.size());
                    if (auto scored = score_component(points,w,h,&mask,evidence,"color-mask",4))
                        expected.push_back(std::move(*scored));
                }
                std::stable_sort(expected.begin(),expected.end(),[](const Scored& a,const Scored& b){return a.candidate.score>b.candidate.score;});
                int count = 0;
                auto actual = components(mask,w,h,evidence,&count,4);
                assert(count == expected_count && actual.size() == expected.size());
                for (std::size_t i = 0; i < actual.size(); ++i) {
                    assert(std::memcmp(&actual[i].candidate.score,&expected[i].candidate.score,sizeof(double)) == 0);
                    assert(actual[i].support_points.size() == expected[i].support_points.size());
                    for (std::size_t j = 0; j < actual[i].support_points.size(); ++j) {
                        assert(actual[i].support_points[j].x == expected[i].support_points[j].x);
                        assert(actual[i].support_points[j].y == expected[i].support_points[j].y);
                    }
                }
            }
        }
    }
}
// 未整列/padding/最終行省略の回転を、独立した逆写像と比較する。ASan/UBSanでも実行する。
static void rotation_tests() {
    android::RgbaRotation rotation;
    uint32_t random = 0x51ab3u;
    for (int w : {1,2,3,17,640}) for (int h : {1,2,5,19,480}) for (int padding : {0,1,13}) {
        std::size_t stride = std::size_t(w)*4+padding;
        std::vector<uint8_t> input(stride*h+5);
        for (auto& byte : input) { random = random*1664525u+1013904223u; byte = uint8_t(random >> 24); }
        const auto* source = input.data()+1;
        for (bool last_padding : {false,true}) for (int degrees : {0,90,180,270}) {
            std::size_t size = stride*(h-1)+w*4+(last_padding ? padding : 0);
            auto out = rotation.orient(source,size,w,h,stride,degrees);
            int ow = degrees%180 == 0 ? w : h, oh = degrees%180 == 0 ? h : w;
            assert(out.width == ow && out.height == oh && out.format == PixelFormat::rgba);
            for (int y = 0; y < oh; ++y) for (int x = 0; x < ow; ++x) {
                int sx = degrees == 90 ? y : degrees == 180 ? w-1-x : degrees == 270 ? w-1-y : x;
                int sy = degrees == 90 ? h-1-x : degrees == 180 ? h-1-y : degrees == 270 ? x : y;
                for (int c = 0; c < 4; ++c)
                    assert(out.data[std::size_t(y)*out.row_stride+x*4+c] == source[std::size_t(sy)*stride+sx*4+c]);
            }
            if (degrees == 0 && last_padding) assert(out.data == source && out.row_stride == stride);
            else {
                auto again = rotation.orient(source,size,w,h,stride,degrees);
                assert(again.data == out.data && again.row_stride == std::size_t(ow)*4);
            }
        }
    }
    auto rejected = [&](const uint8_t* p, std::size_t size, int w, int h, std::size_t stride, int degrees) {
        bool caught = false;
        try { rotation.orient(p,size,w,h,stride,degrees); }
        catch (const std::invalid_argument&) { caught = true; }
        assert(caught);
    };
    uint8_t bytes[32]{};
    rejected(bytes,27,3,2,16,90);
    rejected(bytes,32,3,2,11,90);
    rejected(bytes,32,3,2,16,45);
    rejected(bytes,32,0,2,16,90);
    rejected(nullptr,32,3,2,16,90);
}
// 暗い補間点・近傍・raw/body 分岐を同じ合成候補で確認する。
static void core_line_guard_tests() {
    constexpr int w = 160, h = 40;
    detail::Mask mask(w*h), core(w*h), values(w*h,255);
    detail::Points span, compact;
    for (int x = 0; x < 100; ++x) {
        span.push_back({x,20});
        if (x < 20 || x == 99) mask[20*w+x] = core[20*w+x] = 1;
        if (x < 20) compact.push_back({x,20});
    }
    detail::Evidence evidence{SaberColor::red,values,values,values,mask,core};
    Candidate line, body;
    line.source = "core-line"; line.eligible = true; line.used_point_led_fallback = true;
    line.endpoints = {{0,20},{99,20}}; line.score = 100;
    body.source = "core-halo"; body.eligible = true; body.core_support = .40; body.high_value_ratio = .35;
    body.endpoints = {{0,20},{19,20}}; body.score = 50;
    auto run = [&](Candidate l, Candidate b, int step = 2) {
        std::vector<detail::Scored> c{{l,span},{b,compact}};
        detail::apply_core_line_guard(c,evidence,w,step);
        return c;
    };
    auto rejected = run(line,body);
    assert(!rejected[0].candidate.eligible && rejected[1].candidate.eligible);
    assert(rejected[0].candidate.core_line_guard->supporting_index == 1);
    assert(rejected[0].candidate.core_line_guard->supporting_endpoints.second.x == 38);
    assert(rejected[0].candidate.core_line_guard->lit_coverage == .21);
    assert(rejected[0].candidate.score == 100);
    body.core_support = .399; assert(run(line,body)[0].candidate.eligible); body.core_support = .40;
    body.high_value_ratio = .349; assert(run(line,body)[0].candidate.eligible); body.high_value_ratio = .35;
    body.eligible = false; assert(run(line,body)[0].candidate.eligible); body.eligible = true;
    body.source = "core-line"; assert(run(line,body)[0].candidate.eligible); body.source = "core-halo";
    body.endpoints = {{0,35},{19,35}}; assert(!run(line,body)[0].candidate.eligible);
    body.endpoints = {{0,36},{19,36}}; assert(run(line,body)[0].candidate.eligible);
    body.endpoints = {{0,20},{19,20}};
    line.used_point_led_fallback = false; line.endpoints = body.endpoints;
    assert(run(line,body)[0].candidate.eligible);
    line.used_point_led_fallback = true; line.endpoints = {{0,20},{99,20}};
    // 長い点 LED の全長に実支持がある場合、および .60 境界は保持する。
    for (int x = 0; x < 100; ++x) mask[20*w+x] = core[20*w+x] = x < 59 || x == 99;
    assert(detail::full_lit_coverage(span,evidence,w) == .60);
    assert(run(line,body)[0].candidate.eligible);
    for (int x = 0; x < 100; ++x) mask[20*w+x] = core[20*w+x] = x < 20 || x%4 != 3;
    assert(detail::full_lit_coverage(span,evidence,w) == .80);
    assert(run(line,body)[0].candidate.eligible);
}

// 会場の赤いコーン（2026-10-18、iPhone StaticBGRADetectionTests と同じ場面）: 無視範囲に両端が入る
// 候補は選ばず、同じコマの別の候補を選ぶ。候補の一覧・score・eligible は変えない。
static void ignore_zone_tests() {
    constexpr int width = 200, height = 120, stride = width*4;
    std::vector<uint8_t> scene(stride*height,0);
    auto bar = [&](PixelPoint start, PixelPoint end, int thickness) {
        int steps = std::max(std::abs(end.x-start.x),std::abs(end.y-start.y));
        for (int step = 0; step <= steps; ++step) {
            double t = steps == 0 ? 0.0 : double(step)/double(steps);
            int x = int(std::round(double(start.x)+double(end.x-start.x)*t));
            int y = int(std::round(double(start.y)+double(end.y-start.y)*t));
            for (int dy = -thickness; dy <= thickness; ++dy) for (int dx = -thickness; dx <= thickness; ++dx) {
                if (dx*dx+dy*dy > thickness*thickness || x+dx < 0 || x+dx >= width || y+dy < 0 || y+dy >= height) continue;
                auto* pixel = scene.data()+(y+dy)*stride+(x+dx)*4;
                pixel[0] = 30; pixel[1] = 45; pixel[2] = 245; pixel[3] = 255; // BGRA の赤
            }
        }
    };
    bar({150,15},{150,105},4);
    bar({30,35},{45,105},4);
    const ColorThreshold threshold{140,20,30};
    const PixelBuffer pixels{scene.data(),width,height,stride,scene.size(),PixelFormat::bgra};
    auto run = [&](const IgnoreZones& zones) { return analyze(pixels,threshold,threshold,2,zones); };
    auto distance = [](const Endpoints& a, const Endpoints& b) {
        auto d = [](PixelPoint p, PixelPoint q) { return std::hypot(double(p.x-q.x),double(p.y-q.y)); };
        return std::min(d(a.first,b.first)+d(a.second,b.second),d(a.first,b.second)+d(a.second,b.first));
    };
    auto same_candidates = [](const FrameAnalysis& a, const FrameAnalysis& b) {
        for (int color = 0; color < 2; ++color) {
            assert(a.candidates[color].size() == b.candidates[color].size());
            for (std::size_t i = 0; i < a.candidates[color].size(); ++i) {
                const auto &x = a.candidates[color][i], &y = b.candidates[color][i];
                assert(x.source == y.source && x.score == y.score && x.eligible == y.eligible);
                assert(x.endpoints.first.x == y.endpoints.first.x && x.endpoints.second.y == y.endpoints.second.y);
            }
        }
    };
    const auto plain = run({});
    const auto legacy = analyze(pixels,threshold,threshold);
    same_candidates(plain,legacy);
    assert(plain.selected[0] && plain.selected_extent[0] && !plain.selected[1]);
    const auto first = *plain.selected[0];
    const auto zone = ignore_zone_around(*plain.selected_extent[0],width,height);
    assert(saber_ignored(first,width,height,{zone}));
    IgnoreZones red_zone; red_zone[0] = {zone};
    const auto skipped = run(red_zone);
    same_candidates(plain,skipped);
    assert(skipped.selected[0] && distance(*skipped.selected[0],first) > 40);
    assert(!saber_ignored(*skipped.selected[0],width,height,{zone}));
    IgnoreZones far; far[0] = {IgnoreZone{0.9,0.9,1,1}};
    const auto unrelated = run(far);
    assert(unrelated.selected[0] && distance(*unrelated.selected[0],first) < 1);
    IgnoreZones blue_zone; blue_zone[1] = {zone};
    const auto blue = run(blue_zone);
    assert(blue.selected[0] && distance(*blue.selected[0],first) < 1);
    // 両方を無視すると赤は選ばれない（剣だけを残すため、範囲は色ごと・最大4つ）。
    IgnoreZones both; both[0] = {zone,ignore_zone_around(*skipped.selected_extent[0],width,height)};
    assert(!run(both).selected[0]);
    // FrameProcessor 経由でも同じ範囲が効く。
    FrameProcessor processor;
    auto sent = processor.process(pixels,1.0,{},{},threshold,threshold,red_zone);
    assert(sent.size() == 1 && sent[0].endpoints.first.x == skipped.selected[0]->first.x);
    // 範囲の作り方: 各辺 0.04 広げ、0.06 未満の辺は中心から 0.06 にする。片端だけ入る線分は使う。
    auto tiny = ignore_zone_around(BoundingBox{100,60,100,60},width,height);
    assert(std::abs((tiny.max_x-tiny.min_x)-0.08) < 1e-12 && std::abs(tiny.min_x-0.46) < 1e-12);
    auto line = ignore_zone_around(BoundingBox{20,10,22,110},width,height);
    assert(std::abs((line.max_x-line.min_x)-0.09) < 1e-9);
    assert(std::abs(line.min_y-(10.0/120-0.04)) < 1e-9 && std::abs(line.max_y-(110.0/120+0.04)) < 1e-9);
    IgnoreZone left{0,0,0.5,1};
    assert(!saber_ignored({{10,10},{150,10}},width,height,{left}));
    assert(saber_ignored({{10,10},{99,10}},width,height,{left}));
}
// プレイ範囲（2026-10-18、赤い服の人など動く物の対策）: 両端の中点が範囲外の候補は選ばず、同じコマの
// 範囲内の候補を選ぶ。範囲が無ければ従来と同じ。iPhone StaticBGRADetectionTests と同じ場面。
static void play_area_tests() {
    constexpr int width = 200, height = 120, stride = width*4;
    std::vector<uint8_t> scene(stride*height,0);
    auto bar = [&](PixelPoint start, PixelPoint end, int thickness) {
        int steps = std::max(std::abs(end.x-start.x),std::abs(end.y-start.y));
        for (int step = 0; step <= steps; ++step) {
            double t = steps == 0 ? 0.0 : double(step)/double(steps);
            int x = int(std::round(double(start.x)+double(end.x-start.x)*t));
            int y = int(std::round(double(start.y)+double(end.y-start.y)*t));
            for (int dy = -thickness; dy <= thickness; ++dy) for (int dx = -thickness; dx <= thickness; ++dx) {
                if (dx*dx+dy*dy > thickness*thickness || x+dx < 0 || x+dx >= width || y+dy < 0 || y+dy >= height) continue;
                auto* pixel = scene.data()+(y+dy)*stride+(x+dx)*4;
                pixel[0] = 30; pixel[1] = 45; pixel[2] = 245; pixel[3] = 255;
            }
        }
    };
    bar({150,15},{150,105},4);
    bar({30,35},{45,105},4);
    const ColorThreshold threshold{140,20,30};
    const PixelBuffer pixels{scene.data(),width,height,stride,scene.size(),PixelFormat::bgra};
    auto run = [&](const std::optional<PlayArea>& area) { return analyze(pixels,threshold,threshold,2,{},area); };
    auto distance = [](const Endpoints& a, const Endpoints& b) {
        auto d = [](PixelPoint p, PixelPoint q) { return std::hypot(double(p.x-q.x),double(p.y-q.y)); };
        return std::min(d(a.first,b.first)+d(a.second,b.second),d(a.first,b.second)+d(a.second,b.first));
    };
    auto same_candidates = [](const FrameAnalysis& a, const FrameAnalysis& b) {
        for (int color = 0; color < 2; ++color) {
            assert(a.candidates[color].size() == b.candidates[color].size());
            for (std::size_t i = 0; i < a.candidates[color].size(); ++i) {
                const auto &x = a.candidates[color][i], &y = b.candidates[color][i];
                assert(x.source == y.source && x.score == y.score && x.eligible == y.eligible);
                assert(x.endpoints.first.x == y.endpoints.first.x && x.endpoints.second.y == y.endpoints.second.y);
            }
        }
    };
    const auto legacy = analyze(pixels,threshold,threshold);
    const auto plain = run(std::nullopt);
    same_candidates(plain,legacy);
    assert(plain.selected[0] && legacy.selected[0]);
    const auto first = *plain.selected[0];
    assert(distance(first,*legacy.selected[0]) == 0);
    // 選ばれた棒の中点が入らない半分だけを範囲にすると、もう一方の棒が選ばれる。
    const double mid_x = (double(first.first.x)+double(first.second.x))/2.0/width;
    const PlayArea other = mid_x > 0.5 ? PlayArea{0,0,0.5,1} : PlayArea{0.5,0,1,1};
    assert(!other.contains_midpoint(first,width,height));
    const auto inside = run(other);
    same_candidates(plain,inside);
    assert(inside.selected[0] && distance(*inside.selected[0],first) > 40);
    assert(other.contains_midpoint(*inside.selected[0],width,height));
    // 全体を含む範囲は選択を変えない。どちらも入らない範囲では選ばない。青には影響しない（青は無い）。
    const auto whole = run(PlayArea{0,0,1,1});
    assert(whole.selected[0] && distance(*whole.selected[0],first) == 0);
    assert(!run(PlayArea{0.9,0.9,1,1}).selected[0]);
    // FrameProcessor 経由でも同じ範囲が効く。
    FrameProcessor processor;
    auto sent = processor.process(pixels,1.0,{},{},threshold,threshold,{},other);
    assert(sent.size() == 1 && sent[0].endpoints.first.x == inside.selected[0]->first.x);
    // 中点の規則: 片端がはみ出しても中点が範囲内なら使う。境界上は範囲内。
    const PlayArea left{0,0,0.5,1};
    assert(left.contains_midpoint({{10,10},{150,10}},width,height));   // 中点 x=80/200=0.4
    assert(!left.contains_midpoint({{90,10},{150,10}},width,height));  // 中点 x=120/200=0.6
    assert(left.contains_midpoint({{100,10},{100,100}},width,height)); // 中点 x=0.5（境界）
    // 学習: 30 未満は使わない。両端の min/max + 0.08、最小 0.3、0..1 に収める。
    PlayAreaLearner learner;
    for (int i = 0; i < 29; ++i) learner.add({{40,24},{120,96}},width,height); // x 0.2..0.6, y 0.2..0.8
    assert(!learner.result());
    learner.add({{40,24},{120,96}},width,height);
    const auto learned = learner.result();
    assert(learner.samples == 30 && learned);
    assert(std::abs(learned->min_x-0.12) < 1e-12 && std::abs(learned->max_x-0.68) < 1e-12);
    assert(std::abs(learned->min_y-0.12) < 1e-12 && std::abs(learned->max_y-0.88) < 1e-12);
    const auto small = PlayArea::around(0.5,0.5,0.52,0.5);
    assert(std::abs((small.max_x-small.min_x)-0.3) < 1e-12 && std::abs(small.min_x-0.36) < 1e-12);
    const auto edge = PlayArea::around(0.95,0.0,1.0,0.02);
    assert(edge.max_x == 1 && std::abs(edge.min_x-0.7) < 1e-12 && edge.min_y == 0 && std::abs(edge.max_y-0.3) < 1e-12);
    const auto everything = PlayArea::around(0.0,0.0,1.0,1.0);
    assert(everything.min_x == 0 && everything.max_x == 1 && everything.min_y == 0 && everything.max_y == 1);
    assert(!PlayAreaLearner{}.result(0));
}
// Swift の StaticBGRADetectionTests と同じ2本の棒・時刻で確認する。
static void track_lock_tests() {
    constexpr int width = 640, height = 480, stride = width*4;
    const ColorThreshold threshold{140,20,30};
    for (int color = 0; color < 2; ++color) {
        auto image = [&](bool near, bool far, int near_x = 80) {
            std::vector<uint8_t> pixels(stride*height,0);
            auto bar = [&](int x, int y1, int y2) {
                for (int y = y1; y <= y2; ++y) for (int dx = -4; dx <= 4; ++dx) {
                    auto* p = pixels.data()+y*stride+(x+dx)*4;
                    p[0] = color == 1 ? 245 : 30; p[1] = 45; p[2] = color == 0 ? 245 : 30; p[3] = 255;
                }
            };
            if (near) bar(near_x,200,270);
            if (far) bar(540,160,270);
            return pixels;
        };
        TrackLock lock;
        auto analyze_image = [&](const std::vector<uint8_t>& data, double now, TrackLock* state,
                                 const IgnoreZones& zones = IgnoreZones{}, const std::optional<PlayArea>& area = std::nullopt,
                                 int w = 0) {
            if (w == 0) w = width;
            return analyze({data.data(),w,height,stride,data.size(),PixelFormat::bgra},threshold,threshold,2,zones,area,state,now);
        };
        const auto near = image(true,false), both = image(true,true), far = image(false,true);
        assert(analyze_image(near,1,&lock).selected[color]->first.x < 100);
        auto legacy = analyze_image(both,1.02,nullptr);
        assert(legacy.selected[color] && legacy.selected[color]->first.x > 500);
        auto kept = analyze_image(both,1.02,&lock);
        assert(kept.selected[color] && kept.selected[color]->first.x < 100);
        for (int c = 0; c < 2; ++c) {
            assert(kept.candidates[c].size() == legacy.candidates[c].size());
            for (std::size_t i = 0; i < kept.candidates[c].size(); ++i) {
                const auto& a = kept.candidates[c][i]; const auto& b = legacy.candidates[c][i];
                assert(a.score == b.score && a.eligible == b.eligible && a.source == b.source);
                assert(a.endpoints.first.x == b.endpoints.first.x && a.endpoints.second.y == b.endpoints.second.y);
            }
        }
        FrameProcessor output;
        output.process(kept,width,height,1.02);
        output.process(kept,width,height,1.025); // 補間できる履歴を2回作る。
        for (int missing = 1; missing <= 3; ++missing) {
            auto absent = analyze_image(far,1.02+missing*0.02,&lock);
            assert(!absent.selected[color] && absent.track_lock_suppressed[color] && !absent.selected_extent[color]);
            for (const auto& r : output.process(absent,width,height,1.02+missing*0.02))
                assert(!r.fresh && !r.predicted && !r.text);
        }
        assert(analyze_image(far,1.10,&lock).selected[color]->first.x > 500);
        // 完全に色が消えたフレームも3回の待ちに数える。
        lock.reset(); analyze_image(near,1,&lock);
        for (int missing = 1; missing <= 3; ++missing) {
            auto empty = analyze_image(image(false,false),1+missing*0.02,&lock);
            assert(!empty.selected[color] && empty.track_lock_suppressed[color]);
        }
        assert(analyze_image(both,1.08,&lock).selected[color]->first.x > 500);
        // 期限切れ、サイズ変更、カメラ再開で即座に通常選択へ戻る。
        lock.reset(); analyze_image(near,2,&lock);
        assert(analyze_image(both,2.251,&lock).selected[color]->first.x > 500);
        lock.reset(); analyze_image(near,3,&lock);
        assert(analyze_image(both,3.02,&lock,{},std::nullopt,width-1).selected[color]->first.x > 500);
        lock.reset(); analyze_image(near,4,&lock); lock.reset();
        assert(analyze_image(both,4.02,&lock).selected[color]->first.x > 500);
        // 無視・プレイ範囲は追跡より先に効く（近い候補を復活させない）。
        lock.reset(); auto seed = analyze_image(near,5,&lock);
        IgnoreZones zones; zones[color] = {ignore_zone_around(*seed.selected_extent[color],width,height)};
        assert(!analyze_image(both,5.02,&lock,zones).selected[color]);
        lock.reset(); analyze_image(near,6,&lock);
        assert(!analyze_image(both,6.02,&lock,{},PlayArea{0.7,0,1,1}).selected[color]);
        // 速度予測: 80→100 px / 0.02 s の次は、0.14 s 時点で220 pxを予測する。
        lock.reset(); analyze_image(near,7,&lock);
        analyze_image(image(true,false,100),7.02,&lock);
        assert(analyze_image(image(true,false,220),7.14,&lock).selected[color]);
        // FrameProcessor 所有の履歴も reset と OFF→ON で消える。
        FrameProcessor processor;
        auto process = [&](const std::vector<uint8_t>& data, double now, bool enabled) {
            return processor.process({data.data(),width,height,stride,data.size(),PixelFormat::bgra},now,
                                     {},{},threshold,threshold,{},std::nullopt,enabled);
        };
        process(near,8,true); processor.reset();
        assert(process(both,8.02,true).front().endpoints.first.x > 500);
        processor.reset(); process(near,9,true);
        assert(process(both,9.02,false).front().endpoints.first.x > 500);
        assert(process(both,9.04,true).front().endpoints.first.x > 500);
        // 0.25秒を越えて色を失っても、古い履歴から補間 UDP を作らない。
        for (const auto& r : process(image(false,false),9.31,true)) assert(!r.fresh && !r.text);
    }
    std::cout << "track lock: both colors, gating, 3 misses, resets, filters, velocity and UDP suppression passed\n";
}
int main() {
    track_lock_tests();
    play_area_tests();
    ignore_zone_tests();
    core_line_guard_tests();
    rotation_tests();
    morphology_tests();
    component_order_tests();
    component_allocation_tests();
    blue_support_tests();
    FrameProcessor p;
    auto a = p.process(detection({{10,20},{30,40}}),100,100,1.0);
    assert(a.size() == 1 && a[0].fresh && !a[0].predicted && a[0].port == 5005);
    auto b = p.process(detection({{35,45},{15,25}}),100,100,1.01);
    assert(b[0].endpoints.first.x == 15 && b[0].endpoints.second.x == 35);
    for (int missing = 1; missing <= 3; ++missing) {
        auto predicted = p.process(FrameAnalysis{},100,100,1.01+missing*0.01);
        assert(predicted[0].predicted && predicted[0].fresh && predicted[0].text);
        assert(predicted[0].endpoints.first.x == 15+5*missing);
    }
    auto held = p.process(FrameAnalysis{},100,100,1.05);
    assert(held.size() == 1 && !held[0].fresh && !held[0].text);
    assert(p.next_expiry() && *p.next_expiry() == 1.01+0.18);
    assert(p.expire(1.01+0.18).empty() && !p.next_expiry());
    p.process(detection({{99,99},{80,80}}),100,100,2.0);
    p.process(detection({{100,100},{90,90}}),100,100,2.01);
    auto clipped = p.process(FrameAnalysis{},100,100,2.02);
    assert(clipped[0].endpoints.first.x == 99 && clipped[0].endpoints.first.y == 99);
    assert(p.process(FrameAnalysis{},101,100,2.03).empty());
    assert(payload({{0,0},{99,99}},100,100) == "0,0,1919,1079");
    assert(payload({{0,0},{99,99}},100,100,1920,1080,true,true) == "1919,1079,0,0");
    assert(timestamped_payload("1,2,3,4",123.123456789) == "ts=123.123457;1,2,3,4");
    assert(analyze({}).candidates[0].empty());
    assert(!principal_axis_endpoints({}));
    // One-pixel-wide input, padded rows and ignored alpha.
    std::vector<uint8_t> pixels(40,0);
    assert(analyze({pixels.data(),1,5,8,pixels.size(),PixelFormat::rgba}).candidates[1].empty());
    std::cout << "core tests passed\n";
}
