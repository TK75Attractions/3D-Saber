package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class PcReplyWatchTest {
    @Test fun replyAgeNeverSubstitutesWaitingTimeForARealReplyAndResetsWithTarget() {
        val watch = PcReplyWatch()
        assertNull(watch.ageSeconds(100.0))
        watch.update(true, "192.168.1.10", false, 100.0)
        assertNull(watch.ageSeconds(110.0))
        watch.received("192.168.1.11", 110.0)
        assertNull(watch.ageSeconds(111.0))
        watch.received("192.168.1.10", 110.0)
        assertEquals(2.0, watch.ageSeconds(112.0)!!, 0.000001)
        assertEquals(0.0, watch.ageSeconds(109.0)!!, 0.000001)
        watch.update(true, "192.168.1.11", false, 112.0)
        assertNull(watch.ageSeconds(113.0))
        watch.reset()
        assertNull(watch.ageSeconds(114.0))
    }
    private val pc = "192.168.137.1"
    private val other = "192.168.137.5"

    @Test fun noReplyWarnsAtThreeSeconds() {
        val watch = PcReplyWatch()
        assertNull(watch.update(true, pc, false, 100.0))
        assertNull(watch.update(true, pc, false, 102.999))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 103.0))
    }

    @Test fun repliesRefreshDeadlineAndRecoveryClearsWarning() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 0.0)
        watch.received(pc, 2.0)
        assertNull(watch.update(true, pc, false, 4.999))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 5.0))
        watch.received(pc, 5.1)
        assertNull(watch.update(true, pc, false, 5.1))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 8.1))
    }

    @Test fun anotherPcCannotHideMissingReply() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 10.0)
        watch.received(other, 12.9)
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 13.0))
    }

    @Test fun manualIpUsesItsOwnReplyAndMessage() {
        val watch = PcReplyWatch()
        watch.update(true, pc, true, 0.0)
        watch.received(other, 2.0)
        assertEquals(PcReplyWatch.MANUAL_MESSAGE, watch.update(true, pc, true, 3.0))
        watch.received(pc, 3.1)
        assertNull(watch.update(true, pc, true, 3.1))
    }

    @Test fun stopAndRestartDiscardOldEvidence() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 0.0)
        watch.received(pc, 2.0)
        assertNull(watch.update(false, pc, false, 4.0))
        watch.received(pc, 5.0)
        assertNull(watch.update(true, pc, false, 6.0))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 9.0))
    }

    @Test fun targetChangeDoesNotReusePreviousPcReply() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 0.0)
        watch.received(pc, 2.0)
        assertNull(watch.update(true, other, false, 3.0))
        watch.received(pc, 5.9)
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, other, false, 6.0))
    }

    @Test fun networkChangeAndDelayedRepliesDiscardOldEvidence() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 0.0)
        watch.received(pc, 2.0)
        watch.reset()
        assertNull(watch.update(true, pc, false, 3.0))
        watch.received(pc, 2.9) // main thread へ遅れて届いた旧ネットワークの応答。
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 6.0))
    }

    @Test fun missingDestinationStillWarnsAndExpiryDoesNotClearWarning() {
        val watch = PcReplyWatch()
        watch.update(true, null, false, 0.0)
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, null, false, 3.0))
        assertNull(watch.update(true, pc, false, 4.0))
        watch.received(pc, 5.0)
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 8.0))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, null, false, 13.0))
    }

    @Test fun outOfOrderRepliesDoNotMoveDeadlineBackwards() {
        val watch = PcReplyWatch()
        watch.update(true, pc, false, 0.0)
        watch.received(pc, 2.0)
        watch.received(pc, 1.0)
        assertNull(watch.update(true, pc, false, 4.9))
        assertEquals(PcReplyWatch.AUTOMATIC_MESSAGE, watch.update(true, pc, false, 5.0))
    }
}
