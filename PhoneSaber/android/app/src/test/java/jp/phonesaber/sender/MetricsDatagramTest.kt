package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.Locale

class MetricsDatagramTest {
    @Test fun sharedFormatVectorsAreLocaleIndependent() {
        val locale = Locale.getDefault()
        try {
            Locale.setDefault(Locale.FRANCE)
            val root = generateSequence(File(checkNotNull(System.getProperty("user.dir")))) { it.parentFile }
                .first { File(it, "PhoneSaber/tools/fixtures/protocol_metrics.tsv").isFile }
            File(root, "PhoneSaber/tools/fixtures/protocol_metrics.tsv").readLines().drop(1).forEach { row ->
                val f = row.split('\t')
                if (f[3] != "1") return@forEach
                val v = f[1].split(' ').drop(2).associate { it.substringBefore('=') to it.substringAfter('=') }
                val m = MetricsDatagram(station = v.getValue("station"), platform = v.getValue("platform"), app = v.getValue("app"),
                    fpsMode = v.getValue("fps-mode").toDouble(), captureFps = v.getValue("capture-fps").toDoubleOrNull(),
                    redFps = v.getValue("send-red-fps").toDouble(), blueFps = v.getValue("send-blue-fps").toDouble(),
                    median = v.getValue("c2s-median-ms").toDoubleOrNull(), p95 = v.getValue("c2s-p95-ms").toDoubleOrNull(),
                    samples = v.getValue("c2s-n").toInt(), rtt = v.getValue("rtt-ms").toDoubleOrNull(),
                    rttSamples = v.getValue("rtt-n").toInt(), rttAge = v.getValue("rtt-age-s").toDoubleOrNull(),
                    thermal = v.getValue("thermal"), battery = v.getValue("battery").toDoubleOrNull(), c2sEnd = v.getValue("c2s-end"))
                assertEquals(f[0], f[1], m.line())
            }
        } finally { Locale.setDefault(locale) }
    }
    @Test fun rttIsBoundedAndExpires() {
        val meter = ProbeRoundTrip()
        meter.record(0.0, 2.0); assertEquals(0, meter.snapshot(2.0).second)
        meter.record(2.0, 2.01); meter.record(3.0, 3.03)
        assertEquals(20.0, meter.snapshot(3.1).first!!, 1e-9)
        assertEquals(0, meter.snapshot(14.0).second)
        repeat(100) { meter.record(20.0 + it / 100.0, 20.001 + it / 100.0) }
        assertEquals(16, meter.snapshot(21.0).second)
    }
    @Test fun cadenceAndInvalidValues() {
        val cadence = MetricsCadence()
        assertFalse(cadence.due(0.0, true)); assertFalse(cadence.due(4.99, true))
        assertTrue(cadence.due(5.0, true)); assertFalse(cadence.due(5.0, true))
        assertFalse(cadence.due(10.0, false)); assertFalse(cadence.due(11.0, true))
        val line = MetricsDatagram(station = "A\n台", app = "x".repeat(100), median = Double.NaN, p95 = Double.POSITIVE_INFINITY, samples = 20).line()!!
        assertTrue(line.contains("c2s-n=0")); assertTrue(line.length < 400)
        assertNull(MetricsDatagram(fpsMode = Double.NaN).line())
        assertTrue(MetricsDatagram(median = 2.25, p95 = 2.25, samples = 1).line()!!.contains("c2s-median-ms=2.3"))
    }
    @Test fun formatterHostCost() {
        val metrics = MetricsDatagram(median = 37.0, p95 = 44.0, samples = 300)
        repeat(100) { check(metrics.line() != null) }
        val started = System.nanoTime()
        repeat(10000) { check(metrics.line() != null) }
        println("Kotlin formatter host mean-us=${(System.nanoTime() - started) / 1e7}")
    }
}
