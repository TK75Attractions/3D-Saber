package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class IgnoreZonesTest {
    // iOS の MetricsDatagramTests と同じ例。
    @Test fun ackFormatMatchesIPhone() {
        assertEquals("PHONESABER_ACK 1 id=7 station=A platform=ios result=ignored detail=red1-blue1",
            PcAckProtocol.format(7, "A", "ios", "ignored", "red1-blue1"))
        assertEquals("PHONESABER_ACK 1 id=8 station=none platform=android result=nothing detail=red0-blue0",
            PcAckProtocol.format(8, "", "android", "nothing", "red0-blue0"))
        assertEquals("PHONESABER_ACK 1 id=9 station=B platform=ios result=cleared detail=ignore",
            PcAckProtocol.format(9, "B", "ios", "cleared", "ignore"))
        assertEquals("PHONESABER_ACK 1 id=10 station=A platform=android result=learning detail=10s",
            PcAckProtocol.format(10, "A", "android", "learning", "10s"))
        assertEquals("PHONESABER_ACK 1 id=10 station=A platform=android result=learned detail=samples42",
            PcAckProtocol.format(10, "A", "android", "learned", "samples42"))
        assertEquals("PHONESABER_ACK 1 id=11 station=A platform=ios result=failed detail=samples2",
            PcAckProtocol.format(11, "A", "ios", "failed", "samples2"))
        assertEquals("PHONESABER_ACK 1 id=12 station=B platform=android result=cleared detail=area",
            PcAckProtocol.format(12, "B", "android", "cleared", "area"))
        assertEquals("PHONESABER_ACK 1 id=13 station=A platform=ios result=exposure detail=ev-2",
            PcAckProtocol.format(13, "A", "ios", "exposure", "ev-2"))
        assertEquals("PHONESABER_ACK 1 id=14 station=B platform=android result=tracklock detail=off",
            PcAckProtocol.format(14, "B", "android", "tracklock", "off"))
        assertEquals("PHONESABER_ACK 1 id=15 station=A platform=ios result=clip-saved detail=saved",
            PcAckProtocol.format(15, "A", "ios", "clip-saved", "saved"))
        assertEquals("PHONESABER_ACK 1 id=16 station=A platform=android result=clip-off detail=off",
            PcAckProtocol.format(16, "A", "android", "clip-off", "off"))
        assertEquals("PHONESABER_ACK 1 id=17 station=B platform=ios result=autoclip detail=on",
            PcAckProtocol.format(17, "B", "ios", "autoclip", "on"))
        for (atom in listOf("", "red blue", "赤", "x\n", "x=y", "x".repeat(200)))
            assertNull(PcAckProtocol.format(1, "A", "android", "ignored", atom))
        assertNull(PcAckProtocol.format(-1, "A", "android", "cleared", "ignore"))
        assertNull(PcAckProtocol.format(1, "A", "other", "cleared", "ignore"))
        assertTrue(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=16 clip=1")!!.clip)
        assertEquals(true, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=17 auto-clip=1")?.autoClip)
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=17 auto-clip=1 ignore=red"))
    }

    // Unity の PhoneSaberDiscoveryResponder.BuildCommand と同じ形式。iPhone の testPCCommandParsing と同じ例。
    @Test fun defaultsCommands() {
        assertEquals(PcCommand(81, null, false, defaults = true),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=81 defaults=1 x=ignored"))
        assertEquals(true, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=81 defaults=0 defaults=1")?.defaults)
        for (text in listOf("PHONESABER_COMMAND 2 id=1 defaults=1", "PHONESABER_COMMAND 1 defaults=1",
            "PHONESABER_COMMAND 1 id=x defaults=1", "PHONESABER_COMMAND 1 id=1 defaults=0",
            "PHONESABER_COMMAND 1 id=1 defaults=2", "PHONESABER_COMMAND 1 id=1 defaults=true",
            "PHONESABER_COMMAND 1 id=1 defaults=01", "PHONESABER_COMMAND 1 id=1 defaults=1 defaults=bad"))
            assertNull(text, PcCommandProtocol.parse(text))
        for (other in listOf("ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1", "exposure-bias=-1",
            "track-lock=0", "clip=1", "auto-clip=0"))
            assertNull(other, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 defaults=1 $other"))
        assertEquals("PHONESABER_ACK 1 id=81 station=none platform=android result=defaults detail=tracklock-on-ev-2",
            PcAckProtocol.format(81, "", "android", "defaults", "tracklock-on-ev-2"))
    }

    @Test fun trackLockCommands() {
        assertEquals(PcCommand(71, null, false, trackLock = true),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=71 track-lock=1 x=ignored"))
        assertEquals(false, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=72 track-lock=0")?.trackLock)
        assertEquals(false, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=73 track-lock=1 track-lock=0")?.trackLock)
        for (text in listOf("PHONESABER_COMMAND 2 id=1 track-lock=1", "PHONESABER_COMMAND 1 track-lock=1",
            "PHONESABER_COMMAND 1 id=x track-lock=1", "PHONESABER_COMMAND 1 id=1 track-lock=2",
            "PHONESABER_COMMAND 1 id=1 track-lock=true", "PHONESABER_COMMAND 1 id=1 track-lock=01",
            "PHONESABER_COMMAND 1 id=1 track-lock=1 track-lock=bad")) assertNull(PcCommandProtocol.parse(text))
        for (other in listOf("ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1", "exposure-bias=-1"))
            assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 track-lock=0 $other"))
    }

    @Test fun pcCommandParsingMatchesIPhone() {
        assertEquals(PcCommand(7, "red", false), PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=7 ignore=red"))
        assertEquals(PcCommand(8, "blue", false), PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=8 ignore=blue"))
        assertEquals(PcCommand(9, null, true), PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=9 clear-ignore=1"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 ignore=red"))        // id なし
        assertEquals("both", PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=11 ignore=both")?.ignore) // 赤と青をまとめて
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=3 ignore=green")) // 知らない色
        assertNull(PcCommandProtocol.parse("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 2 id=1 ignore=red"))   // 別の版
    }

    @Test fun pcCommandParsingEdgeCases() {
        // 空の項目・値の無い項目・知らない項目は飛ばす。
        assertEquals(PcCommand(5, "red", false), PcCommandProtocol.parse("PHONESABER_COMMAND 1  id=5  x=1 ignore=red"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id= ignore=red"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=x ignore=red"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=4 clear-ignore=0"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 10 id=4 ignore=red"))
        // 後の値が有効。不正な色で上書きされたら ignore は無い。
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=4 ignore=red ignore=green"))
        assertEquals(PcCommand(6, "blue", true), PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=6 clear-ignore=1 ignore=blue"))
        // Unity の id は大きい（Ticks 由来 ×1000）。Int に収まらなければ捨てる。
        assertEquals(99_999_001, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=99999001 ignore=red")?.id)
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=9999999999 ignore=red"))
        // 探索応答としては従来どおり受理しない（古い版の動作と同じ）。
        assertNull(DiscoveryProtocol.parse("PHONESABER_COMMAND 1 id=7 ignore=red"))
    }

    @Test fun zonesKeepTheNewestFourPerColor() {
        var zones = IgnoreZones.NONE
        for (i in 0 until 6) zones = zones.adding(0, IgnoreZone(i / 10.0, 0.0, i / 10.0 + 0.1, 0.2))
        zones = zones.adding(1, IgnoreZone(0.5, 0.5, 0.6, 0.6))
        assertEquals(4, zones.red.size)
        assertEquals(0.2, zones.red.first().minX, 0.0)
        assertEquals(0.5, zones.red.last().minX, 0.0)
        assertEquals(1, zones.blue.size)
        assertEquals("赤 4・青 1", zones.summary)
        assertArrayEquals(doubleArrayOf(0.5, 0.5, 0.6, 0.6), zones.packedBlue, 0.0)
        assertEquals(16, zones.packedRed.size)
        assertTrue(IgnoreZones.NONE.isEmpty && IgnoreZones.NONE.packedRed.isEmpty())
    }

    @Test fun zonesSurviveSaveAndLoad() {
        val zones = IgnoreZones.NONE.adding(0, IgnoreZone(0.35, 0.1, 0.84, 0.66))
            .adding(0, IgnoreZone(-0.04, 0.0, 0.06, 1.04)).adding(1, IgnoreZone(0.1, 0.2, 0.3, 0.4))
        assertEquals(zones, IgnoreZones.decode(zones.encode()))
        assertEquals(IgnoreZones.NONE, IgnoreZones.decode(IgnoreZones.NONE.encode()))
        assertEquals(IgnoreZones.NONE, IgnoreZones.decode(null))
        // 壊れた値は捨て、読める範囲だけ残す。
        val damaged = IgnoreZones.decode("red:0.1,0.1,0.2,0.2;x,1,2,3;0.5,0.5,0.4,0.6|blue:1,2|green:0,0,1,1")
        assertEquals(listOf(IgnoreZone(0.1, 0.1, 0.2, 0.2)), damaged.red)
        assertTrue(damaged.blue.isEmpty())
    }

    @Test fun overlayFitsTheFrameLikeFitCenter() {
        val fit = IgnoreZoneOverlay.Fit.of(1000.0, 500.0, 480.0, 640.0)
        assertEquals(375.0, fit.width, 1e-9)
        assertEquals(500.0, fit.height, 1e-9)
        assertEquals(312.5, fit.x(0.0), 1e-9)
        assertEquals(687.5, fit.x(1.0), 1e-9)
        assertEquals(250.0, fit.y(0.5), 1e-9)
    }
}
