#pragma once
#include <phonesaber/core.hpp>
#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace phonesaber::android {
// カメラセッションだけが所有する作業領域。入力の position/limit は JNI 側で適用する。
class RgbaRotation {
    std::vector<uint8_t> storage_;
    template<int degrees>
    void rotate(const uint8_t* data, int width, int height, std::size_t stride, int out_width) {
        // 向きの分岐は画素ループの外。180度は連続行、90/270度は小区画で両画像を再利用する。
        constexpr int tile = degrees == 180 ? 32768 : 16;
        for (int y0 = 0; y0 < height; y0 += tile) for (int x0 = 0; x0 < width; x0 += tile)
            for (int y = y0; y < std::min(y0+tile,height); ++y)
                for (int x = x0; x < std::min(x0+tile,width); ++x) {
                    const int ox = degrees == 90 ? height-1-y : degrees == 180 ? width-1-x : y;
                    const int oy = degrees == 90 ? x : degrees == 180 ? height-1-y : width-1-x;
                    // カメラの非整列入力でも alias/alignment 違反を起こさず4byteをコピー。
                    std::memcpy(storage_.data()+(std::size_t(oy)*out_width+ox)*4,
                                data+std::size_t(y)*stride+std::size_t(x)*4,4);
                }
    }
public:
    PixelBuffer orient(const uint8_t* data, std::size_t size, int width, int height,
                       std::size_t stride, int degrees) {
        if (!data || width < 1 || height < 1 || width > 32768 || height > 32768 ||
            stride < std::size_t(width)*4 || (degrees != 0 && degrees != 90 && degrees != 180 && degrees != 270) ||
            size < std::size_t(width)*4 || std::size_t(height-1) > (size-std::size_t(width)*4)/stride)
            throw std::invalid_argument("Invalid direct RGBA plane or rotation");
        // 0度かつ最終行のpaddingまである場合は従来通りコピーしない。
        if (degrees == 0 && stride <= size/std::size_t(height))
            return {data,width,height,stride,size,PixelFormat::rgba};
        const int out_width = degrees % 180 == 0 ? width : height;
        const int out_height = degrees % 180 == 0 ? height : width;
        const std::size_t bytes = std::size_t(out_width)*out_height*4;
        if (bytes > INT32_MAX) throw std::invalid_argument("RGBA rotation exceeds buffer limit");
        storage_.resize(bytes);
        if (degrees == 0) {
            for (int y = 0; y < height; ++y)
                std::memcpy(storage_.data()+std::size_t(y)*out_width*4,data+std::size_t(y)*stride,std::size_t(width)*4);
        } else if (degrees == 90) {
            rotate<90>(data,width,height,stride,out_width);
        } else if (degrees == 180) {
            rotate<180>(data,width,height,stride,out_width);
        } else {
            rotate<270>(data,width,height,stride,out_width);
        }
        return {storage_.data(),out_width,out_height,std::size_t(out_width)*4,bytes,PixelFormat::rgba};
    }
};
} // namespace phonesaber::android
