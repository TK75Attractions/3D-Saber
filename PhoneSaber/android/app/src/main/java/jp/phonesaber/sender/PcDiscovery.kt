package jp.phonesaber.sender

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.ArrayDeque

/** Main-thread lifecycle/selection, one independent broadcast receive thread per generation. */
class PcDiscovery(context: Context,
                  private val onUpdate: (Destination?, Network?, String) -> Unit) {
    private val mainExecutor = androidx.core.content.ContextCompat.getMainExecutor(context)
    private var resolveBusy = false
    private var resumeResolve: (() -> Unit)? = null
    private val main = Handler(Looper.getMainLooper())
    private val connectivity = requireNotNull(context.getSystemService(ConnectivityManager::class.java))
    private val nsd = requireNotNull(context.getSystemService(NsdManager::class.java))
    private val multicast = requireNotNull(context.applicationContext.getSystemService(WifiManager::class.java))
        .createMulticastLock("PhoneSaber-discovery").apply { setReferenceCounted(false) }
    private var active = false
    private var lifecycle = 0
    @Volatile private var generation = 0
    private var network: Network? = null
    private var socket: DatagramSocket? = null
    private var listener: NsdManager.DiscoveryListener? = null
    private var callback: ConnectivityManager.NetworkCallback? = null
    private var manual: Destination? = null
    private var station = ""
    private data class Found(val destination: Destination, val seen: Long)
    private val found = linkedMapOf<String, Found>()
    private var selectedKey: String? = null
    private var warning = ""

    fun setStation(value: String) {
        if (station == value) return
        station = value
        if (active) restartDiscovery(network?.let(connectivity::getLinkProperties))
        else { found.clear(); selectedKey = null; publish() }
    }

    fun setManual(address: InetAddress?) {
        manual = address?.let { Destination(it, "手入力", "手入力") }
        publish()
    }

    fun start() {
        if (active) return
        active = true
        val session = ++lifecycle
        val request = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
        val events = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(wifi: Network) { main.post { if (active && session == lifecycle) switchNetwork(wifi) } }
            override fun onLinkPropertiesChanged(wifi: Network, properties: LinkProperties) {
                main.post { if (active && session == lifecycle && wifi == network) restartDiscovery(properties) }
            }
            override fun onLost(wifi: Network) {
                main.post {
                    if (active && session == lifecycle && wifi == network) {
                        switchNetwork(null)
                        connectivity.allNetworks.firstOrNull { candidate ->
                            connectivity.getNetworkCapabilities(candidate)
                                ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
                        }?.let(::switchNetwork)
                    }
                }
            }
        }
        callback = events
        connectivity.registerNetworkCallback(request, events)
        connectivity.allNetworks.firstOrNull {
            connectivity.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        }?.let(::switchNetwork)
        publish()
    }

    private fun switchNetwork(wifi: Network?) {
        if (network == wifi) return
        network = wifi
        restartDiscovery(wifi?.let(connectivity::getLinkProperties))
    }

    private fun restartDiscovery(properties: LinkProperties?) {
        endDiscovery()
        found.clear(); selectedKey = null; warning = ""
        publish()
        val wifi = network ?: return
        val token = generation
        try { multicast.acquire() } catch (e: Exception) {
            warning = "探索ロック失敗: ${e.localizedMessage}"
        }
        val broadcasts = mutableSetOf(InetAddress.getByName("255.255.255.255"))
        properties?.linkAddresses?.forEach { link ->
            if (link.address is Inet4Address && link.prefixLength in 1..30) {
                val bytes = link.address.address
                val ip = bytes.fold(0L) { value, byte -> (value shl 8) or (byte.toLong() and 255) }
                val mask = (0xffffffffL shl (32 - link.prefixLength)) and 0xffffffffL
                val broadcast = ip or (mask xor 0xffffffffL)
                broadcasts += InetAddress.getByAddress(ByteArray(4) { i ->
                    ((broadcast shr (24 - 8 * i)) and 255).toByte()
                })
            }
        }
        try {
            val udp = DatagramSocket(null).apply {
                try {
                    wifi.bindSocket(this)
                    bind(java.net.InetSocketAddress(0))
                    broadcast = true
                    soTimeout = 500
                } catch (e: Exception) { close(); throw e }
            }
            socket = udp
            Thread({ broadcastLoop(udp, broadcasts, token) }, "PhoneSaber-discovery").apply {
                isDaemon = true; start()
            }
        } catch (e: Exception) { warning = "UDP探索失敗: ${e.localizedMessage}" }
        beginNsd(token)
        main.post(object : Runnable {
            override fun run() {
                if (!active || token != generation) return
                val now = System.nanoTime()
                found.entries.removeAll { it.value.destination.source == "UDP探索" &&
                    now - it.value.seen > 8_000_000_000L }
                publish()
                main.postDelayed(this, 1000)
            }
        })
        publish()
    }

    private fun broadcastLoop(udp: DatagramSocket, addresses: Set<InetAddress>, token: Int) {
        val request = DiscoveryProtocol.REQUEST.toByteArray(Charsets.US_ASCII)
        val bytes = ByteArray(1025)
        var nextRequest = 0L
        while (token == generation && !udp.isClosed) {
            try {
                val now = System.nanoTime()
                if (now >= nextRequest) {
                    for (address in addresses) {
                        // One failed directed broadcast must not suppress the other address.
                        try { udp.send(DatagramPacket(request, request.size, address, Ports.DISCOVERY)) }
                        catch (_: Exception) { if (udp.isClosed) return }
                    }
                    nextRequest = now + 2_000_000_000L
                }
                val packet = DatagramPacket(bytes, bytes.size)
                udp.receive(packet)
                val reply = DiscoveryProtocol.parse(String(bytes, 0, packet.length, Charsets.UTF_8)) ?: continue
                if (!reply.compatible || packet.port != Ports.DISCOVERY ||
                    packet.address.isAnyLocalAddress || packet.address.isMulticastAddress) continue
                main.post {
                    if (active && token == generation && StationMatcher.reply(station, reply)) {
                        val destination = Destination(packet.address, reply.name, "UDP探索")
                        val key = "udp:${packet.address.hostAddress}"
                        if (key in found || found.size < 16) found[key] = Found(destination, System.nanoTime())
                        publish()
                    }
                }
            } catch (_: SocketTimeoutException) {
                // Wake to resend discovery; no coordinate traffic on this socket.
            } catch (e: Exception) {
                main.post {
                    if (active && token == generation) {
                        warning = "UDP探索失敗: ${e.localizedMessage}"; publish()
                    }
                }
                return
            }
        }
    }

    @Suppress("DEPRECATION") // API 29-compatible resolver; serialize requests to avoid ALREADY_ACTIVE.
    private fun beginNsd(token: Int) {
        val queue = ArrayDeque<NsdServiceInfo>()
        val present = mutableSetOf<String>()
        fun resolveNext() {
            if (token != generation || queue.isEmpty()) return
            if (resolveBusy) { resumeResolve = { resolveNext() }; return }
            val service = queue.removeFirst()
            resolveBusy = true
            resumeResolve = null
            try {
                nsd.resolveService(service, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(info: NsdServiceInfo, code: Int) {
                        main.post {
                            resolveBusy = false
                            if (token == generation) resolveNext()
                            else resumeResolve?.also { resumeResolve = null }?.invoke()
                        }
                    }
                    override fun onServiceResolved(info: NsdServiceInfo) {
                        main.post {
                            resolveBusy = false
                            if (active && token == generation) {
                                val host = if (android.os.Build.VERSION.SDK_INT >= 34) {
                                    info.hostAddresses.firstOrNull { it is Inet4Address }
                                        ?: info.hostAddresses.firstOrNull()
                                } else info.host
                                if (info.serviceName in present && StationMatcher.service(station, info.serviceName) &&
                                    host != null && !host.isAnyLocalAddress) {
                                    // Bonjour service port is discovery metadata. Coordinates use fixed ports.
                                    val key = "nsd:${info.serviceName}"
                                    if (key in found || found.size < 16) found[key] = Found(
                                        Destination(host, info.serviceName, "Bonjour"), System.nanoTime())
                                    publish()
                                }
                                resolveNext()
                            } else resumeResolve?.also { resumeResolve = null }?.invoke()
                        }
                    }
                })
            } catch (e: Exception) {
                resolveBusy = false
                warning = "Bonjour解決失敗: ${e.localizedMessage}"; publish()
                main.post { if (token == generation) resolveNext() }
            }
        }
        val events = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(type: String) = Unit
            override fun onDiscoveryStopped(type: String) = Unit
            override fun onStartDiscoveryFailed(type: String, code: Int) {
                main.post { if (token == generation) { warning = "Bonjour探索失敗 ($code)"; publish() } }
            }
            override fun onStopDiscoveryFailed(type: String, code: Int) = Unit
            override fun onServiceFound(info: NsdServiceInfo) {
                main.post {
                    if (!active || token != generation || !StationMatcher.service(station, info.serviceName) ||
                        !present.add(info.serviceName)) return@post
                    if (queue.size < 16) { queue.add(info); resolveNext() }
                }
            }
            override fun onServiceLost(info: NsdServiceInfo) {
                main.post {
                    if (token == generation) {
                        present.remove(info.serviceName)
                        queue.removeAll { it.serviceName == info.serviceName }
                        found.remove("nsd:${info.serviceName}")
                        publish()
                    }
                }
            }
        }
        listener = events
        try {
            if (android.os.Build.VERSION.SDK_INT >= 33) {
                nsd.discoverServices("_phonesaber._udp.", NsdManager.PROTOCOL_DNS_SD,
                    network, mainExecutor, events)
            } else nsd.discoverServices("_phonesaber._udp.", NsdManager.PROTOCOL_DNS_SD, events)
        }
        catch (e: Exception) { warning = "Bonjour探索失敗: ${e.localizedMessage}" }
    }

    private fun publish() {
        if (selectedKey?.let(found::containsKey) != true) selectedKey = found.keys.firstOrNull()
        val destination = manual ?: selectedKey?.let { found[it]?.destination }
        val message = if (network == null) "同じ Wi-Fi に接続してください" else if (warning.isNotEmpty()) warning
            else if (destination == null && station.isNotEmpty()) "台${station}の PC が見つかりません（探索中・手入力も可能）"
            else if (destination == null) "PCを探索中（見つからない場合は手入力）" else "同じ Wi-Fi の送信先"
        onUpdate(destination, network, message)
    }

    private fun endDiscovery() {
        generation++
        resumeResolve = null
        socket?.close(); socket = null
        listener?.let { try { nsd.stopServiceDiscovery(it) } catch (_: Exception) { } }
        listener = null
        if (multicast.isHeld) multicast.release()
    }

    fun stop() {
        active = false
        lifecycle++
        callback?.let { connectivity.unregisterNetworkCallback(it) }
        callback = null
        endDiscovery()
        network = null
        found.clear(); selectedKey = null
    }
}
