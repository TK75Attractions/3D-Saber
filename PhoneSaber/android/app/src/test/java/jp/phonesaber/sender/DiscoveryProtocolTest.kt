package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class DiscoveryProtocolTest {
    @Test fun stationMatcherRequiresExactMetadataOnlyWhenConfigured() {
        val legacy = DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=PC")!!
        val a = DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=PC station=A")!!
        val b = DiscoveryProtocol.parse("PHONESABER_UNITY 1 station=B name=PC blue=5006 red=5005")!!
        listOf(legacy, a, b).forEach { assertTrue(StationMatcher.reply("", it)) }
        assertTrue(StationMatcher.reply("A", a))
        assertFalse(StationMatcher.reply("A", b))
        assertFalse(StationMatcher.reply("A", legacy))
        assertTrue(StationMatcher.service("A", "Phone Saber Unity A"))
        assertTrue(StationMatcher.service("B", "Phone Saber Unity B"))
        listOf("Phone Saber Unity", "Phone Saber Unity B", "Phone Saber Unity AA", "Phone Saber UnityA")
            .forEach { assertFalse(StationMatcher.service("A", it)) }
        assertTrue(StationMatcher.service("", "Phone Saber Unity B"))
        assertNull(DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=PC station=A station=B"))
        assertNull(DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=PC station="))
    }

    @Test fun responderReplyAndFieldOrder() {
        val reply = DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=My_PC")!!
        assertEquals("My_PC", reply.name)
        assertEquals(5005, reply.redPort)
        assertEquals(5006, reply.bluePort)
        assertTrue(reply.compatible)
        assertEquals(reply, DiscoveryProtocol.parse("  PHONESABER_UNITY 1 name=My_PC blue=5006 red=5005  "))
    }

    @Test fun rejectsMalformedUnversionedAndAmbiguousReplies() {
        val replies = listOf("", "PHONESABER_DISCOVER 1", "PHONESABER_UNITY 2 red=5005 blue=5006 name=PC",
            "PHONESABER_UNITY 1 red=5005 blue=5006", "PHONESABER_UNITY 1 red=5005 red=5006 name=PC",
            "PHONESABER_UNITY 1 red=5005 blue=5006 unknown=PC", "PHONESABER_UNITY 1 red=0 blue=5006 name=PC",
            "PHONESABER_UNITY 1 red=65536 blue=5006 name=PC", "PHONESABER_UNITY 1 red=-1 blue=5006 name=PC",
            "PHONESABER_UNITY 1 red=+5005 blue=5006 name=PC", "PHONESABER_UNITY 1 red=x blue=5006 name=PC",
            "PHONESABER_UNITY 1 red=999999999999999 blue=5006 name=PC",
            "PHONESABER_UNITY 1 red=5005 blue=5006 name=", "PHONESABER_UNITY 1 red=5005 blue=5006 name=PC extra=1",
            "PHONESABER_UNITY 1 red=5005 blue=5006 name=PC\n", "PHONESABER_UNITY 1 red=5005 blue=5006 name=PC\u0000",
            "PHONESABER_UNITY 1 red=5005 blue=5006 name=" + "a".repeat(1024))
        replies.forEach { assertNull(it, DiscoveryProtocol.parse(it)) }
    }

    @Test fun validAdvertisedPortsDoNotChangeProductionContract() {
        val reply = DiscoveryProtocol.parse("PHONESABER_UNITY 1 red=6000 blue=6001 name=PC")!!
        assertFalse(reply.compatible)
    }

    @Test fun manualIpIsNumericAndUnicast() {
        assertEquals("192.168.1.10", ManualAddress.parse(" 192.168.1.10 ")!!.hostAddress)
        listOf("PC.local", "1.2.3", "1.2.3.256", "1.2.3.-1", "0.0.0.0", "127.0.0.1", "224.0.0.1",
            "255.255.255.255", "1..3.4").forEach { assertNull(it, ManualAddress.parse(it)) }
    }
}
