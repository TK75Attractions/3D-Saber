package jp.phonesaber.sender

import java.util.concurrent.ScheduledThreadPoolExecutor

/** JNIとexpiryを1本に直列化し、最初の処理より前にAndroidのスレッド優先度を設定する。 */
internal fun analysisExecutor(prioritize: () -> Unit) = ScheduledThreadPoolExecutor(1) { task ->
    Thread({ prioritize(); task.run() }, "PhoneSaber-Analysis")
}.apply { removeOnCancelPolicy = true }
