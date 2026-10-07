package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class EventRecoveryTest {
    @Test fun defaultStartupAndOptIn() {
        assertFalse(SendingResumePolicy().foreground(false))
        assertTrue(SendingResumePolicy().foreground(true))
    }

    @Test fun foregroundAndRelaunchRetainSendingIntent() {
        val policy = SendingResumePolicy()
        policy.start()
        assertTrue(policy.foreground(false))
        val relaunched = SendingResumePolicy(policy.wantsSending)
        assertTrue(relaunched.foreground(false))
        // 権限・カメラ・通信待ちでも送信の意思は残る。
        assertTrue(relaunched.foreground(false))
    }

    @Test fun stopPreventsForegroundRestartEvenWithAutoStart() {
        val policy = SendingResumePolicy()
        assertTrue(policy.foreground(true))
        policy.stop()
        assertFalse(policy.wantsSending)
        assertFalse(policy.foreground(true))
        assertFalse(SendingResumePolicy(policy.wantsSending).foreground(false))
        assertTrue(SendingResumePolicy(policy.wantsSending).foreground(true))
    }

    @Test fun backoffCapsAtThirtySeconds() {
        val retry = RecoveryBackoff()
        var time = 100.0
        for (expected in listOf(1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 30.0, 30.0)) {
            assertEquals(expected, retry.nextDelay(time)!!, 0.0)
            time += expected
        }
    }

    @Test fun backoffExhaustsAfterLongFailureAndCanReset() {
        val retry = RecoveryBackoff()
        var time = 0.0
        while (true) {
            val delay = retry.nextDelay(time) ?: break
            assertTrue(delay > 0 && delay <= 30)
            time += delay
        }
        assertEquals(900.0, time, 0.0)
        retry.reset()
        assertEquals(1.0, retry.nextDelay(901.0)!!, 0.0)
    }

    @Test fun oneFrameDoesNotResetFailureBudget() {
        val retry = RecoveryBackoff(20.0)
        assertEquals(1.0, retry.nextDelay(0.0)!!, 0.0)
        retry.receivedFrame(1.0)
        assertEquals(2.0, retry.nextDelay(2.0)!!, 0.0)
        retry.receivedFrame(3.0)
        assertNull(retry.nextDelay(20.0))
    }

    @Test fun stableFramesResetBackoff() {
        val retry = RecoveryBackoff()
        retry.nextDelay(0.0)
        retry.nextDelay(1.0)
        for (time in 3..13) retry.receivedFrame(time.toDouble())
        assertEquals(0, retry.attempts)
        assertEquals(1.0, retry.nextDelay(14.0)!!, 0.0)
    }
}
