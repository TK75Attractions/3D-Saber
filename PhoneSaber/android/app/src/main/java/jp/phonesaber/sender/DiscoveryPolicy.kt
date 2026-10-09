package jp.phonesaber.sender

import java.net.Inet4Address
import java.net.InetAddress

/** UDP探索の送信先: 255.255.255.255 と各IPv4サブネットの directed broadcast（prefix 1–30）。 */
object DiscoveryBroadcasts {
    fun addresses(links: List<Pair<InetAddress, Int>>): Set<InetAddress> {
        val result = linkedSetOf(InetAddress.getByAddress(byteArrayOf(-1, -1, -1, -1)))
        for ((address, prefixLength) in links) {
            if (address !is Inet4Address || prefixLength !in 1..30) continue
            val ip = address.address.fold(0L) { value, byte -> (value shl 8) or (byte.toLong() and 255) }
            val mask = (0xffffffffL shl (32 - prefixLength)) and 0xffffffffL
            val broadcast = ip or (mask xor 0xffffffffL)
            result += InetAddress.getByAddress(ByteArray(4) { i ->
                ((broadcast shr (24 - 8 * i)) and 255).toByte()
            })
        }
        return result
    }
}

/**
 * 自動探索で維持する前回のPC。UDP応答の name はPC名、Bonjour は Unity 共通のサービス名なので、
 * 同じPCでも経路によって名前が違う。名前か IPv4 のどちらかが一致すれば同じPCとして受理する。
 */
object PreferredPcMatcher {
    fun accepts(station: String, pinnedName: String?, pinnedAddress: String?, candidate: Destination): Boolean =
        station.isNotEmpty() || pinnedName == null || candidate.name == pinnedName ||
            (pinnedAddress != null && candidate.address.hostAddress == pinnedAddress)
}

object DiscoveryStatusText {
    /** [waitingForPinned] は、前回のPC以外の候補だけが見つかっている間の前回PC名。 */
    fun message(hasNetwork: Boolean, developerNonWifi: Boolean, warning: String, hasDestination: Boolean,
                station: String, waitingForPinned: String?): String = when {
        !hasNetwork -> "同じ Wi-Fi に接続してください"
        developerNonWifi -> "開発用: 非Wi-Fi経路（PCのIPを手入力してください）"
        warning.isNotEmpty() -> warning
        !hasDestination && station.isNotEmpty() -> "台${station}の PC が見つかりません（探索中・手入力も可能）"
        !hasDestination && waitingForPinned != null ->
            "前回のPC「$waitingForPinned」を探索中（別のPCは自動では選びません。切り替えは「停止」で解除" +
                "（停止中なら 開始→停止）、または手入力）"
        !hasDestination -> "PCを探索中（見つからない場合は手入力）"
        else -> "同じ Wi-Fi の送信先"
    }
}
