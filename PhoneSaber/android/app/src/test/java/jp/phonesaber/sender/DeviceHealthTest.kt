package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test

class DeviceHealthTest {
    @Test fun thermalStatusesHaveJapaneseLabels() {
        assertEquals(listOf("正常", "やや高い", "やや高い", "高い", "危険", "危険", "危険"),
            (0..6).map { ThermalLevel.fromStatus(it).title })
        assertEquals(ThermalLevel.UNKNOWN, ThermalLevel.fromStatus(-1))
    }

    @Test fun batteryAndTimingHandleUnavailableData() {
        assertEquals("電池 42% / 充電中", DeviceHealthText.battery(42, DeviceHealthText.batteryState(2)))
        assertEquals("電池 100% / 満充電", DeviceHealthText.battery(100, DeviceHealthText.batteryState(5)))
        assertEquals("未充電", DeviceHealthText.batteryState(3))
        assertEquals("未充電", DeviceHealthText.batteryState(4))
        for (value in listOf(null, -1, 101, Int.MIN_VALUE))
            assertEquals("電池 不明 / 状態不明", DeviceHealthText.battery(value, DeviceHealthText.batteryState(1)))
        assertTrue(DeviceHealthText.timing(HealthRates()).contains("未計測"))
        assertTrue(DeviceHealthText.timing(HealthRates(medianJniMs = 12.25)).contains("12.3 ms"))
    }

    @Test fun warningsRespectWarmupRateBoundaryAndHeat() {
        val good = HealthRates(21.0, ready = true)
        assertNull(DeviceHealthText.warning(ThermalLevel.NORMAL, good, 30.0, true))
        val low = HealthRates(20.9, ready = true)
        assertTrue(DeviceHealthText.warning(ThermalLevel.NORMAL, low, 30.0, true)!!.contains("70%未満"))
        assertNull(DeviceHealthText.warning(ThermalLevel.NORMAL, low, 30.0, false))
        assertNull(DeviceHealthText.warning(ThermalLevel.ELEVATED, HealthRates(), 30.0, true))
        for (level in listOf(ThermalLevel.HIGH, ThermalLevel.DANGEROUS))
            assertTrue(DeviceHealthText.warning(level, good, 30.0, false)!!.contains(level.title))
    }

    @Test fun medianWindowExpiresDuringStallAndResets() {
        val meter = DeviceHealthMeter()
        meter.reset(100.0)
        listOf(90.0, 10.0, 30.0, 20.0).forEachIndexed { i, ms -> meter.processed(101.0 + i, ms) }
        assertEquals(1.0, meter.snapshot(104.0).analysisFps, 0.000001)
        assertEquals(25.0, meter.snapshot(104.0).medianJniMs!!, 0.000001)
        assertTrue(meter.snapshot(104.0).ready)
        assertEquals(20.0, meter.snapshot(106.0).medianJniMs!!, 0.000001)
        assertEquals(0.0, meter.snapshot(110.0).analysisFps, 0.000001)
        assertNull(meter.snapshot(110.0).medianJniMs)
        meter.reset(110.0)
        meter.processed(111.0, Double.NaN)
        assertFalse(meter.snapshot(111.0).ready)
        assertNull(meter.snapshot(111.0).medianJniMs)
    }
}
