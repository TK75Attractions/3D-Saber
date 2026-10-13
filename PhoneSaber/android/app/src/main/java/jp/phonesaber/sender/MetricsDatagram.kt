package jp.phonesaber.sender

// 診断専用。座標送信workerでは整形しない。
data class MetricsDatagram(val station: String = "?", val platform: String = "android", val app: String = "unknown",
    val fpsMode: Double = 60.0, val captureFps: Double? = 0.0, val redFps: Double = 0.0, val blueFps: Double = 0.0,
    val median: Double? = null, val p95: Double? = null, val samples: Int = 0,
    val rtt: Double? = null, val rttSamples: Int = 0, val rttAge: Double? = null,
    val thermal: String = "unknown", val battery: Double? = null, val lowLatencyTest: Boolean = false,
    val c2sEnd: String = "os-send") {
    fun line(): String? {
        if (!fpsMode.isFinite() || fpsMode !in 1.0..240.0) return null
        val c2sValid = median != null && p95 != null && samples in 1..2400 &&
            number(median, 2000.0) != "unknown" && number(p95, 2000.0) != "unknown" && p95 >= median
        val rttValid = rtt != null && rttAge != null && rttSamples in 1..2400 &&
            number(rtt, 1000.0) != "unknown" && number(rttAge, 10.0) != "unknown"
        val text = "PHONESABER_METRICS 1 station=${atom(station, 16)} platform=$platform app=${atom(app, 24)}" +
            " fps-mode=${kotlin.math.floor(fpsMode + 0.5).toInt()} capture-fps=${number(captureFps, 1000.0)}" +
            " send-red-fps=${number(redFps, 1000.0)} send-blue-fps=${number(blueFps, 1000.0)}" +
            " c2s-median-ms=${number(median.takeIf { c2sValid }, 2000.0)} c2s-p95-ms=${number(p95.takeIf { c2sValid }, 2000.0)} c2s-n=${if (c2sValid) samples else 0}" +
            " rtt-ms=${number(rtt.takeIf { rttValid }, 1000.0)} rtt-n=${if (rttValid) rttSamples else 0} rtt-age-s=${number(rttAge.takeIf { rttValid }, 10.0)}" +
            " route=LAN thermal=${atom(thermal, 16)} battery=${number(battery, 100.0)} low-latency-test=${if (lowLatencyTest) 1 else 0} c2s-end=$c2sEnd"
        return text.takeIf { it.length < 400 && it.all { c -> c.code in 32..126 } }
    }
    companion object {
        private fun atom(value: String, limit: Int) = value.take(limit).map {
            if (it in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-?") it else '_'
        }.joinToString("").ifEmpty { "?" }
        private fun number(value: Double?, max: Double) = value?.takeIf { it.isFinite() && it in 0.0..max }
            ?.let { val tenths = (it * 10 + 0.5).toInt(); "${tenths / 10}.${tenths % 10}" } ?: "unknown"
    }
}

class MetricsCadence {
    private var last: Double? = null
    fun due(now: Double, sending: Boolean): Boolean {
        if (!sending) { last = null; return false }
        val previous = last
        if (previous == null) { last = now; return false }
        if (now - previous < 5) return false
        last = now; return true
    }
}

// 同期不要の単調時計。直近10秒、最大16応答だけ保持する。
class ProbeRoundTrip {
    private val values = ArrayDeque<Pair<Double, Double>>()
    @Synchronized fun record(sent: Double, received: Double) {
        if (received < sent || received - sent > 1) return
        values.addLast(received to (received - sent) * 1000)
        while (values.size > 16) values.removeFirst()
    }
    @Synchronized fun snapshot(now: Double): Triple<Double?, Int, Double?> {
        val live = values.filter { now - it.first <= 10 }
        val sorted = live.map { it.second }.sorted()
        val n = sorted.size
        if (n == 0) return Triple(null, 0, null)
        return Triple(if (n % 2 == 0) (sorted[n / 2 - 1] + sorted[n / 2]) / 2 else sorted[n / 2], n, now - live.last().first)
    }
}
