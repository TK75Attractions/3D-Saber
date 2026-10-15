package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class LaunchSettingsTest {
    private fun read(vararg extras: Pair<String, Any>) = LaunchSettings.read { extras.toMap()[it] }

    @Test fun typedExtrasOverrideSavedSettingsOnlyForThisLaunch() {
        val launch = read("autoStartSending" to true, "cameraFPS" to 60)
        assertTrue(launch.autoStart(false))
        assertEquals(60, launch.cameraFps(30))
        // 起動引数で開始した送信意思も、次の起動へ保存しない。
        assertFalse(launch.persistAutomaticResume)
        val nextLaunch = read()
        assertFalse(nextLaunch.autoStart(false))
        assertEquals(30, nextLaunch.cameraFps(30))
        assertTrue(nextLaunch.persistAutomaticResume)
    }

    @Test fun explicitFalseAndThirtyAreNotTreatedAsMissing() {
        val launch = read("autoStartSending" to false, "cameraFPS" to 30)
        assertFalse(launch.autoStart(true))
        assertEquals(30, launch.cameraFps(60))
    }

    @Test fun lowLatencyComparisonIsOptInAndDoesNotSurviveRelaunch() {
        assertTrue(read("lowLatencyCamera" to true).lowLatencyCamera)
        assertFalse(read("lowLatencyCamera" to "true").lowLatencyCamera)
        assertFalse(read().lowLatencyCamera)
    }

    @Test fun unsupportedFpsAndWrongExtraTypesFallBackToPreferences() {
        for (fps in listOf(0, 120, -1, "60", true)) {
            val launch = read("autoStartSending" to "true", "cameraFPS" to fps)
            assertTrue(launch.autoStart(true))
            assertEquals(30, launch.cameraFps(30))
            assertTrue(launch.persistAutomaticResume)
        }
    }
}
