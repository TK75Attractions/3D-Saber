#include <jni.h>
#include <phonesaber/core.hpp>
#include <exception>
#include <new>
#include <array>
#include <chrono>
#include <optional>
#include "rgba_rotation.hpp"

namespace {
struct Session {
    phonesaber::FrameProcessor processor;
    phonesaber::android::RgbaRotation rotation;
    // 無視範囲（会場のコーンなど）。Kotlin が変更時だけ解析スレッドから設定する。
    phonesaber::IgnoreZones ignore_zones;
    // 直近に選ばれた候補の範囲を IgnoreZone にしたものと、その処理時刻（解析スレッドだけが読み書き）。
    std::array<std::optional<phonesaber::IgnoreZone>, 2> winner_zone;
    std::array<double, 2> winner_time{};
    // プレイ範囲（剣を振る場所）と、その学習。どちらも解析スレッドだけが読み書きする。
    // 学習中は範囲で絞らない（新しい範囲を前の範囲より広く覚えられるように）。無視範囲は学習中も効く。
    std::optional<phonesaber::PlayArea> play_area;
    std::optional<phonesaber::PlayAreaLearner> learner;
};
Session* session(jlong handle) { return reinterpret_cast<Session*>(handle); }
phonesaber::FrameProcessor* processor(jlong handle) { return &session(handle)->processor; }
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
jobjectArray process_pixels(JNIEnv* env, jlong handle, const phonesaber::PixelBuffer& pixels,
    jdouble time, jint brightness, jint dominance, jboolean mirror_x, jboolean mirror_y, jboolean measurement_mode) {
    const phonesaber::ColorThreshold threshold{static_cast<uint8_t>(brightness),
        static_cast<uint8_t>(dominance), 30};
    // 出力寸法・認識は既定のまま、反転と既存の計測APIを公開する。
    phonesaber::OutputConfig output;
    output.mirror_x = mirror_x == JNI_TRUE;
    output.mirror_y = mirror_y == JNI_TRUE;
    output.measurement_mode = measurement_mode == JNI_TRUE;
    Session* current = session(handle);
    // FrameProcessor::process(pixels, ...) と同じ analyze → process。範囲が無ければ従来と同じ結果。
    const std::optional<phonesaber::PlayArea> area = current->learner ? std::optional<phonesaber::PlayArea>{}
                                                                      : current->play_area;
    const auto analysis = phonesaber::analyze(pixels, threshold, threshold, 2, current->ignore_zones, area);
    if (current->learner) for (int color = 0; color < 2; ++color) if (analysis.selected[color])
        current->learner->add(*analysis.selected[color], pixels.width, pixels.height);
    for (int color = 0; color < 2; ++color) if (analysis.selected_extent[color]) {
        current->winner_zone[color] = phonesaber::ignore_zone_around(*analysis.selected_extent[color],
            pixels.width, pixels.height);
        current->winner_time[color] = time;
    }
    if (output.measurement_mode) {
        // iPhone と同じく検出後・文字列生成直前の Unix epoch。カメラ時刻ではない。
        const double epoch = std::chrono::duration<double>(
            std::chrono::system_clock::now().time_since_epoch()).count();
        return results(env, current->processor.process(analysis, pixels.width, pixels.height, time, output, {epoch, epoch}));
    }
    return results(env, current->processor.process(analysis, pixels.width, pixels.height, time, output));
}

}
extern "C" JNIEXPORT jlong JNICALL
Java_jp_phonesaber_sender_NativeCore_create(JNIEnv* env, jobject) {
    try { return reinterpret_cast<jlong>(new Session()); }
    catch (const std::exception& error) {
        fail(env, "java/lang/IllegalStateException", error.what()); return 0;
    }
}
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeCore_destroy(JNIEnv*, jobject, jlong handle) {
    delete session(handle);
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
        return process_pixels(env, handle, pixels, time, brightness, dominance, mirror_x, mirror_y, measurement_mode);
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

extern "C" JNIEXPORT jobjectArray JNICALL
Java_jp_phonesaber_sender_NativeCore_processRotated(JNIEnv* env, jobject, jlong handle,
    jobject buffer, jint position, jint remaining, jint width, jint height, jint stride,
    jint degrees, jdouble time, jint brightness, jint dominance,
    jboolean mirror_x, jboolean mirror_y, jboolean measurement_mode) {
    const auto* data = static_cast<const uint8_t*>(env->GetDirectBufferAddress(buffer));
    const jlong capacity = env->GetDirectBufferCapacity(buffer);
    if (!handle || !data || position < 0 || remaining < 0 || position > capacity || remaining > capacity-position ||
        stride < 0 || brightness < 0 || brightness > 255 || dominance < 0 || dominance > 255) {
        fail(env, "java/lang/IllegalArgumentException", "Invalid direct RGBA buffer or threshold");
        return nullptr;
    }
    try {
        const auto pixels = session(handle)->rotation.orient(data+position, static_cast<std::size_t>(remaining),
            width, height, static_cast<std::size_t>(stride), degrees);
        return process_pixels(env, handle, pixels, time, brightness, dominance, mirror_x, mirror_y, measurement_mode);
    } catch (const std::invalid_argument& error) {
        fail(env, "java/lang/IllegalArgumentException", error.what()); return nullptr;
    } catch (const std::exception& error) {
        fail(env, "java/lang/IllegalStateException", error.what()); return nullptr;
    }
}

// zones: 色ごとに [min_x, min_y, max_x, max_y] × n（0..1、解析フレーム）。null は範囲なし。
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeCore_setIgnoreZones(JNIEnv* env, jobject, jlong handle,
    jdoubleArray red, jdoubleArray blue) {
    if (!handle) { fail(env, "java/lang/IllegalStateException", "Closed core"); return; }
    phonesaber::IgnoreZones zones;
    jdoubleArray arrays[2] = {red, blue};
    for (int color = 0; color < 2; ++color) {
        if (!arrays[color]) continue;
        const jsize length = env->GetArrayLength(arrays[color]);
        if (length % 4 != 0 || length > 4 * 64) {
            fail(env, "java/lang/IllegalArgumentException", "Invalid ignore zones"); return;
        }
        std::vector<jdouble> values(static_cast<std::size_t>(length));
        if (length > 0) env->GetDoubleArrayRegion(arrays[color], 0, length, values.data());
        if (env->ExceptionCheck()) return;
        for (jsize i = 0; i < length; i += 4)
            zones[color].push_back({values[i], values[i + 1], values[i + 2], values[i + 3]});
    }
    session(handle)->ignore_zones = std::move(zones);
}
// 直近 max_age 秒以内に選ばれた候補の範囲（余白込み）を out[4] に書く。無ければ false。
extern "C" JNIEXPORT jboolean JNICALL
Java_jp_phonesaber_sender_NativeCore_winnerZone(JNIEnv* env, jobject, jlong handle, jint color,
    jdouble now, jdouble max_age, jdoubleArray out) {
    if (!handle || color < 0 || color > 1 || !out || env->GetArrayLength(out) < 4) return JNI_FALSE;
    const Session* current = session(handle);
    const auto& zone = current->winner_zone[color];
    if (!zone || now - current->winner_time[color] > max_age) return JNI_FALSE;
    const jdouble values[4] = {zone->min_x, zone->min_y, zone->max_x, zone->max_y};
    env->SetDoubleArrayRegion(out, 0, 4, values);
    return env->ExceptionCheck() ? JNI_FALSE : JNI_TRUE;
}
// プレイ範囲 [min_x, min_y, max_x, max_y]（0..1、解析フレーム）。null は範囲なし（従来と同じ選び方）。
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeCore_setPlayArea(JNIEnv* env, jobject, jlong handle, jdoubleArray area) {
    if (!handle) { fail(env, "java/lang/IllegalStateException", "Closed core"); return; }
    if (!area) { session(handle)->play_area.reset(); return; }
    if (env->GetArrayLength(area) != 4) { fail(env, "java/lang/IllegalArgumentException", "Invalid play area"); return; }
    jdouble values[4];
    env->GetDoubleArrayRegion(area, 0, 4, values);
    if (env->ExceptionCheck()) return;
    session(handle)->play_area = phonesaber::PlayArea{values[0], values[1], values[2], values[3]};
}
// 学習を始める（前の学習は捨てる）。解析スレッドから呼ぶ。
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeCore_startAreaLearning(JNIEnv* env, jobject, jlong handle) {
    if (!handle) { fail(env, "java/lang/IllegalStateException", "Closed core"); return; }
    session(handle)->learner = phonesaber::PlayAreaLearner{};
}
// 学習を終える。学習していなければ -1。数えた winner の数を返し、足りていれば out[4] に範囲を書く。
extern "C" JNIEXPORT jint JNICALL
Java_jp_phonesaber_sender_NativeCore_finishAreaLearning(JNIEnv* env, jobject, jlong handle, jdoubleArray out) {
    if (!handle || !out || env->GetArrayLength(out) < 4) return -1;
    Session* current = session(handle);
    if (!current->learner) return -1;
    const auto learner = *current->learner;
    current->learner.reset();
    if (const auto area = learner.result()) {
        const jdouble values[4] = {area->min_x, area->min_y, area->max_x, area->max_y};
        env->SetDoubleArrayRegion(out, 0, 4, values);
        if (env->ExceptionCheck()) return -1;
    }
    return static_cast<jint>(learner.samples);
}
