package jp.phonesaber.sender

// adb の型付き extras だけを受理し、保存設定には書き戻さない。
data class LaunchSettings(private val autoStartSending: Boolean?, private val fps: Int?,
                          val lowLatencyCamera: Boolean = false) {
    companion object {
        fun read(extra: (String) -> Any?): LaunchSettings = LaunchSettings(
            extra(SendingResumePolicy.AUTO_START_KEY) as? Boolean,
            (extra("cameraFPS") as? Int)?.takeIf { it == 30 || it == 60 },
            extra("lowLatencyCamera") as? Boolean ?: false)
    }
    fun autoStart(saved: Boolean) = autoStartSending ?: saved
    fun cameraFps(saved: Int) = fps ?: saved
    val persistAutomaticResume get() = autoStartSending == null
}
