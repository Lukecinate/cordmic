// Low-latency microphone capture with AAudio (blocking read from a dedicated thread).
// Kotlin side: dev.cordmic.AAudioCapture
#include <jni.h>
#include <aaudio/AAudio.h>
#include <android/log.h>
#include <vector>
#include <dlfcn.h>

#define TAG "CordMicNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

static AAudioStream *gStream = nullptr;

// AAudioStreamBuilder_setPrivacySensitive is API 30; minSdk is 29, so look it up at run time (0.6.0).
// A privacy-sensitive capture cannot be silenced or taken over by the Assistant hotword or another app.
using SetPrivacyFn = void (*)(AAudioStreamBuilder *, bool);
static void setPrivacySensitive(AAudioStreamBuilder *b) {
    static SetPrivacyFn fn = reinterpret_cast<SetPrivacyFn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setPrivacySensitive"));
    if (fn) fn(b, true);
}
static std::vector<int16_t> gTmp;

extern "C" JNIEXPORT jint JNICALL
Java_dev_cordmic_AAudioCapture_nativeOpen(JNIEnv *, jclass, jint sampleRate, jint channels,
                                         jint inputPreset, jboolean exclusive, jint performanceMode) {
    if (gStream) return 0;
    auto open = [&](bool capCapacity) -> aaudio_result_t {
        AAudioStreamBuilder *b = nullptr;
        aaudio_result_t hr = AAudio_createStreamBuilder(&b);
        if (hr != AAUDIO_OK) return hr;
        AAudioStreamBuilder_setDirection(b, AAUDIO_DIRECTION_INPUT);
        AAudioStreamBuilder_setPerformanceMode(b, performanceMode);
        AAudioStreamBuilder_setSharingMode(b, exclusive ? AAUDIO_SHARING_MODE_EXCLUSIVE
                                                        : AAUDIO_SHARING_MODE_SHARED);
        AAudioStreamBuilder_setFormat(b, AAUDIO_FORMAT_PCM_I16);
        AAudioStreamBuilder_setSampleRate(b, sampleRate);
        AAudioStreamBuilder_setChannelCount(b, channels);
        AAudioStreamBuilder_setInputPreset(b, inputPreset);  // API 28+
        setPrivacySensitive(b);                              // API 30+, no-op before
        if (capCapacity) AAudioStreamBuilder_setBufferCapacityInFrames(b, sampleRate / 50); // 20 ms ceiling
        hr = AAudioStreamBuilder_openStream(b, &gStream);
        AAudioStreamBuilder_delete(b);
        return hr;
    };
    // Prefer a short capacity so the HAL cannot queue a long capture buffer. Some devices reject the hint.
    aaudio_result_t r = open(true);
    if (r != AAUDIO_OK) {
        gStream = nullptr;
        r = open(false);
    }
    if (r != AAUDIO_OK) {
        LOGE("openStream failed: %s", AAudio_convertResultToText(r));
        gStream = nullptr;
        return r;
    }
    if (AAudioStream_getSampleRate(gStream) != sampleRate ||
        AAudioStream_getChannelCount(gStream) != channels) {
        LOGE("format not honoured: %d Hz %d ch", AAudioStream_getSampleRate(gStream),
             AAudioStream_getChannelCount(gStream));
        AAudioStream_close(gStream);
        gStream = nullptr;
        return AAUDIO_ERROR_INVALID_FORMAT;
    }
    r = AAudioStream_requestStart(gStream);
    if (r != AAUDIO_OK) {
        AAudioStream_close(gStream);
        gStream = nullptr;
        return r;
    }
    // One burst is the smallest buffer the stream will accept; a larger one only adds capture latency.
    int32_t burst = AAudioStream_getFramesPerBurst(gStream);
    if (burst > 0) AAudioStream_setBufferSizeInFrames(gStream, burst);
    LOGI("opened: rate=%d burst=%d perf=%d sharing=%d cap=%d buf=%d",
         AAudioStream_getSampleRate(gStream), AAudioStream_getFramesPerBurst(gStream),
         AAudioStream_getPerformanceMode(gStream), AAudioStream_getSharingMode(gStream),
         AAudioStream_getBufferCapacityInFrames(gStream), AAudioStream_getBufferSizeInFrames(gStream));
    return 0;
}

// Reads exactly `frames` frames into dst[offset..] (little-endian s16). Returns frames or <0.
extern "C" JNIEXPORT jint JNICALL
Java_dev_cordmic_AAudioCapture_nativeRead(JNIEnv *env, jclass, jbyteArray dst, jint offset,
                                         jint frames, jlong timeoutNanos) {
    if (!gStream) return AAUDIO_ERROR_INVALID_STATE;
    int32_t ch = AAudioStream_getChannelCount(gStream);
    size_t n = (size_t) frames * ch;
    if (gTmp.size() < n) gTmp.resize(n);
    aaudio_result_t r = AAudioStream_read(gStream, gTmp.data(), frames, timeoutNanos);
    if (r > 0) env->SetByteArrayRegion(dst, offset, r * ch * 2, reinterpret_cast<jbyte *>(gTmp.data()));
    return r;
}

// [sampleRate, framesPerBurst, performanceMode, sharingMode, bufferSize, xRunCount]
// bufferSize is the actual size after setBufferSizeInFrames (one burst), not HAL capacity.
extern "C" JNIEXPORT jintArray JNICALL
Java_dev_cordmic_AAudioCapture_nativeInfo(JNIEnv *env, jclass) {
    jint v[6] = {0, 0, 0, 0, 0, 0};
    if (gStream) {
        v[0] = AAudioStream_getSampleRate(gStream);
        v[1] = AAudioStream_getFramesPerBurst(gStream);
        v[2] = AAudioStream_getPerformanceMode(gStream);
        v[3] = AAudioStream_getSharingMode(gStream);
        v[4] = AAudioStream_getBufferSizeInFrames(gStream);
        v[5] = AAudioStream_getXRunCount(gStream);
    }
    jintArray a = env->NewIntArray(6);
    env->SetIntArrayRegion(a, 0, 6, v);
    return a;
}

extern "C" JNIEXPORT jint JNICALL
Java_dev_cordmic_AAudioCapture_nativeSessionId(JNIEnv *, jclass) {
    if (!gStream) return 0;
    int32_t id = AAudioStream_getSessionId(gStream);
    return id > 0 ? id : 0;
}

extern "C" JNIEXPORT void JNICALL
Java_dev_cordmic_AAudioCapture_nativeClose(JNIEnv *, jclass) {
    if (gStream) {
        AAudioStream_requestStop(gStream);
        AAudioStream_close(gStream);
        gStream = nullptr;
    }
}
