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
