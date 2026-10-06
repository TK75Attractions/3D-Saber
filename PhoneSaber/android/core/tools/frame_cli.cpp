#include "phonesaber/core.hpp"
#include <iomanip>
#include <iostream>
#include <locale>
#include <stdexcept>
using namespace phonesaber;
static void point(PixelPoint p) { std::cout << "{\"x\":" << p.x << ",\"y\":" << p.y << '}'; }
static void output(const std::vector<FrameResult>& results, const FrameProcessor& p) {
    std::cout << "{\"results\":[";
    for (std::size_t i = 0; i < results.size(); ++i) {
        if (i) std::cout << ',';
        const auto& r = results[i];
        std::cout << "{\"color\":\"" << (r.color == SaberColor::red ? "red" : "blue") << "\",\"endpoints\":[";
        point(r.endpoints.first); std::cout << ','; point(r.endpoints.second);
        std::cout << "],\"fresh\":" << r.fresh << ",\"predicted\":" << r.predicted << ",\"port\":" << r.port << ",\"text\":";
        if (r.text) std::cout << '"' << *r.text << '"'; else std::cout << "null";
        std::cout << '}';
    }
    std::cout << "],\"expiry\":";
    if (auto expiry = p.next_expiry()) std::cout << *expiry; else std::cout << "null";
    std::cout << "}\n";
}
int main() {
    std::cout.imbue(std::locale::classic()); std::cout << std::boolalpha << std::setprecision(17);
    FrameProcessor processor;
    char command;
    while (std::cin >> command) {
        std::vector<FrameResult> results;
        if (command == 'R') processor.reset();
        else if (command == 'E') { double now; std::cin >> now; results = processor.expire(now); }
        else if (command == 'F') {
            double now; int w, h; OutputConfig config; std::array<double,2> epochs;
            std::cin >> now >> w >> h >> config.width >> config.height >> config.mirror_x >> config.mirror_y >> config.measurement_mode >> epochs[0] >> epochs[1];
            FrameAnalysis analysis;
            for (int i = 0; i < 2; ++i) {
                int present; Endpoints e;
                std::cin >> present >> e.first.x >> e.first.y >> e.second.x >> e.second.y;
                if (present) analysis.selected[i] = e;
            }
            results = processor.process(analysis,w,h,now,config,epochs);
        } else { std::cerr << "invalid command\n"; return 2; }
        if (!std::cin) { std::cerr << "truncated frame command\n"; return 2; }
        output(results,processor);
    }
}
