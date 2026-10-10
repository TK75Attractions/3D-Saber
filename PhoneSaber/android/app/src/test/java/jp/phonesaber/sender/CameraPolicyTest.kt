package jp.phonesaber.sender

import androidx.camera.core.CameraState
import org.junit.Assert.*
import org.junit.Test

class CameraPolicyTest {
    private data class Size(val width: Int, val height: Int)

    private fun order(sizes: List<Size>, durations: Map<Size, Long?>, fps: Int) =
        CameraFormatPolicy.order(sizes, Size::width, Size::height, { durations[it] },
            CameraFormatPolicy.maxFrameDuration(fps))

    @Test fun fps60NeedsSwitchFixedRangeAndCompactSize() {
        assertEquals(60, CameraFormatPolicy.selectFps(60, fixed60 = true, compact60 = true))
        assertEquals(30, CameraFormatPolicy.selectFps(60, fixed60 = false, compact60 = true))
        assertEquals(30, CameraFormatPolicy.selectFps(60, fixed60 = true, compact60 = false))
        assertEquals(30, CameraFormatPolicy.selectFps(30, fixed60 = true, compact60 = true))
        assertTrue(CameraFormatPolicy.isCompact(480, 640))
        assertFalse(CameraFormatPolicy.isCompact(720, 480))
        assertFalse(CameraFormatPolicy.isCompact(640, 640))
    }

    @Test fun picksLargestCompactSizeThatMeetsTheFrameDuration() {
        val vga = Size(640, 480); val qvga = Size(320, 240); val hd = Size(1280, 720)
        val durations = mapOf(vga to 33_333_333L, qvga to 16_666_666L, hd to 16_666_666L)
        assertEquals(listOf(vga, qvga), order(listOf(qvga, hd, vga), durations, 30))
        // 60fps では VGA が 1/30 秒しか出せないので QVGA。
        assertEquals(listOf(qvga), order(listOf(qvga, hd, vga), durations, 60))
        // VGA 以下が無ければ最小サイズから。
        assertEquals(listOf(hd, Size(1920, 1080)), order(listOf(Size(1920, 1080), hd),
            mapOf(hd to 16_666_666L, Size(1920, 1080) to 16_666_666L), 60))
    }

    @Test fun unknownDurationIsAcceptedInsteadOfFailingTheBind() {
        // Preview(PRIVATE) の候補が YUV 表に無い場合、lookup は null（例外で bind を失敗させない）。
        val vga = Size(640, 480); val odd = Size(600, 450)
        assertEquals(listOf(vga, odd), order(listOf(odd, vga), mapOf(vga to null, odd to 0L), 60))
    }

    @Test fun cameraErrorsGiveActionableJapaneseHints() {
        val config60 = CameraErrorText.describe(CameraState.ERROR_STREAM_CONFIG, 60)
        assertTrue(config60, config60.startsWith("カメラエラー (${CameraState.ERROR_STREAM_CONFIG}): "))
        assertTrue(config60, config60.contains("60fps"))
        assertFalse(CameraErrorText.describe(CameraState.ERROR_STREAM_CONFIG, 30).contains("60fps"))
        assertTrue(CameraErrorText.describe(CameraState.ERROR_CAMERA_IN_USE, 30).contains("別のアプリ"))
        assertTrue(CameraErrorText.describe(CameraState.ERROR_CAMERA_DISABLED, 30).contains("カメラへのアクセス"))
        assertTrue(CameraErrorText.describe(CameraState.ERROR_DO_NOT_DISTURB_MODE_ENABLED, 30).contains("おやすみ"))
        assertEquals("カメラエラー (99): カメラの一時的なエラー", CameraErrorText.describe(99, 30))
    }
}
