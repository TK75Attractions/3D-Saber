package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test
import java.io.File

class PayloadRoutingTest {
    @Test fun sharedCoordinateContractVectorsKeepTheirBytesAndColorPorts() {
        val root = generateSequence(File(checkNotNull(System.getProperty("user.dir")))) { it.parentFile }
            .first { File(it, "PhoneSaber/tools/fixtures/protocol_coordinates.tsv").isFile }
        File(root, "PhoneSaber/tools/fixtures/protocol_coordinates.tsv").readLines().drop(1).forEach { line ->
            val fields = line.split('\t')
            if (fields[2] != "1" || fields[3] != "1") return@forEach
            // JNI の本文を再整形せず、通常・計測とも両色の正しい port に送る。
            val text = fields[1]
            val packets = PayloadRouting.datagrams(arrayOf(
                NativeResult(0, true, false, Ports.RED, text),
                NativeResult(1, true, false, Ports.BLUE, text)))
            assertEquals(fields[0], listOf(CoordinateDatagram(Ports.RED, text),
                CoordinateDatagram(Ports.BLUE, text)), packets)
            packets.forEach { assertArrayEquals(text.toByteArray(Charsets.US_ASCII),
                it.text.toByteArray(Charsets.US_ASCII)) }
        }
    }

    @Test fun forwardsCoreTextExactlyToFixedColorPorts() {
        val packets = PayloadRouting.datagrams(arrayOf(
            NativeResult(0, true, false, 5005, "0,1079,1919,0"),
            NativeResult(1, true, false, 5006, "12,34,56,78")))
        assertEquals(listOf(CoordinateDatagram(5005, "0,1079,1919,0"),
            CoordinateDatagram(5006, "12,34,56,78")), packets)
        assertArrayEquals("0,1079,1919,0".toByteArray(Charsets.US_ASCII), packets[0].text.toByteArray(Charsets.US_ASCII))
    }

    @Test fun predictionsSendAndHeldAbsentExpiredNeverSend() {
        assertEquals(listOf(CoordinateDatagram(5006, "1,2,3,4")), PayloadRouting.datagrams(arrayOf(
            NativeResult(0, false, false, 5005, null),
            NativeResult(1, true, true, 5006, "1,2,3,4"))))
        assertTrue(PayloadRouting.datagrams(emptyArray()).isEmpty())
        assertTrue(PayloadRouting.datagrams(arrayOf(
            NativeResult(0, false, false, 5005, "1,2,3,4"),
            NativeResult(1, true, false, 5006, null))).isEmpty())
    }

    @Test fun rejectsUnknownColorsAndCrossedPorts() {
        assertTrue(PayloadRouting.datagrams(arrayOf(
            NativeResult(0, true, false, 5006, "1,2,3,4"),
            NativeResult(1, true, false, 5005, "1,2,3,4"),
            NativeResult(2, true, false, 5005, "1,2,3,4"))).isEmpty())
    }

    @Test fun doesNotReformatCoreMeasurementText() {
        // 計測モードもコアの文字列をそのまま送る。保持中は ts があっても送らない。
        val text = "ts=1791234567.123456;1,2,3,4"
        val packets = PayloadRouting.datagrams(arrayOf(
            NativeResult(0, true, false, 5005, text),
            NativeResult(1, true, true, 5006, text),
            NativeResult(1, false, false, 5006, text)))
        assertEquals(listOf(CoordinateDatagram(5005, text), CoordinateDatagram(5006, text)), packets)
        for (packet in packets) assertArrayEquals(text.toByteArray(Charsets.US_ASCII),
            packet.text.toByteArray(Charsets.US_ASCII))
    }
}
