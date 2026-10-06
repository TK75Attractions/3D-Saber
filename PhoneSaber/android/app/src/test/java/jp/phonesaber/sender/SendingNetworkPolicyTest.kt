package jp.phonesaber.sender

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SendingNetworkPolicyTest {
    @Test fun releaseAlwaysRejectsNonWifiEvenWithDeveloperOverrideAndManualIp() {
        assertFalse(SendingNetworkPolicy.canSend(false, true, true, false, true))
    }

    @Test fun debugNonWifiRequiresOptInManualIpAndAnActiveNetwork() {
        assertTrue(SendingNetworkPolicy.canSend(true, true, true, false, true))
        assertFalse(SendingNetworkPolicy.canSend(true, false, true, false, true))
        assertFalse(SendingNetworkPolicy.canSend(true, true, true, false, false))
        assertFalse(SendingNetworkPolicy.canSend(true, true, false, false, true))
    }

    @Test fun wifiRemainsAllowedInEveryBuildWithoutDeveloperSettings() {
        for (debug in listOf(false, true))
            for (override in listOf(false, true))
                for (manual in listOf(false, true)) {
                    assertTrue(SendingNetworkPolicy.canSend(debug, override, true, true, manual))
                    assertFalse(SendingNetworkPolicy.canSend(debug, override, false, true, manual))
                }
    }
}
