package jp.phonesaber.sender

import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.view.Window
import android.widget.Button
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView
import org.junit.Assert.*
import org.junit.Test
import org.mockito.Mockito.*

class SendingWifiLockTest {
    // Activityの構築・画面表示はせず、本番の開始/停止処理とlockの所有状態を検証する。
    private fun withActivity(failAcquire: Boolean, failedIndex: Int, restartBeforeRetry: Boolean = true) {
        val activity = mock(MainActivity::class.java)
        fun field(name: String, value: Any) {
            MainActivity::class.java.getDeclaredField(name).apply { isAccessible = true }.set(activity, value)
        }
        val camera = mock(CameraSession::class.java)
        val sender = mock(LatestUdpSender::class.java)
        val window = mock(Window::class.java)
        `when`(activity.window).thenReturn(window)
        `when`(activity.getSystemService(ConnectivityManager::class.java))
            .thenReturn(mock(ConnectivityManager::class.java))
        field("camera", camera)
        field("sender", sender)
        field("foreground", true)
        field("resumePolicy", SendingResumePolicy(wasSending = true))
        field("mirrors", MirrorSettings())
        field("brightness", 145)
        field("dominance", 25)
        field("latencyLog", LatencyLog())
        field("cameraBackoff", RecoveryBackoff())
        field("battery", mock(BatteryManager::class.java))
        field("startStop", mock(Button::class.java))
        for (name in listOf("brightnessSlider", "dominanceSlider")) field(name, mock(SeekBar::class.java))
        for (name in listOf("mirrorXSwitch", "mirrorYSwitch", "measurementSwitch", "fps60Switch",
            "lowLatencyCameraSwitch")) field(name, mock(Switch::class.java))
        for (name in listOf("detection", "health")) field(name, mock(TextView::class.java))
        val locks = List(2) { mock(WifiManager.WifiLock::class.java) }
        val held = BooleanArray(2)
        var failed = false
        locks.forEachIndexed { index, lock ->
            `when`(lock.isHeld).thenAnswer { held[index] }
            doAnswer {
                if (failAcquire && index == failedIndex && !failed) {
                    failed = true
                    throw RuntimeException("acquireWifiLock failed")
                }
                held[index] = true; null
            }.`when`(lock).acquire()
            doAnswer {
                if (!failAcquire && index == failedIndex && !failed) {
                    failed = true
                    throw RuntimeException("releaseWifiLock failed")
                }
                held[index] = false; null
            }.`when`(lock).release()
        }
        field("lowLatencyWifi\$delegate", lazy { locks[0] })
        field("highPerformanceWifi\$delegate", lazy { locks[1] })
        val start = MainActivity::class.java.getDeclaredMethod("startSending").apply { isAccessible = true }
        val stop = MainActivity::class.java.getDeclaredMethod("stopSending", Boolean::class.javaPrimitiveType)
            .apply { isAccessible = true }
        // IntentFilterだけをmockし、Android runtimeなしで停止後の状態表示まで実行する。
        mockConstruction(IntentFilter::class.java).use {
            start.invoke(activity)
            verify(camera).start(145, 25, MirrorSettings(), false, 30, false)
            assertTrue(held[1 - failedIndex])
            stop.invoke(activity, false)
            assertFalse("片方のRPC失敗でも、もう片方のlockを解放する", held[1 - failedIndex])
            verify(camera).stop()
            verify(window).clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            // 解放失敗時の保持状態は残る。次の停止では同じlockの解放を再試行する。
            assertEquals(!failAcquire, held[failedIndex])
            if (!restartBeforeRetry) {
                stop.invoke(activity, false)
                assertTrue("再開しなくても次の停止で解放を再試行する", held.none { it })
                verify(camera, times(2)).stop()
                for (lock in locks) verify(lock).acquire()
                verify(locks[failedIndex], times(2)).release()
                verify(locks[1 - failedIndex]).release()
                return
            }
            start.invoke(activity)
            assertTrue(held.all { it })
            stop.invoke(activity, false)
            assertTrue("再開後の停止では両方のlockを解放する", held.none { it })
            verify(camera, times(2)).start(145, 25, MirrorSettings(), false, 30, false)
            verify(camera, times(2)).stop()
            locks.forEachIndexed { index, lock ->
                verify(lock, times(if (!failAcquire && index == failedIndex) 1 else 2)).acquire()
                verify(lock, times(if (failAcquire && index == failedIndex) 1 else 2)).release()
            }
        }
    }

    @Test fun acquireFailureStillStartsAndAllowsStopAndRestart() {
        for (index in 0..1) withActivity(failAcquire = true, failedIndex = index)
    }

    @Test fun releaseFailureStillReleasesTheOtherLockAndAllowsRestart() {
        for (index in 0..1) withActivity(failAcquire = false, failedIndex = index)
    }

    @Test fun releaseFailureRetriesOnRepeatedStop() {
        for (index in 0..1) withActivity(failAcquire = false, failedIndex = index, restartBeforeRetry = false)
    }
}
