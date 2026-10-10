package jp.phonesaber.sender

import androidx.camera.core.CameraState

/** カメラ解像度・fps の選択。Android の Size/Range に依存しない純粋関数（JVM テスト用）。 */
object CameraFormatPolicy {
    const val FRAME_60FPS_NS = 16_666_667L
    const val FRAME_30FPS_NS = 33_333_334L

    fun isCompact(width: Int, height: Int) = maxOf(width, height) <= 640 && minOf(width, height) <= 480

    fun selectFps(preferredFps: Int, fixed60: Boolean, compact60: Boolean) =
        if (preferredFps == 60 && fixed60 && compact60) 60 else 30

    fun maxFrameDuration(fps: Int) = if (fps == 60) FRAME_60FPS_NS else FRAME_30FPS_NS

    /**
     * iPhone と同じ方針: 要求 fps を出せる VGA 以下の最大サイズ、なければ最小サイズ。
     * [minFrameDuration] が null（YUV 表に無いサイズ・不明）や 0 のサイズは従来どおり受理する。
     */
    fun <T> order(sizes: List<T>, width: (T) -> Int, height: (T) -> Int,
                  minFrameDuration: (T) -> Long?, maxFrameDuration: Long): List<T> {
        val supported = sizes.filter { size ->
            val duration = minFrameDuration(size) ?: 0L
            duration == 0L || duration <= maxFrameDuration
        }
        val compact = supported.filter { isCompact(width(it), height(it)) }
        return if (compact.isNotEmpty()) compact.sortedByDescending { width(it).toLong() * height(it) }
            else supported.sortedBy { width(it).toLong() * height(it) }
    }
}

object CameraErrorText {
    /** CameraX の CameraState.error を、当日スタッフが対処できる日本語にする。 */
    fun describe(code: Int, fps: Int): String {
        val hint = when (code) {
            CameraState.ERROR_CAMERA_IN_USE, CameraState.ERROR_MAX_CAMERAS_IN_USE ->
                "別のアプリがカメラを使用中。他のカメラアプリを閉じてください"
            CameraState.ERROR_STREAM_CONFIG ->
                if (fps == 60) "カメラ設定に失敗。停止して「カメラ 60fps」を OFF にして開始してください"
                else "カメラ設定に失敗"
            CameraState.ERROR_CAMERA_DISABLED ->
                "カメラが無効です。クイック設定の「カメラへのアクセス」を ON にしてください"
            CameraState.ERROR_DO_NOT_DISTURB_MODE_ENABLED -> "おやすみモード（サイレント）を解除してください"
            CameraState.ERROR_CAMERA_FATAL_ERROR -> "カメラの重大なエラー。続く場合は端末を再起動してください"
            CameraState.ERROR_CAMERA_REMOVED -> "カメラが見つかりません"
            else -> "カメラの一時的なエラー"
        }
        return "カメラエラー ($code): $hint"
    }
}
