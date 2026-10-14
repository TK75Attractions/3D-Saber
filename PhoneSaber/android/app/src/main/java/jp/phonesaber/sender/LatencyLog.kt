package jp.phonesaber.sender

import java.util.Locale

/** main thread専用。送信中10秒ごとに1行、停止後も直近5分をコピーできる。 */
class LatencyLog {
    companion object { const val TAG = "PhoneSaberLatency" }
    private var lastAt = 0.0
    private val lines = ArrayDeque<Pair<Double, String>>()

    fun start(at: Double) { lastAt = at }
    fun due(at: Double, sending: Boolean): Boolean {
        if (!sending || at - lastAt < 10.0) return false
        lastAt = at
        return true
    }
    fun append(at: Double, line: String) {
        lines.addLast(at to line)
        prune(at)
        while (lines.size > 60) lines.removeFirst()
    }
    fun copy(at: Double): String {
        prune(at)
        return lines.joinToString("\n") { it.second }
    }
    private fun prune(at: Double) {
        while (lines.isNotEmpty() && lines.first().first <= at - 300.0) lines.removeFirst()
    }
}

object LatencyStatusText {
    fun line(rates: HealthRates, thermal: Int, replyAge: Double?): String {
        fun number(value: Double?) = value?.let { String.format(Locale.US, "%.1f", it) } ?: "UNKNOWN"
        return "capture_fps=${number(rates.captureFps)} analysis_fps=${number(rates.analysisFps)} " +
            "send_red_fps=${number(rates.redSendFps)} send_blue_fps=${number(rates.blueSendFps)} " +
            "capture_to_send_median_ms=${number(rates.medianCaptureToSendMs)} " +
            "capture_to_send_p95_ms=${number(rates.p95CaptureToSendMs)} samples=${rates.sentSamples} " +
            "sensor_to_analyzer_median_ms=${number(rates.medianSensorToAnalyzerMs)} " +
            "sensor_to_analyzer_p95_ms=${number(rates.p95SensorToAnalyzerMs)} " +
            "analyzer_to_jni_end_median_ms=${number(rates.medianAnalyzerToJniEndMs)} " +
            "analyzer_to_jni_end_p95_ms=${number(rates.p95AnalyzerToJniEndMs)} " +
            "jni_end_to_send_median_ms=${number(rates.medianJniEndToSendMs)} " +
            "jni_end_to_send_p95_ms=${number(rates.p95JniEndToSendMs)} stage_samples=${rates.stageSamples} " +
            "dropped_camera_frames_est=${rates.unanalysedEstimate} dropped_udp_frames=${rates.udpDroppedFrames} " +
            "thermal=$thermal(${ThermalLevel.fromStatus(thermal).title}) pc_reply_age_s=${number(replyAge)} " +
            "jni_median_ms=${number(rates.medianJniMs)} window_s=5 drops=session"
    }
}
