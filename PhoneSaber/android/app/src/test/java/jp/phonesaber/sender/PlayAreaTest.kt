package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class PlayAreaTest {
    // Unity の PhoneSaberDiscoveryResponder.BuildCommand と同じ形式。iPhone の testPCCommandParsing と同じ例。
    @Test fun newCommandsParseLikeIPhone() {
        assertEquals(PcCommand(11, null, false, learnAreaSeconds = 10),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=11 learn-area=10"))
        assertEquals(PcCommand(12, null, false, clearArea = true),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=12 clear-area=1"))
        assertEquals(PcCommand(13, null, false, exposureBias = -1),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=13 exposure-bias=-1"))
        assertEquals(PcCommand(14, null, false, exposureBias = -2),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=14 exposure-bias=-2"))
        assertEquals(PcCommand(15, null, false, exposureBias = 0),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=15 exposure-bias=0"))
        // 知らない項目は無視する。
        assertEquals(PcCommand(16, null, false, learnAreaSeconds = 5),
            PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=16 x=1 learn-area=5"))
    }

    @Test fun newCommandsRejectBadValuesAndMixedActions() {
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 learn-area=10"))              // id なし
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 2 id=1 learn-area=10"))         // 別の版
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 learn-area=0"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 learn-area=61"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 learn-area=x"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 clear-area=0"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-3"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 exposure-bias=1"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1.5"))
        // 1つの指示は1つの操作だけ（新しい操作が他と一緒なら指示ごと捨てる）。
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 learn-area=10 clear-area=1"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1 ignore=red"))
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 clear-ignore=1 learn-area=10"))
        // 同じ項目は後のものが有効。
        assertEquals(-2, PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1 exposure-bias=-2")?.exposureBias)
        assertNull(PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=1 learn-area=10 learn-area=x"))
        // 古い指示の受け付けは変えない。
        assertEquals(PcCommand(6, "blue", true), PcCommandProtocol.parse("PHONESABER_COMMAND 1 id=6 clear-ignore=1 ignore=blue"))
        // 探索応答としては受理しない。
        assertNull(DiscoveryProtocol.parse("PHONESABER_COMMAND 1 id=7 learn-area=10"))
    }

    @Test fun areaSurvivesSaveAndLoad() {
        val area = PlayArea(0.12, 0.05, 0.68, 0.88)
        assertEquals(area, PlayArea.decode(area.encode()))
        assertEquals("x 12〜68% / y 5〜88%", area.summary)
        assertNull(PlayArea.decode(null))
        assertNull(PlayArea.decode(""))
        assertNull(PlayArea.decode("0.1,0.2,0.3"))
        assertNull(PlayArea.decode("0.5,0.2,0.4,0.6")) // min > max
        assertNull(PlayArea.decode("x,0.2,0.4,0.6"))
        assertArrayEquals(doubleArrayOf(0.12, 0.05, 0.68, 0.88), area.packed(), 0.0)
    }

    @Test fun exposureBiasMapsEvToCameraXIndex() {
        // 1/6 EV 刻み・±12（多くの端末）: −1 EV = −6、−2 EV = −12。
        assertEquals(-6, ExposureBias.index(-1, 1, 6, -12, 12))
        assertEquals(-12, ExposureBias.index(-2, 1, 6, -12, 12))
        assertEquals(0, ExposureBias.index(0, 1, 6, -12, 12))
        // 1/3 EV 刻み・±6、1/2 EV 刻み・範囲の端で切る。
        assertEquals(-3, ExposureBias.index(-1, 1, 3, -6, 6))
        assertEquals(-3, ExposureBias.index(-2, 1, 2, -3, 3))
        // 非対応（範囲 [0,0]・刻み 0）。
        assertNull(ExposureBias.index(-1, 1, 6, 0, 0))
        assertNull(ExposureBias.index(-1, 0, 1, -12, 12))
        assertTrue(ExposureBias.valid(0) && ExposureBias.valid(-1) && ExposureBias.valid(-2))
        assertFalse(ExposureBias.valid(-3) || ExposureBias.valid(1))
        assertEquals("0（OFF）", ExposureBias.label(0))
        assertEquals("−2 EV", ExposureBias.label(-2))
    }
}
