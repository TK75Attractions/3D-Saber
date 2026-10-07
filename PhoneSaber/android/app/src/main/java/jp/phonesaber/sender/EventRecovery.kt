package jp.phonesaber.sender

// 送信の意思はライフサイクルや通信切断では消さず、停止操作だけで消す。
class SendingResumePolicy(wasSending: Boolean = false) {
    companion object {
        const val WAS_SENDING_KEY = "wasSending"
        const val AUTO_START_KEY = "autoStartSending"
    }
    var wantsSending = wasSending
        private set
    private var evaluatedStartup = false
    fun foreground(autoStart: Boolean): Boolean {
        if (!evaluatedStartup) {
            evaluatedStartup = true
            wantsSending = wantsSending || autoStart
        }
        return wantsSending
    }
    fun start() { evaluatedStartup = true; wantsSending = true }
    fun stop() { evaluatedStartup = true; wantsSending = false }
}

// 時刻は単調時計の秒。連続15分の失敗後だけ手動確認を待つ。
class RecoveryBackoff(private val limit: Double = 15.0 * 60) {
    var attempts = 0
        private set
    private var failedSince: Double? = null
    private var healthySince: Double? = null
    fun nextDelay(now: Double): Double? {
        healthySince = null
        if (failedSince == null) failedSince = now
        val remaining = limit - (now - failedSince!!)
        if (remaining <= 0) return null
        val delay = minOf(30.0, (1 shl minOf(attempts, 5)).toDouble())
        attempts++
        return minOf(delay, remaining)
    }
    fun receivedFrame(now: Double) {
        if (healthySince == null) healthySince = now
        if (now - healthySince!! >= 10) reset()
    }
    fun reset() { attempts = 0; failedSince = null; healthySince = null }
}
