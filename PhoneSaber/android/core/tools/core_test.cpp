#include "phonesaber/core.hpp"
#include <cassert>
#include <iostream>
using namespace phonesaber;
static FrameAnalysis detection(Endpoints e) { FrameAnalysis a; a.selected[0] = e; return a; }
int main() {
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
