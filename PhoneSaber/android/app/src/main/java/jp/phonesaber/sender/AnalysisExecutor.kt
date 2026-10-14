package jp.phonesaber.sender

import java.util.concurrent.ScheduledThreadPoolExecutor

/** JNIとexpiryを1本に直列化し、最初の処理より前にAndroidのスレッド優先度を設定する。 */
internal fun analysisExecutor(prioritize: () -> Unit) = ScheduledThreadPoolExecutor(1) { task ->
    Thread({
        // 優先度を拒否する端末でも、登録済みworkerを既定優先度で実行し後始末まで進める。
        try { prioritize() } catch (_: SecurityException) { }
        task.run()
    }, "PhoneSaber-Analysis")
}.apply { removeOnCancelPolicy = true }
