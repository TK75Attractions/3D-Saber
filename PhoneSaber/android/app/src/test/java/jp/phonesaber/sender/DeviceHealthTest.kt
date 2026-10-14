package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class DeviceHealthTest {
    @Test fun stageTimesUseTheSameSentFrameAndIncludeDispatchWait() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        // 露光開始→解析開始80ms、解析準備/JNI 7ms、送信待ち/OS送信8ms。
        meter.sent(100.095, 100_000_000_000L, 100_095_000_000L,
            100_080_000_000L, 100_087_000_000L)
        val rates = meter.snapshot(101.0)
        assertEquals(80.0, rates.medianSensorToAnalyzerMs!!, 0.0)
        assertEquals(7.0, rates.medianAnalyzerToJniEndMs!!, 0.0)
        assertEquals(8.0, rates.medianJniEndToSendMs!!, 0.0)
        assertEquals(95.0, rates.medianCaptureToSendMs!!, 0.0)
        assertEquals(1, rates.stageSamples)
        val line = LatencyStatusText.line(rates, 0, null)
        for (field in listOf("sensor_to_analyzer_median_ms=80.0", "sensor_to_analyzer_p95_ms=80.0",
            "analyzer_to_jni_end_median_ms=7.0", "analyzer_to_jni_end_p95_ms=7.0",
            "jni_end_to_send_median_ms=8.0", "jni_end_to_send_p95_ms=8.0", "stage_samples=1"))
            assertTrue(line, line.contains(field))
        assertEquals(0, meter.snapshot(105.095).stageSamples)
        assertNull(meter.snapshot(105.095).medianJniEndToSendMs)
    }

    @Test fun stageTimesRejectInvalidIntervalsAndRemainAvailableWithoutSensorClock() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        // SENSOR=UNKNOWNでも残りのelapsedRealtime段階は計測できる。
        meter.sent(101.0, null, 100_095_000_000L, 100_080_000_000L, 100_087_000_000L)
        // 逆転/大きな差、および段階欠落は使わない。時計源そのものは数値から判定できない。
        meter.sent(101.0, null, 100_095_000_000L, 100_080_000_000L, 100_079_000_000L)
        meter.sent(101.0, null, 100_095_000_000L, 100_080_000_000L, 100_096_000_000L)
        meter.sent(101.0, null, 100_095_000_000L, 90_080_000_000L, 100_087_000_000L)
        meter.sent(101.0, null, 100_095_000_000L, 0L, 100_087_000_000L)
        meter.sent(101.0, null, 100_095_000_000L)
        val rates = meter.snapshot(102.0)
        assertNull(rates.medianSensorToAnalyzerMs)
        assertNull(rates.medianCaptureToSendMs)
        assertEquals(7.0, rates.medianAnalyzerToJniEndMs!!, 0.0)
        assertEquals(8.0, rates.p95JniEndToSendMs!!, 0.0)
        assertEquals(1, rates.stageSamples)
        meter.reset(103.0)
        assertEquals(0, meter.snapshot(104.0).stageSamples)
    }

    @Test fun stageWindowIsBoundedAndUsesNearestRankP95() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        for (ms in 1..20) meter.sent(101.0, 100_000_000_000L,
            100_000_000_000L + ms * 3_000_000L, 100_000_000_000L + ms * 1_000_000L,
            100_000_000_000L + ms * 2_000_000L)
        val rates = meter.snapshot(102.0)
        assertEquals(10.5, rates.medianSensorToAnalyzerMs!!, 0.0)
        assertEquals(19.0, rates.p95SensorToAnalyzerMs!!, 0.0)
        assertEquals(19.0, rates.p95AnalyzerToJniEndMs!!, 0.0)
        assertEquals(19.0, rates.p95JniEndToSendMs!!, 0.0)
        repeat(1300) { meter.sent(103.0, null, 100_095_000_000L, 100_080_000_000L, 100_087_000_000L) }
        assertEquals(1200, meter.snapshot(104.0).stageSamples)
        assertNull(meter.snapshot(104.0).medianSensorToAnalyzerMs)
    }
    @Test fun rateQueuesAreBoundedWithoutLosingCumulativeDroppedEstimate() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        for (frame in 0L until 1250L) {
            meter.captured(101.0, frame)
            meter.datagramSent(101.0, true)
            meter.datagramSent(101.0, false)
        }
        val rates = meter.snapshot(105.0)
        assertEquals(240.0, rates.captureFps!!, 0.000001)
        assertEquals(240.0, rates.redSendFps, 0.000001)
        assertEquals(240.0, rates.blueSendFps, 0.000001)
        assertEquals(1250L, rates.unanalysedEstimate)
        assertEquals(0.0, meter.snapshot(106.0).blueSendFps, 0.000001)
    }
    @Test fun captureAndColorSendRatesExpireResetAndIgnoreDuplicateCallbacks() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        assertNull(meter.snapshot(101.0).captureFps)
        meter.captured(101.0, 10)
        meter.captured(101.1, 10)
        meter.captured(101.2, 9)
        meter.captured(102.0, 11)
        meter.processed(102.0, 1.0)
        meter.datagramSent(102.0, true)
        meter.datagramSent(102.0, false)
        meter.datagramSent(102.1, true)
        // センサー時計がUNKNOWNでもfpsはOS送信成功を数える。
        meter.sent(102.0, null, 102_000_000_000L)
        val rates = meter.snapshot(104.0)
        assertEquals(0.5, rates.captureFps!!, 0.000001)
        assertEquals(0.5, rates.redSendFps, 0.000001)
        assertEquals(0.25, rates.blueSendFps, 0.000001)
        assertEquals(1L, rates.unanalysedEstimate)
        assertNull(rates.medianCaptureToSendMs)
        assertEquals(0.0, meter.snapshot(107.1).redSendFps, 0.000001)
        assertEquals(0.0, meter.snapshot(107.1).captureFps!!, 0.000001)
        meter.reset(108.0)
        assertNull(meter.snapshot(108.1).captureFps)
        assertEquals(0L, meter.snapshot(108.1).unanalysedEstimate)
    }

    @Test fun droppedFramesExcludeHeldEmptyAndClampCallbackDeliveryRace() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        meter.processed(100.1, 1.0)
        assertEquals(0L, meter.snapshot(101.0).unanalysedEstimate)
        meter.droppedFrame(false)
        assertEquals(0L, meter.snapshot(101.0).udpDroppedFrames)
        meter.droppedFrame(true)
        meter.droppedFrame(true)
        assertEquals(2L, meter.snapshot(110.0).udpDroppedFrames)
        meter.reset(111.0)
        assertEquals(0L, meter.snapshot(112.0).udpDroppedFrames)
    }
    @Test fun diagnosticsDoNotHideCaptureAndDroppedFrames() {
        // 解析fpsだけでは、カメラ低下と解析待ちの破棄を区別できない。
        val text = DeviceHealthText.timing(HealthRates())
        assertTrue(text, text.contains("撮影fps"))
        assertTrue(text, text.contains("未解析推定"))
        assertTrue(text, text.contains("送信破棄"))
    }
    @Test fun thermalStatusesHaveJapaneseLabels() {
        assertEquals(listOf("正常", "やや高い", "やや高い", "高い", "危険", "危険", "危険"),
            (0..6).map { ThermalLevel.fromStatus(it).title })
        assertEquals(ThermalLevel.UNKNOWN, ThermalLevel.fromStatus(-1))
    }

    @Test fun batteryAndTimingHandleUnavailableData() {
        assertEquals("電池 42% / 充電中", DeviceHealthText.battery(42, DeviceHealthText.batteryState(2)))
        assertEquals("電池 100% / 満充電", DeviceHealthText.battery(100, DeviceHealthText.batteryState(5)))
        assertEquals("未充電", DeviceHealthText.batteryState(3))
        assertEquals("未充電", DeviceHealthText.batteryState(4))
        for (value in listOf(null, -1, 101, Int.MIN_VALUE))
            assertEquals("電池 不明 / 状態不明", DeviceHealthText.battery(value, DeviceHealthText.batteryState(1)))
        assertTrue(DeviceHealthText.timing(HealthRates()).contains("未計測"))
        assertTrue(DeviceHealthText.timing(HealthRates(medianJniMs = 12.25)).contains("12.3 ms"))
    }

    @Test fun warningsRespectWarmupRateBoundaryAndHeat() {
        val good = HealthRates(21.0, ready = true)
        assertNull(DeviceHealthText.warning(ThermalLevel.NORMAL, good, 30.0, true))
        val low = HealthRates(20.9, ready = true)
        assertTrue(DeviceHealthText.warning(ThermalLevel.NORMAL, low, 30.0, true)!!.contains("70%未満"))
        assertNull(DeviceHealthText.warning(ThermalLevel.NORMAL, low, 30.0, false))
        assertNull(DeviceHealthText.warning(ThermalLevel.ELEVATED, HealthRates(), 30.0, true))
        for (level in listOf(ThermalLevel.HIGH, ThermalLevel.DANGEROUS))
            assertTrue(DeviceHealthText.warning(level, good, 30.0, false)!!.contains(level.title))
    }

    @Test fun medianWindowExpiresDuringStallAndResets() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        listOf(90.0, 10.0, 30.0, 20.0).forEachIndexed { i, ms -> meter.processed(101.0 + i, ms) }
        assertEquals(1.0, meter.snapshot(104.0).analysisFps, 0.000001)
        assertEquals(25.0, meter.snapshot(104.0).medianJniMs!!, 0.000001)
        assertTrue(meter.snapshot(104.0).ready)
        assertEquals(20.0, meter.snapshot(106.0).medianJniMs!!, 0.000001)
        assertEquals(0.0, meter.snapshot(110.0).analysisFps, 0.000001)
        assertNull(meter.snapshot(110.0).medianJniMs)
        meter.reset(110.0)
        meter.processed(111.0, Double.NaN)
        assertFalse(meter.snapshot(111.0).ready)
        assertNull(meter.snapshot(111.0).medianJniMs)
    }

    @Test fun captureToSendIncludesWorkerWaitAndSocketCompletion() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        // 露光開始100秒、JNI終了100.020秒、送信完了100.055秒。
        meter.processed(100.020, 2.0)
        assertNull(meter.snapshot(100.020).medianCaptureToSendMs)
        meter.sent(100.055, 100_000_000_000L, 100_055_000_000L)
        val rates = meter.snapshot(100.060)
        assertEquals(55.0, rates.medianCaptureToSendMs!!, 0.000001)
        assertEquals(55.0, rates.p95CaptureToSendMs!!, 0.000001)
        assertEquals(1, rates.sentSamples)
        assertEquals(2.0, rates.medianJniMs!!, 0.000001)
        assertTrue(DeviceHealthText.timing(rates).contains("撮影→送信 中央値 55 ms / p95 55 ms（1件）"))
    }

    @Test fun captureClockRejectsUnknownEvenWhenOffsetLooksPlausible() {
        // UNKNOWNで見かけ上50msの差でも、異なる時計のoffsetかもしれない。
        assertNull(CaptureClock.timestamp(100_000_000_000L, false))
        assertNull(CaptureClock.timestamp(0L, true))
        assertNull(CaptureClock.timestamp(-1L, true))
        assertEquals(100_000_000_000L, CaptureClock.timestamp(100_000_000_000L, true))
    }

    @Test fun sendWindowUsesNearestRankP95AndExpiresAndResets() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        for (ms in 1..20) meter.sent(101.0 + ms / 100.0, 100_000_000_000L,
            100_000_000_000L + ms * 1_000_000L)
        val rates = meter.snapshot(102.0)
        assertEquals(10.5, rates.medianCaptureToSendMs!!, 0.000001)
        assertEquals(19.0, rates.p95CaptureToSendMs!!, 0.000001)
        assertEquals(20, rates.sentSamples)
        // 解析だけ続いても、送信していなければ送信時刻を補完しない。
        meter.processed(105.0, 1.0)
        assertEquals(20, meter.snapshot(105.0).sentSamples)
        assertEquals(0, meter.snapshot(106.2).sentSamples)
        assertNull(meter.snapshot(106.2).p95CaptureToSendMs)
        meter.sent(107.0, 106_000_000_000L, 106_020_000_000L)
        meter.reset(108.0)
        assertNull(meter.snapshot(108.1).medianCaptureToSendMs)
        assertTrue(DeviceHealthText.timing(meter.snapshot(108.1)).contains("撮影→送信 未計測"))
    }

    @Test fun captureToSendIgnoresUnavailableAndInvalidClockValues() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        meter.sent(101.0, 100_000_000_000L, 100_060_000_000L)
        meter.sent(101.1, 100_000_000_000L, 100_080_000_000L)
        meter.sent(101.2, 100_000_000_000L, 99_995_000_000L)
        meter.sent(101.3, 100_000_000_000L, 110_000_000_000L)
        meter.sent(101.4, null, 100_090_000_000L)
        meter.sent(101.5, 0L, 90_000_000L)
        assertEquals(70.0, meter.snapshot(102.0).medianCaptureToSendMs!!, 0.000001)
        assertEquals(80.0, meter.snapshot(102.0).p95CaptureToSendMs!!, 0.000001)
        assertEquals(2, meter.snapshot(102.0).sentSamples)
        assertNull(meter.snapshot(107.0).medianCaptureToSendMs)
    }
}
