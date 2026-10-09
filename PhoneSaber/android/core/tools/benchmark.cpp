#include "phonesaber/core.hpp"
#include "png.hpp"
#include <algorithm>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <stdexcept>

int main(int argc, char** argv) {
    try {
        if (argc < 3) throw std::runtime_error("usage: core-benchmark ITERATIONS PNG [PNG ...]");
        int iterations = std::stoi(argv[1]);
        if (iterations < 1) throw std::runtime_error("iterations must be positive");
        std::cout << "png,width,height,median_ms,min_ms,max_ms\n" << std::fixed << std::setprecision(6);
        for (int file = 2; file < argc; ++file) {
            auto image = read_png(argv[file]);
            // JNI と同じ BGRA 入力。デコードと変換は計測区間の外。
            for (std::size_t i = 0; i < image.rgba.size(); i += 4) std::swap(image.rgba[i],image.rgba[i+2]);
            phonesaber::PixelBuffer pixels{image.rgba.data(),image.width,image.height,
                std::size_t(image.width)*4,image.rgba.size(),phonesaber::PixelFormat::bgra};
            phonesaber::FrameProcessor processor;
            std::size_t result_count = 0;
            for (int i = 0; i < 10; ++i) result_count += processor.process(pixels,double(i)/60.0).size();
            std::vector<double> times;
            times.reserve(iterations);
            for (int i = 0; i < iterations; ++i) {
                auto start = std::chrono::steady_clock::now();
                auto results = processor.process(pixels,double(i+10)/60.0);
                auto end = std::chrono::steady_clock::now();
                result_count += results.size();
                times.push_back(std::chrono::duration<double,std::milli>(end-start).count());
            }
            std::sort(times.begin(),times.end());
            std::cout << argv[file] << ',' << image.width << ',' << image.height << ','
                << times[times.size()/2] << ',' << times.front() << ',' << times.back() << '\n';
            // 結果を使用し、最適化による呼び出しの除去を防ぐ。
            std::cerr << argv[file] << ": results=" << result_count << '\n';
        }
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 2; }
    return 0;
}
