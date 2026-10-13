package jp.phonesaber.sender

/** 単調時計の秒で探索応答を監視する。座標UDPの受信確認ではなく、表示だけに使う。 */
class PcReplyWatch {
    companion object {
        const val WARN_AFTER = 3.0
        const val AUTOMATIC_MESSAGE = "PC（Unity）から3秒以上応答がありません。同じ Wi-Fi・PCの起動・ファイアウォールを確認してください。"
        const val MANUAL_MESSAGE = "手入力PCから3秒以上Unityの応答がありません（Unity以外の受信先では確認できません）。"
    }

    private var target: String? = null
    private var waitingSince: Double? = null
    private var lastReplyAt: Double? = null

    fun reset() { target = null; waitingSince = null; lastReplyAt = null }

    fun ageSeconds(now: Double): Double? = lastReplyAt?.let { maxOf(0.0, now - it) }

    fun received(address: String, now: Double) {
        val since = waitingSince ?: return
        if (address == target && now >= since) lastReplyAt = maxOf(lastReplyAt ?: since, now)
    }

    fun update(sending: Boolean, address: String?, manual: Boolean, now: Double): String? {
        if (!sending) { reset(); return null }
        if (waitingSince == null || target != address) {
            // 候補の8秒失効で既に出ている警告を消さない。新しい宛先には確認の猶予を与える。
            if (waitingSince == null || address != null) waitingSince = now
            target = address
            lastReplyAt = null
        }
        val since = maxOf(waitingSince!!, lastReplyAt ?: waitingSince!!)
        return if (now - since >= WARN_AFTER) {
            if (manual) MANUAL_MESSAGE else AUTOMATIC_MESSAGE
        } else null
    }
}
