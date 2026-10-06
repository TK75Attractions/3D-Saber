#include "png.hpp"
#include <algorithm>
#include <array>
#include <fstream>
#include <iterator>
#include <limits>
#include <stdexcept>
#include <zlib.h>

static uint32_t be32(const uint8_t* p) {
    return uint32_t(p[0])<<24 | uint32_t(p[1])<<16 | uint32_t(p[2])<<8 | uint32_t(p[3]);
}
static int paeth(int a, int b, int c) {
    int p = a+b-c, pa = std::abs(p-a), pb = std::abs(p-b), pc = std::abs(p-c);
    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}
PNGImage read_png(const std::string& path) {
    std::ifstream file(path,std::ios::binary);
    if (!file) throw std::runtime_error("cannot open PNG: "+path);
    std::vector<uint8_t> bytes{std::istreambuf_iterator<char>(file),std::istreambuf_iterator<char>()};
    constexpr std::array<uint8_t,8> signature{137,80,78,71,13,10,26,10};
    if (bytes.size() < 8 || !std::equal(signature.begin(),signature.end(),bytes.begin())) throw std::runtime_error("invalid PNG signature");
    int w = 0, h = 0, channels = 0;
    bool ihdr = false, ended = false;
    std::vector<uint8_t> compressed;
    std::size_t pos = 8;
    while (pos < bytes.size()) {
        if (bytes.size()-pos < 12) throw std::runtime_error("truncated PNG chunk");
        uint32_t n = be32(bytes.data()+pos);
        if (std::size_t(n) > bytes.size()-pos-12) throw std::runtime_error("truncated PNG data");
        auto* type = bytes.data()+pos+4; auto* data = bytes.data()+pos+8;
        auto crc = crc32(0,type,n+4);
        if (crc != be32(data+n)) throw std::runtime_error("PNG CRC mismatch");
        std::string name(reinterpret_cast<char*>(type),4);
        if (name == "IHDR") {
            if (ihdr || pos != 8 || n != 13) throw std::runtime_error("invalid PNG IHDR");
            uint32_t width = be32(data), height = be32(data+4);
            if (!width || !height || width > 32768 || height > 32768 || uint64_t(width)*height > 100000000) throw std::runtime_error("unsupported PNG dimensions");
            if (data[8] != 8 || (data[9] != 2 && data[9] != 6) || data[10] || data[11] || data[12]) throw std::runtime_error("requires non-interlaced 8-bit RGB/RGBA PNG");
            w = int(width); h = int(height); channels = data[9] == 2 ? 3 : 4; ihdr = true;
        } else if (name == "IDAT") {
            if (!ihdr) throw std::runtime_error("IDAT before IHDR");
            compressed.insert(compressed.end(),data,data+n);
        } else if (name == "IEND") {
            if (n != 0) throw std::runtime_error("invalid IEND");
            ended = true; pos += 12; break;
        } else if (name == "tRNS" || !(type[0]&32)) throw std::runtime_error("unsupported critical PNG chunk: "+name);
        pos += std::size_t(n)+12;
    }
    if (!ended || !ihdr || compressed.empty() || pos != bytes.size()) throw std::runtime_error("incomplete PNG");
    std::size_t stride = std::size_t(w)*channels;
    std::vector<uint8_t> filtered((stride+1)*h), pixels(stride*h);
    uLongf size = filtered.size();
    if (uncompress(filtered.data(),&size,compressed.data(),compressed.size()) != Z_OK || size != filtered.size()) throw std::runtime_error("invalid PNG zlib stream");
    for (int y = 0; y < h; ++y) {
        auto* row = pixels.data()+std::size_t(y)*stride;
        const auto* input = filtered.data()+std::size_t(y)*(stride+1);
        int filter = *input++;
        if (filter > 4) throw std::runtime_error("invalid PNG filter");
        for (std::size_t x = 0; x < stride; ++x) {
            int a = x >= std::size_t(channels) ? row[x-channels] : 0;
            const auto* previous = y > 0 ? pixels.data()+std::size_t(y-1)*stride : nullptr;
            int b = previous ? previous[x] : 0;
            int c = previous && x >= std::size_t(channels) ? previous[x-channels] : 0;
            int predictor = filter == 0 ? 0 : filter == 1 ? a : filter == 2 ? b : filter == 3 ? (a+b)/2 : paeth(a,b,c);
            row[x] = uint8_t(int(input[x])+predictor);
        }
    }
    PNGImage result{w,h,std::vector<uint8_t>(std::size_t(w)*h*4)};
    for (std::size_t i = 0; i < std::size_t(w)*h; ++i) {
        result.rgba[i*4] = pixels[i*channels]; result.rgba[i*4+1] = pixels[i*channels+1]; result.rgba[i*4+2] = pixels[i*channels+2];
        result.rgba[i*4+3] = channels == 4 ? pixels[i*channels+3] : 255;
    }
    return result;
}
