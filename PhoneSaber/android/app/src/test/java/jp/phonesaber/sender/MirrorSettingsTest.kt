package jp.phonesaber.sender

import org.junit.Assert.assertEquals
import org.junit.Test

class MirrorSettingsTest {
    @Test fun missingPreferencesDefaultToNoMirrors() {
        val stored = emptyMap<String, Boolean>()
        assertEquals(MirrorSettings(false, false), MirrorSettings.load { key, default ->
            stored[key] ?: default
        })
    }

    @Test fun eachAxisRestoresIndependently() {
        assertEquals(MirrorSettings(true, false), MirrorSettings.load { key, default ->
            mapOf("mirrorX" to true)[key] ?: default
        })
        assertEquals(MirrorSettings(false, true), MirrorSettings.load { key, default ->
            mapOf("mirrorY" to true)[key] ?: default
        })
    }

    @Test fun allCombinationsPersistAcrossReloadIncludingTurningMirrorsOff() {
        val stored = mutableMapOf<String, Boolean>()
        for (x in listOf(true, false)) for (y in listOf(true, false)) {
            val settings = MirrorSettings(x, y)
            settings.save { key, value -> stored[key] = value }
            assertEquals(mapOf("mirrorX" to x, "mirrorY" to y), stored)
            assertEquals(settings, MirrorSettings.load { key, default -> stored[key] ?: default })
        }
    }
}
