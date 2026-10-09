#include "../src/internal.hpp"
#include "../../app/src/main/cpp/rgba_rotation.hpp"
#include <cassert>
#include <cstring>
#include <iostream>
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
    for (int w : {9,17,31}) {
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
                if (pattern >= 512) mask[i] = (random >> 24) < 24;
            }
            if (pattern < 512) {
                int x0 = pattern%3 == 0 ? 0 : w-3, y0 = pattern%2 == 0 ? 0 : h-3;
                for (int y = 0; y < 3; ++y) for (int x = 0; x < 3; ++x)
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
int main() {
    rotation_tests();
    morphology_tests();
    component_order_tests();
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
