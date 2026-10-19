package jp.phonesaber.sender

import java.net.InetAddress

object Ports {
    const val RED = 5005
    const val BLUE = 5006
    const val DISCOVERY = 5007
}

data class DiscoveryReply(val redPort: Int, val bluePort: Int, val name: String, val station: String = "") {
    val compatible: Boolean get() = redPort == Ports.RED && bluePort == Ports.BLUE
}

// 台未指定では従来どおり全候補を受理する。台名は独立した末尾 token で照合する。
object StationMatcher {
    fun reply(station: String, reply: DiscoveryReply): Boolean =
        station.isEmpty() || station == reply.station

    fun service(station: String, name: String): Boolean =
        station.isEmpty() || name.endsWith(" $station")
}

object DiscoveryProtocol {
    const val REQUEST = "PHONESABER_DISCOVER 1"

    fun parse(text: String): DiscoveryReply? {
        if (text.length > 1024 || text.any { it == '\u0000' || it == '\n' || it == '\r' }) return null
        val tokens = text.trim().split(Regex("\\s+"))
        if (tokens.size !in 5..6 || tokens[0] != "PHONESABER_UNITY" || tokens[1] != "1") return null
        val fields = mutableMapOf<String, String>()
        for (token in tokens.drop(2)) {
            val pair = token.split('=', limit = 2)
            if (pair.size != 2 || pair[0] !in setOf("red", "blue", "name", "station") ||
                pair[1].isEmpty() || fields.put(pair[0], pair[1]) != null) return null
        }
        fun port(key: String): Int? = fields[key]?.takeIf { it.all(Char::isDigit) }
            ?.toIntOrNull()?.takeIf { it in 1..65535 }
        return DiscoveryReply(port("red") ?: return null, port("blue") ?: return null,
            fields["name"] ?: return null, fields["station"] ?: "")
    }
}

/**
 * PC（Unity の F8）からの指示。探索応答（PHONESABER_UNITY）の直後に、別の datagram で 10 秒間届く。
 *   "PHONESABER_COMMAND 1 id=<番号> ignore=red|blue" / "PHONESABER_COMMAND 1 id=<番号> clear-ignore=1"
 *   "PHONESABER_COMMAND 1 id=<番号> learn-area=<秒 1..60>" / "... clear-area=1"（プレイ範囲、2026-10-18）
 *   "PHONESABER_COMMAND 1 id=<番号> exposure-bias=0|-1|-2"（露出補正 EV、2026-10-18）
 *   "PHONESABER_COMMAND 1 id=<番号> track-lock=0|1"（剣を追いかける。既定 ON、端末に保存）
 *   "PHONESABER_COMMAND 1 id=<番号> defaults=1"（剣を追いかける ON・露出 -2 に戻して保存）
 * iPhone の LANLivenessProbe.parseCommand と同じ規則: 版は 1 だけ、id 必須、ignore は red/blue だけ、
 * 知らない項目は無視、同じ項目は後のものが有効、範囲外の値はその項目が無いのと同じ。同じ id は受け手が一度だけ実行する。
 * 1つの指示は1つの操作だけ: learn-area / clear-area / exposure-bias / track-lock は他の操作と一緒なら指示ごと捨てる
 * （ignore と clear-ignore の組は 7c5a1c1 からの受け付けを変えない）。
 */
data class PcCommand(val id: Int, val ignore: String?, val clearIgnore: Boolean,
                     val learnAreaSeconds: Int? = null, val clearArea: Boolean = false,
                     val exposureBias: Int? = null, val trackLock: Boolean? = null,
                     val clip: Boolean = false, val autoClip: Boolean? = null, val defaults: Boolean = false)

object PcCommandProtocol {
    const val PREFIX = "PHONESABER_COMMAND 1 "
    const val MAXIMUM_LEARN_SECONDS = 60

    fun parse(text: String): PcCommand? {
        if (!text.startsWith(PREFIX)) return null
        var id: Int? = null
        var ignore: String? = null
        var clear = false
        var learn: Int? = null
        var clearArea = false
        var exposure: Int? = null
        var trackLock: Boolean? = null
        var clip = false
        var autoClip: Boolean? = null
        var defaults = false
        // Swift の split(separator:) と同じく空の項目は数えない。
        for (field in text.substring(PREFIX.length).split(' ').filter { it.isNotEmpty() }) {
            val parts = field.split('=', limit = 2).filter { it.isNotEmpty() }
            if (parts.size != 2) continue
            when (parts[0]) {
                "id" -> id = parts[1].toIntOrNull()
                // "both" = 赤と青をまとめて（2026-10-18、運営のキー操作 Ctrl+Shift+I）。
                "ignore" -> ignore = parts[1].takeIf { it == "red" || it == "blue" || it == "both" }
                "clear-ignore" -> clear = parts[1] == "1"
                "learn-area" -> learn = parts[1].toIntOrNull()?.takeIf { it in 1..MAXIMUM_LEARN_SECONDS }
                "clear-area" -> clearArea = parts[1] == "1"
                "defaults" -> defaults = parts[1] == "1"
                "clip" -> clip = parts[1] == "1"
                "auto-clip" -> autoClip = when (parts[1]) { "0" -> false; "1" -> true; else -> null }
                "track-lock" -> trackLock = when (parts[1]) { "0" -> false; "1" -> true; else -> null }
                "exposure-bias" -> exposure = parts[1].toIntOrNull()?.takeIf { ExposureBias.valid(it) }
            }
        }
        val legacy = (if (ignore != null) 1 else 0) + (if (clear) 1 else 0)
        val added = (if (learn != null) 1 else 0) + (if (clearArea) 1 else 0) + (if (exposure != null) 1 else 0) + (if (trackLock != null) 1 else 0) + (if (clip) 1 else 0) + (if (autoClip != null) 1 else 0) + (if (defaults) 1 else 0)
        if (id == null || legacy + added == 0 || (added > 0 && legacy + added != 1)) return null
        return PcCommand(id, ignore, clear, learn, clearArea, exposure, trackLock, clip, autoClip, defaults)
    }
}

data class Destination(val address: InetAddress, val name: String, val source: String)

// Only native text is routed: Kotlin never scales coordinates or formats numbers.
data class NativeResult(val color: Int, val fresh: Boolean, val predicted: Boolean,
                        val port: Int, val text: String?)
data class CoordinateDatagram(val port: Int, val text: String)

object PayloadRouting {
    fun datagrams(results: Array<NativeResult>): List<CoordinateDatagram> = results.mapNotNull {
        val expectedPort = when (it.color) { 0 -> Ports.RED; 1 -> Ports.BLUE; else -> return@mapNotNull null }
        if (!it.fresh || it.text == null || it.port != expectedPort) null
        else CoordinateDatagram(expectedPort, it.text)
    }
}

object ManualAddress {
    // Numeric IPv4 only: no main-thread DNS, typo-driven DNS queries or ambiguous host names.
    fun parse(value: String): InetAddress? {
        val parts = value.trim().split('.')
        if (parts.size != 4) return null
        val octets = parts.map { part ->
            if (part.isEmpty() || part.length > 3 || part.any { !it.isDigit() }) return null
            part.toIntOrNull()?.takeIf { it in 0..255 } ?: return null
        }
        val address = InetAddress.getByAddress(octets.map { it.toByte() }.toByteArray())
        return address.takeUnless { it.isAnyLocalAddress || it.isMulticastAddress ||
            it.isLoopbackAddress || octets == listOf(255, 255, 255, 255) }
    }
}

// iOS の LANLivenessProbe.formatAck と同じ ASCII 形式。
object PcAckProtocol {
    fun format(id: Int, station: String, platform: String, result: String, detail: String): String? {
        val label = station.ifEmpty { "none" }
        val atom = Regex("[A-Za-z0-9_.-]+")
        if (id < 0 || platform !in listOf("ios", "android") || !listOf(label, result, detail).all { atom.matches(it) }) return null
        return "PHONESABER_ACK 1 id=$id station=$label platform=$platform result=$result detail=$detail".takeIf { it.length < 200 }
    }
}
