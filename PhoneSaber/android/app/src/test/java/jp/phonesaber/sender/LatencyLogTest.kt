package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class LatencyLogTest {
    @Test fun emitsOnceEveryTenSecondsOnlyWhileSendingWithoutCatchupBurst() {
        val log = LatencyLog()
        log.start(100.0)
        assertFalse(log.due(109.999, true))
        assertFalse(log.due(110.0, false))
        assertTrue(log.due(110.0, true))
        assertFalse(log.due(110.0, true))
        assertFalse(log.due(119.999, true))
        assertTrue(log.due(150.0, true))
        assertFalse(log.due(150.1, true))
        log.start(200.0)
        assertFalse(log.due(209.0, true))
        assertTrue(log.due(210.0, true))
    }

    @Test fun copyKeepsFiveMinutesAcrossRestartAndExpiresEvenAfterStopping() {
        val log = LatencyLog()
        assertEquals("", log.copy(100.0))
        log.append(100.0, "old")
        log.append(110.0, "new")
        log.start(200.0)
        assertEquals("old\nnew", log.copy(399.999))
        assertEquals("new", log.copy(400.0))
        assertEquals("", log.copy(410.0))
    }

    @Test fun boundsTheNumberOfRetainedLines() {
        val log = LatencyLog()
        for (i in 0..99) log.append(100.0, "line$i")
        val copied = log.copy(101.0).lines()
        assertEquals(60, copied.size)
        assertEquals("line40", copied.first())
        assertEquals("line99", copied.last())
    }

    @Test fun statusIncludesAllFieldsWithoutInventingUnavailableMeasurements() {
        val unknown = LatencyStatusText.line(HealthRates(), -1, null)
        for (field in listOf("capture_fps=UNKNOWN", "analysis_fps=0.0", "send_red_fps=0.0",
            "send_blue_fps=0.0", "capture_to_send_median_ms=UNKNOWN", "capture_to_send_p95_ms=UNKNOWN",
            "samples=0", "dropped_camera_frames_est=0", "dropped_udp_frames=0",
            "thermal=-1(不明)", "pc_reply_age_s=UNKNOWN")) assertTrue(unknown, unknown.contains(field))
        val measured = LatencyStatusText.line(HealthRates(60.0, medianCaptureToSendMs = 37.0,
            p95CaptureToSendMs = 50.0, sentSamples = 300, captureFps = 60.0,
            redSendFps = 60.0, blueSendFps = 30.0, unanalysedEstimate = 2, udpDroppedFrames = 3), 3, 2.5)
        for (field in listOf("capture_fps=60.0", "send_blue_fps=30.0", "capture_to_send_median_ms=37.0",
            "capture_to_send_p95_ms=50.0", "samples=300", "dropped_camera_frames_est=2",
            "dropped_udp_frames=3", "thermal=3(高い)", "pc_reply_age_s=2.5"))
            assertTrue(measured, measured.contains(field))
    }
}
