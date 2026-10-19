package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class IgnoreZonesTest {
    // Unity の PhoneSaberDiscoveryResponder.BuildCommand と同じ形式。iPhone の testPCCommandParsing と同じ例。
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
