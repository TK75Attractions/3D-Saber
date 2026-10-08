#include "../../main/cpp/rgba_rotation.hpp"
#include <jni.h>

// 同じ回転実装を Kotlin の参照と全byte比較するための Debug 専用 probe。
extern "C" JNIEXPORT jlong JNICALL
Java_jp_phonesaber_sender_NativeRotationProbe_create(JNIEnv*, jobject) {
    return reinterpret_cast<jlong>(new phonesaber::android::RgbaRotation());
}
extern "C" JNIEXPORT void JNICALL
Java_jp_phonesaber_sender_NativeRotationProbe_destroy(JNIEnv*, jobject, jlong handle) {
    delete reinterpret_cast<phonesaber::android::RgbaRotation*>(handle);
}
extern "C" JNIEXPORT jobject JNICALL
Java_jp_phonesaber_sender_NativeRotationProbe_rotate(JNIEnv* env, jobject, jlong handle,
    jobject buffer, jint position, jint remaining, jint width, jint height, jint stride, jint degrees) {
    try {
        const auto* data = static_cast<const uint8_t*>(env->GetDirectBufferAddress(buffer));
        const jlong capacity = env->GetDirectBufferCapacity(buffer);
        if (!handle || !data || position < 0 || remaining < 0 || position > capacity || remaining > capacity-position || stride < 0)
            throw std::invalid_argument("Invalid test plane");
        const auto pixels = reinterpret_cast<phonesaber::android::RgbaRotation*>(handle)->orient(
            data+position, remaining, width, height, stride, degrees);
        return env->NewDirectByteBuffer(const_cast<uint8_t*>(pixels.data), pixels.size_bytes);
    } catch (const std::exception& error) {
        jclass type = env->FindClass("java/lang/IllegalArgumentException");
        if (type) { env->ThrowNew(type,error.what()); env->DeleteLocalRef(type); }
        return nullptr;
    }
}
