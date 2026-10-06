package jp.phonesaber.sender

import android.net.Network
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.channels.DatagramChannel
import java.util.concurrent.atomic.AtomicInteger

/** One pending frame, replaced on offer; nonblocking sends, no retries or FIFO. */
class LatestUdpSender : AutoCloseable {
    private data class Frame(val packets: List<CoordinateDatagram>, val started: Long)
    private val gate = Object()
    private var destination: Destination? = null
    private var network: Network? = null
    private var channel: DatagramChannel? = null
    private var pending: Frame? = null
    private var closed = false
    val redSent = AtomicInteger()
    val blueSent = AtomicInteger()
    @Volatile var error: String? = null
        private set
    private val worker = Thread(::loop, "PhoneSaber-UDP").apply { isDaemon = true; start() }

    fun configure(target: Destination?, wifi: Network?) = synchronized(gate) {
        if (destination == target && network == wifi) return@synchronized
        pending = null
        channel?.close()
        channel = null
        destination = target
        network = wifi
        error = null
        redSent.set(0); blueSent.set(0)
    }

    fun offer(results: Array<NativeResult>, processingStarted: Long) = synchronized(gate) {
        if (!closed && destination != null && network != null) {
            // Even an empty/held frame replaces pending fresh coordinates.
            pending = Frame(PayloadRouting.datagrams(results), processingStarted)
            gate.notifyAll()
        }
    }

    fun stop() = synchronized(gate) {
        pending = null
        destination = null
        channel?.close()
        channel = null
        redSent.set(0); blueSent.set(0)
        error = null
    }

    private fun loop() {
        while (true) synchronized(gate) {
            while (!closed && pending == null) gate.wait()
            if (closed) return
            val frame = pending ?: return@synchronized
            pending = null
            val target = destination ?: return@synchronized
            val wifi = network ?: return@synchronized
            try {
                val socket = channel ?: DatagramChannel.open().also {
                    // Bind traffic to Wi-Fi even if mobile data is the default route.
                    try {
                        it.configureBlocking(false)
                        wifi.bindSocket(it.socket())
                        channel = it
                    } catch (e: Exception) { it.close(); throw e }
                }
                for (packet in frame.packets) {
                    if (System.nanoTime() - frame.started >= 180_000_000L) break
                    val bytes = packet.text.toByteArray(Charsets.US_ASCII)
                    val sent = socket.send(ByteBuffer.wrap(bytes), InetSocketAddress(target.address, packet.port))
                    if (sent == bytes.size) {
                        if (packet.port == Ports.RED) redSent.incrementAndGet() else blueSent.incrementAndGet()
                        error = null
                    }
                    // A full OS socket drops this datagram; never retain it for retry.
                }
            } catch (e: Exception) {
                error = "UDP送信失敗: ${e.localizedMessage}"
                channel?.close()
                channel = null
            }
        }
    }

    override fun close() = synchronized(gate) {
        closed = true
        pending = null
        channel?.close()
        channel = null
        gate.notifyAll()
    }
}
