#include <jni.h>
#include <phonesaber/core.hpp>
#include <exception>
#include <new>
#include <chrono>

namespace {
phonesaber::FrameProcessor* processor(jlong handle) {
    return reinterpret_cast<phonesaber::FrameProcessor*>(handle);
}
void fail(JNIEnv* env, const char* kind, const char* message) {
    jclass type = env->FindClass(kind);
    if (type) { env->ThrowNew(type, message); env->DeleteLocalRef(type); }
}
jobjectArray results(JNIEnv* env, const std::vector<phonesaber::FrameResult>& values) {
    jclass type = env->FindClass("jp/phonesaber/sender/NativeResult");
    if (!type) return nullptr;
    jmethodID ctor = env->GetMethodID(type, "<init>", "(IZZILjava/lang/String;)V");
    if (!ctor) { env->DeleteLocalRef(type); return nullptr; }
    jobjectArray array = env->NewObjectArray(static_cast<jsize>(values.size()), type, nullptr);
    if (!array) { env->DeleteLocalRef(type); return nullptr; }
    for (std::size_t i = 0; i < values.size(); ++i) {
        const auto& result = values[i];
        jstring text = result.text ? env->NewStringUTF(result.text->c_str()) : nullptr;
        if (env->ExceptionCheck()) break;
        jobject value = env->NewObject(type, ctor, static_cast<jint>(result.color),
            static_cast<jboolean>(result.fresh), static_cast<jboolean>(result.predicted),
            static_cast<jint>(result.port), text);
        if (text) env->DeleteLocalRef(text);
        if (!value) break;
        env->SetObjectArrayElement(array, static_cast<jsize>(i), value);
        env->DeleteLocalRef(value);
        if (env->ExceptionCheck()) break;
    }
    env->DeleteLocalRef(type);
    return array;
}
}
extern "C" JNIEXPORT jlong JNICALL
Java_jp_phonesaber_sender_NativeCore_create(JNIEnv* env, jobject) {
    try { return reinterpret_cast<jlong>(new phonesaber::FrameProcessor()); }
    catch (const std::exception& error) {
        fail(env, "java/lang/IllegalStateException", error.what()); return 0;
    }
}
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeCore_destroy(JNIEnv*, jobject, jlong handle) {
    delete processor(handle);
}
extern "C" JNIEXPORT jobjectArray JNICALL
Java_jp_phonesaber_sender_NativeCore_process(JNIEnv* env, jobject, jlong handle,
    jobject buffer, jint width, jint height, jint stride, jdouble time,
    jint brightness, jint dominance, jboolean mirror_x, jboolean mirror_y, jboolean measurement_mode) {
    const auto* data = static_cast<const uint8_t*>(env->GetDirectBufferAddress(buffer));
    const jlong capacity = env->GetDirectBufferCapacity(buffer);
    if (!handle || !data || width <= 0 || height <= 0 || width > 32768 || height > 32768 ||
        stride < static_cast<int64_t>(width) * 4 ||
        capacity < static_cast<int64_t>(height) * stride ||
        brightness < 0 || brightness > 255 || dominance < 0 || dominance > 255) {
        fail(env, "java/lang/IllegalArgumentException", "Invalid direct RGBA buffer or threshold");
        return nullptr;
    }
    try {
        const phonesaber::PixelBuffer pixels{data, width, height, static_cast<std::size_t>(stride),
            static_cast<std::size_t>(capacity), phonesaber::PixelFormat::rgba};
        const phonesaber::ColorThreshold threshold{static_cast<uint8_t>(brightness),
            static_cast<uint8_t>(dominance), 30};
        // 出力寸法・認識は既定のまま、反転と既存の計測APIを公開する。
        phonesaber::OutputConfig output;
        output.mirror_x = mirror_x == JNI_TRUE;
        output.mirror_y = mirror_y == JNI_TRUE;
        output.measurement_mode = measurement_mode == JNI_TRUE;
        if (output.measurement_mode) {
            const auto analysis = phonesaber::analyze(pixels, threshold, threshold);
            // iPhone と同じく検出後・文字列生成直前の Unix epoch。カメラ時刻ではない。
            const double epoch = std::chrono::duration<double>(
                std::chrono::system_clock::now().time_since_epoch()).count();
            return results(env, processor(handle)->process(analysis, width, height, time, output, {epoch, epoch}));
        }
        return results(env, processor(handle)->process(pixels, time, output, {}, threshold, threshold));
    } catch (const std::exception& error) {
        fail(env, "java/lang/IllegalStateException", error.what()); return nullptr;
    }
}
extern "C" JNIEXPORT jobjectArray JNICALL
Java_jp_phonesaber_sender_NativeCore_expire(JNIEnv* env, jobject, jlong handle, jdouble time) {
    if (!handle) { fail(env, "java/lang/IllegalStateException", "Closed core"); return nullptr; }
    try { return results(env, processor(handle)->expire(time)); }
    catch (const std::exception& error) {
        fail(env, "java/lang/IllegalStateException", error.what()); return nullptr;
    }
}
extern "C" JNIEXPORT jdouble JNICALL
Java_jp_phonesaber_sender_NativeCore_nextExpiry(JNIEnv*, jobject, jlong handle) {
    return handle ? processor(handle)->next_expiry().value_or(-1.0) : -1.0;
}
