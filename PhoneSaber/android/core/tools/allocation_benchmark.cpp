// C++ new/new[] の回数・要求 byte 数を計測する専用 executable。
// PNG デコード、warm-up、出力、および libc 内部の malloc は計数外。
#include "phonesaber/core.hpp"
#include "png.hpp"
#include <algorithm>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <new>
#include <stdexcept>

namespace {
bool measuring = false;
std::size_t calls = 0, bytes = 0;
}
void* operator new(std::size_t size) {
    void* p = std::malloc(size ? size : 1);
    if (!p) throw std::bad_alloc();
    if (measuring) { ++calls; bytes += size; }
    return p;
}
void* operator new[](std::size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }

int main(int argc, char** argv) {
    try {
        if (argc < 3) throw std::runtime_error("usage: core-allocation-benchmark ITERATIONS PNG [PNG ...]");
        int iterations = std::stoi(argv[1]);
        if (iterations < 1) throw std::runtime_error("iterations must be positive");
        std::cout << "png,width,height,new_calls_per_frame,new_bytes_per_frame\n"
            << std::fixed << std::setprecision(1);
        for (int file = 2; file < argc; ++file) {
            auto image = read_png(argv[file]);
            for (std::size_t i = 0; i < image.rgba.size(); i += 4) std::swap(image.rgba[i],image.rgba[i+2]);
            phonesaber::PixelBuffer pixels{image.rgba.data(),image.width,image.height,
                std::size_t(image.width)*4,image.rgba.size(),phonesaber::PixelFormat::bgra};
            phonesaber::FrameProcessor processor;
            std::size_t result_count = 0;
            for (int i = 0; i < 10; ++i) result_count += processor.process(pixels,double(i)/60.0).size();
            calls = 0; bytes = 0; measuring = true;
            for (int i = 0; i < iterations; ++i) result_count += processor.process(pixels,double(i+10)/60.0).size();
            measuring = false;
            std::cout << argv[file] << ',' << image.width << ',' << image.height << ','
                << double(calls)/iterations << ',' << double(bytes)/iterations << '\n';
            std::cerr << argv[file] << ": results=" << result_count << '\n';
        }
    } catch (const std::exception& error) { measuring = false; std::cerr << error.what() << '\n'; return 2; }
    return 0;
}
