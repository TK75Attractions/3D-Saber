package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class PayloadRoutingTest {
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
        // Core supports this contract; Android production never enables measurement_mode.
        val text = "ts=1791234567.123456;1,2,3,4"
        assertEquals(text, PayloadRouting.datagrams(arrayOf(NativeResult(0, true, false, 5005, text))).single().text)
    }
}
