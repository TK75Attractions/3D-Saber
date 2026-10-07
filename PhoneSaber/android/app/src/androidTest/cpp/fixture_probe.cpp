#include <jni.h>
#include <phonesaber/core.hpp>
#include <exception>
#include <sstream>
#include <stdexcept>

namespace {
void quoted(std::ostream& out, const std::string& value) {
    out << '"';
    for (char c : value) {
        if (c == '"' || c == '\\') out << '\\';
        out << c;
    }
    out << '"';
}
void point(std::ostream& out, phonesaber::PixelPoint p) {
    out << "{\"x\":" << p.x << ",\"y\":" << p.y << '}';
}
}

// 本番NativeCore.processとは別に、同じanalyzeの元画像座標・候補種別だけを検査する。
extern "C" JNIEXPORT jstring JNICALL
Java_jp_phonesaber_sender_NativeFixtureProbe_analyze(JNIEnv* env, jobject,
    jobject buffer, jint width, jint height, jint stride) {
    const auto* data = static_cast<const uint8_t*>(env->GetDirectBufferAddress(buffer));
    const jlong capacity = env->GetDirectBufferCapacity(buffer);
    try {
        if (!data || width <= 0 || height <= 0 || width > 32768 || height > 32768 ||
            stride < static_cast<int64_t>(width) * 4 ||
            capacity < static_cast<int64_t>(height) * stride) {
            throw std::invalid_argument("Invalid fixture RGBA buffer");
        }
        const auto analysis = phonesaber::analyze({data, width, height,
            static_cast<std::size_t>(stride), static_cast<std::size_t>(capacity),
            phonesaber::PixelFormat::rgba});
        std::ostringstream out;
        out << '{';
        for (int color = 0; color < 2; ++color) {
            if (color) out << ',';
            out << (color == 0 ? "\"red\":" : "\"blue\":") << "{\"selected\":";
            if (analysis.selected[color]) {
                out << '[';
                point(out, analysis.selected[color]->first);
                out << ',';
                point(out, analysis.selected[color]->second);
                out << ']';
            } else out << "null";
            out << ",\"candidateType\":";
            const phonesaber::Candidate* selected = nullptr;
            for (const auto& candidate : analysis.candidates[color]) {
                if (candidate.eligible) { selected = &candidate; break; }
            }
            if (selected) quoted(out, selected->source); else out << "null";
            out << ",\"rejectedCandidateTypes\":[";
            bool first = true;
            for (const auto& candidate : analysis.candidates[color]) {
                if (candidate.eligible) continue;
                if (!first) out << ',';
                quoted(out, candidate.source);
                first = false;
            }
            out << "]}";
        }
        out << '}';
        return env->NewStringUTF(out.str().c_str());
    } catch (const std::exception& error) {
        jclass type = env->FindClass("java/lang/IllegalStateException");
        if (type) {
            env->ThrowNew(type, error.what());
            env->DeleteLocalRef(type);
        }
        return nullptr;
    }
}
