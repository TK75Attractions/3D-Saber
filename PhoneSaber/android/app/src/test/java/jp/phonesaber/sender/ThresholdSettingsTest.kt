package jp.phonesaber.sender

import org.junit.Assert.assertEquals
import org.junit.Test

class ThresholdSettingsTest {
    @Test fun defaultsMatchTheProductionThresholds() {
        assertEquals(ThresholdSettings(145, 25), ThresholdSettings())
        assertEquals(ThresholdSettings(), ThresholdSettings.restore { _, default -> default })
    }

    @Test fun recreationKeepsTheOperatorThresholds() {
        val bundle = mutableMapOf<String, Int>()
        ThresholdSettings(160, 30).save { key, value -> bundle[key] = value }
        assertEquals(ThresholdSettings(160, 30), ThresholdSettings.restore { key, default -> bundle[key] ?: default })
    }

    @Test fun restoredValuesStayInTheJniRange() {
        assertEquals(ThresholdSettings(255, 0), ThresholdSettings.restore { key, _ ->
            if (key.contains("Brightness")) 999 else -4 })
    }
}
