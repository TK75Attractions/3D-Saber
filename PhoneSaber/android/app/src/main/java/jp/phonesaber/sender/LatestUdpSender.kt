package jp.phonesaber.sender

import android.net.Network
import android.os.SystemClock
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.channels.DatagramChannel
import java.util.concurrent.atomic.AtomicInteger

/** One pending frame, replaced on offer; nonblocking sends, no retries or FIFO. */
class LatestUdpSender internal constructor(
    private val elapsedRealtimeNanos: () -> Long = SystemClock::elapsedRealtimeNanos,
    private val openChannel: () -> DatagramChannel) : AutoCloseable {
    constructor() : this(openChannel = { DatagramChannel.open() })
    private data class Frame(val packets: List<CoordinateDatagram>, val started: Long, val capturedNs: Long?,
                             val analyzerStartedNs: Long?, val jniEndedNs: Long?)
    internal val healthMeter = DeviceHealthMeter()
    private val gate = Object()
    // offerをsocket処理から分離する。制御操作はgate→pendingGateの順で同期する。
    private val pendingGate = Object()
    private var destination: Destination? = null
    private var network: Network? = null
    private var channel: DatagramChannel? = null
    private var pending: Frame? = null
    private var closed = false
    val redSent = AtomicInteger()
    val blueSent = AtomicInteger()
    @Volatile var error: String? = null
        private set
    internal val worker = Thread(::loop, "PhoneSaber-UDP").apply { isDaemon = true; start() }

    fun configure(target: Destination?, wifi: Network?) = synchronized(gate) { synchronized(pendingGate) pending@ {
        if (destination == target && network == wifi) return@pending
        pending = null
        channel?.closeQuietly()
        channel = null
        destination = target
        network = wifi
        error = null
        redSent.set(0); blueSent.set(0)
    } }

    fun offer(results: Array<NativeResult>, processingStarted: Long, capturedNs: Long? = null,
              analyzerStartedNs: Long? = null, jniEndedNs: Long? = null) = synchronized(pendingGate) {
        if (!closed && destination != null && network != null) {
            // Even an empty/held frame replaces pending fresh coordinates.
            pending?.let { healthMeter.droppedFrame(it.packets.isNotEmpty()) }
            pending = Frame(PayloadRouting.datagrams(results), processingStarted, capturedNs,
                analyzerStartedNs, jniEndedNs)
            pendingGate.notifyAll()
        }
    }

    fun stop() = synchronized(gate) { synchronized(pendingGate) {
        pending = null
        destination = null
        channel?.closeQuietly()
        channel = null
        redSent.set(0); blueSent.set(0)
        error = null
    } }

    private fun loop() {
        while (true) {
            synchronized(pendingGate) {
                while (!closed && pending == null) pendingGate.wait()
                if (closed) return
            }
            sendLatest()
        }
    }

    private fun sendLatest() {
        synchronized(gate) {
            // 制御操作後に最新を取り出す。stop/reconfigure前のフレームは送らない。
            val frame = synchronized(pendingGate) {
                if (closed) null else pending.also { pending = null }
            } ?: return@synchronized
            val target = destination ?: return@synchronized
            val wifi = network ?: return@synchronized
            var completedNs: Long? = null
            var redSucceeded = false
            var blueSucceeded = false
            try {
                val socket = channel ?: openChannel().also {
                    // Bind traffic to Wi-Fi even if mobile data is the default route.
                    try {
                        it.configureBlocking(false)
                        wifi.bindSocket(it.socket())
                        channel = it
                    } catch (e: Exception) { it.closeQuietly(); throw e }
                }
                for (packet in frame.packets) {
                    if (System.nanoTime() - frame.started >= 180_000_000L) break
                    val bytes = packet.text.toByteArray(Charsets.US_ASCII)
                    val sent = socket.send(ByteBuffer.wrap(bytes), InetSocketAddress(target.address, packet.port))
                    if (sent == bytes.size) {
                        completedNs = elapsedRealtimeNanos()
                        if (packet.port == Ports.RED) {
                            redSucceeded = true
                            redSent.incrementAndGet()
                        } else {
                            blueSucceeded = true
                            blueSent.incrementAndGet()
                        }
                        error = null
                    }
                    // A full OS socket drops this datagram; never retain it for retry.
                }
            } catch (e: Exception) {
                error = "UDP送信失敗: ${e.localizedMessage}"
                channel?.closeQuietly()
                channel = null
            } finally {
                // 両色成功でもフレーム1件。置換・期限切れ・socket満杯は計測しない。
                // 統計のlockを赤と青のsendの間に挟まない。
                val at = System.nanoTime() / 1e9
                if (redSucceeded) healthMeter.datagramSent(at, true)
                if (blueSucceeded) healthMeter.datagramSent(at, false)
                completedNs?.let { healthMeter.sent(at, frame.capturedNs, it,
                    frame.analyzerStartedNs, frame.jniEndedNs) }
                if (completedNs == null) healthMeter.droppedFrame(frame.packets.isNotEmpty())
            }
        }
    }

    override fun close() = synchronized(gate) { synchronized(pendingGate) {
        closed = true
        pending = null
        channel?.closeQuietly()
        channel = null
        pendingGate.notifyAll()
    } }

    private fun DatagramChannel.closeQuietly() {
        // close失敗でも終了済み。停止・worker通知を続け、送信/bindの元の例外を保持する。
        try { close() } catch (_: IOException) { }
    }
}
