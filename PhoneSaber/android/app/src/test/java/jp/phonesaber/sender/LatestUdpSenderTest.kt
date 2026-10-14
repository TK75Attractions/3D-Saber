package jp.phonesaber.sender

import android.net.Network
import java.io.IOException
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.channels.DatagramChannel
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.*
import org.junit.Test
import org.mockito.ArgumentMatchers.any
import org.mockito.Mockito.*

class LatestUdpSenderTest {
    private val target = Destination(InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1)), "PC", "手入力")
    private val wifi = mock(Network::class.java)

    private fun channel(bound: CountDownLatch): DatagramChannel {
        val channel = mock(DatagramChannel::class.java)
        `when`(channel.configureBlocking(false)).thenReturn(channel)
        val socket = mock(DatagramSocket::class.java)
        `when`(channel.socket()).thenReturn(socket)
        doAnswer { bound.countDown(); null }.`when`(wifi).bindSocket(socket)
        // close は IOException を投げても終了扱い。2回目の後始末は成功させる。
        doThrow(IOException("close failed")).doNothing().`when`(channel).close()
        return channel
    }

    private fun await(latch: CountDownLatch) = assertTrue("worker timed out", latch.await(2, TimeUnit.SECONDS))

    @Test fun stagesReachTheMeterOnceAtLastSuccessfulColorSend() {
        val completed = CountDownLatch(2)
        val clockCalls = AtomicInteger()
        val socket = channel(CountDownLatch(1))
        `when`(socket.send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java)))
            .thenAnswer { call ->
                completed.countDown()
                call.getArgument<ByteBuffer>(0).remaining()
            }
        LatestUdpSender(elapsedRealtimeNanos = {
            100_093_000_000L + clockCalls.incrementAndGet() * 1_000_000L
        }) { socket }.use { sender ->
            sender.configure(target, wifi)
            sender.offer(arrayOf(NativeResult(0, true, false, Ports.RED, "1,2,3,4"),
                NativeResult(1, true, false, Ports.BLUE, "5,6,7,8")), System.nanoTime(),
                100_000_000_000L, 100_080_000_000L, 100_087_000_000L)
            await(completed)
            // 同一設定ならカウンタをリセットせず、sendと統計更新の完了を待てる。
            sender.configure(target, wifi)
            val rates = sender.healthMeter.snapshot(System.nanoTime() / 1e9)
            assertEquals(1, sender.redSent.get())
            assertEquals(1, sender.blueSent.get())
            assertEquals(2, clockCalls.get())
            assertEquals(1, rates.stageSamples)
            assertEquals(95.0, rates.medianCaptureToSendMs!!, 0.0)
            assertEquals(80.0, rates.medianSensorToAnalyzerMs!!, 0.0)
            assertEquals(7.0, rates.medianAnalyzerToJniEndMs!!, 0.0)
            assertEquals(8.0, rates.medianJniEndToSendMs!!, 0.0)
        }
    }

    @Test fun slowSendDoesNotBlockAnalyzerAndPendingFrameStaysLatest() {
        val sending = CountDownLatch(1)
        val release = CountDownLatch(1)
        val latestSent = CountDownLatch(1)
        val socket = channel(CountDownLatch(1))
        val payloads = java.util.Collections.synchronizedList(mutableListOf<String>())
        `when`(socket.send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java)))
            .thenAnswer { call ->
                val bytes = call.getArgument<ByteBuffer>(0)
                val text = Charsets.US_ASCII.decode(bytes).toString()
                payloads.add(text)
                if (text == "1,2,3,4") {
                    sending.countDown()
                    check(release.await(5, TimeUnit.SECONDS))
                } else latestSent.countDown()
                // OS満杯を再現し、Android時計に依存せず送信待ちと置換を検証する。
                0
            }
        val analyzer = Executors.newSingleThreadExecutor()
        val sender = LatestUdpSender { socket }
        fun offer(text: String) = sender.offer(
            arrayOf(NativeResult(0, true, false, Ports.RED, text)), System.nanoTime())
        try {
            sender.configure(target, wifi)
            offer("1,2,3,4")
            await(sending)
            // 前フレームのsendを止めたまま、analyzerが最新2フレームを渡して復帰できること。
            analyzer.submit { offer("5,6,7,8"); offer("9,10,11,12") }.get(2, TimeUnit.SECONDS)
            release.countDown()
            await(latestSent)
            assertEquals(listOf("1,2,3,4", "9,10,11,12"), payloads.toList())
        } finally {
            release.countDown()
            analyzer.shutdownNow()
            sender.close()
            sender.worker.join(2000)
        }
    }

    @Test fun failedColorDoesNotReplaceTheSuccessfulSendTimestamp() {
        for (throws in listOf(false, true)) {
            val completed = CountDownLatch(2)
            val clockCalls = AtomicInteger()
            val socket = channel(CountDownLatch(1))
            `when`(socket.send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java)))
                .thenAnswer { call ->
                    completed.countDown()
                    val address = call.getArgument<java.net.InetSocketAddress>(1)
                    if (address.port == Ports.BLUE) {
                        if (throws) throw IOException("blue send failed")
                        0
                    } else call.getArgument<ByteBuffer>(0).remaining()
                }
            LatestUdpSender(elapsedRealtimeNanos = {
                clockCalls.incrementAndGet()
                100_094_000_000L
            }) { socket }.use { sender ->
                sender.configure(target, wifi)
                sender.offer(arrayOf(NativeResult(0, true, false, Ports.RED, "1,2,3,4"),
                    NativeResult(1, true, false, Ports.BLUE, "5,6,7,8")), System.nanoTime(),
                    100_000_000_000L, 100_080_000_000L, 100_087_000_000L)
                await(completed)
                sender.configure(target, wifi)
                val rates = sender.healthMeter.snapshot(System.nanoTime() / 1e9)
                assertEquals(1, sender.redSent.get())
                assertEquals(0, sender.blueSent.get())
                assertEquals(1, clockCalls.get())
                assertEquals(1, rates.stageSamples)
                assertEquals(94.0, rates.medianCaptureToSendMs!!, 0.0)
                assertEquals(7.0, rates.medianJniEndToSendMs!!, 0.0)
                assertEquals(0L, rates.udpDroppedFrames)
            }
        }
    }

    @Test fun unsentFramesNeverContributeStageSamples() {
        // socket満杯、期限切れ、held/空フレームは成功送信の統計に混ぜない。
        for (mode in listOf("full", "expired", "held", "empty")) {
            val bound = CountDownLatch(1)
            val socket = channel(bound)
            `when`(socket.send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java)))
                .thenReturn(0)
            val clockCalls = AtomicInteger()
            LatestUdpSender(elapsedRealtimeNanos = {
                clockCalls.incrementAndGet()
                100_094_000_000L
            }) { socket }.use { sender ->
                sender.configure(target, wifi)
                val results = if (mode == "empty") emptyArray() else arrayOf(
                    NativeResult(0, mode != "held", false, Ports.RED, "1,2,3,4"))
                val started = System.nanoTime() - if (mode == "expired") 180_000_000L else 0L
                sender.offer(results, started, 100_000_000_000L, 100_080_000_000L, 100_087_000_000L)
                await(bound)
                sender.configure(target, wifi)
                val rates = sender.healthMeter.snapshot(System.nanoTime() / 1e9)
                assertEquals(mode, 0, clockCalls.get())
                assertEquals(mode, 0, rates.stageSamples)
                assertEquals(mode, 0, rates.sentSamples)
                assertEquals(mode, if (mode in listOf("full", "expired")) 1L else 0L, rates.udpDroppedFrames)
                verify(socket, times(if (mode == "full") 1 else 0))
                    .send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java))
            }
        }
    }

    @Test fun stopSurvivesCloseFailureAndCanRestart() {
        val firstBound = CountDownLatch(1)
        val secondBound = CountDownLatch(1)
        val first = channel(firstBound)
        val second = channel(secondBound)
        var opens = 0
        LatestUdpSender { if (opens++ == 0) first else second }.use { sender ->
            sender.configure(target, wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(firstBound)
            sender.stop()
            sender.configure(target, wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(secondBound)
            assertNull(sender.error)
        }
    }

    @Test fun reconfigureSurvivesCloseFailureAndUsesNewChannel() {
        val firstBound = CountDownLatch(1)
        val secondBound = CountDownLatch(1)
        val first = channel(firstBound)
        val second = channel(secondBound)
        var opens = 0
        LatestUdpSender { if (opens++ == 0) first else second }.use { sender ->
            sender.configure(target, wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(firstBound)
            sender.configure(target.copy(name = "PC2"), wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(secondBound)
        }
    }

    @Test fun closeFailureStillWakesAndTerminatesWorker() {
        val bound = CountDownLatch(1)
        val sender = LatestUdpSender { channel(bound) }
        try {
            sender.configure(target, wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(bound)
            sender.close()
            sender.worker.join(2000)
            assertFalse("closed worker still waiting", sender.worker.isAlive)
        } finally { sender.close() }
    }

    @Test fun sendAndCloseFailuresDoNotKillWorkerAndNextFrameReopensChannel() {
        val firstBound = CountDownLatch(1)
        val recovered = CountDownLatch(1)
        val first = channel(firstBound)
        `when`(first.send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java)))
            .thenThrow(IOException("send failed"))
        var opens = 0
        LatestUdpSender { if (opens++ == 0) first else channel(recovered) }.use { sender ->
            sender.worker.uncaughtExceptionHandler = Thread.UncaughtExceptionHandler { _, _ -> }
            sender.configure(target, wifi)
            sender.offer(arrayOf(NativeResult(0, true, false, Ports.RED, "1,2,3,4")), System.nanoTime())
            await(firstBound)
            // offerは送信を待たない。次のbind通知まで待って回復を確認する。
            sender.offer(emptyArray(), System.nanoTime())
            await(recovered)
            assertTrue(sender.worker.isAlive)
            assertTrue(sender.error.orEmpty().contains("send failed"))
            verify(first).send(any(ByteBuffer::class.java), any(java.net.SocketAddress::class.java))
        }
    }

    @Test fun bindAndCloseFailuresPreserveBindErrorAndNextFrameReopensChannel() {
        val firstBound = CountDownLatch(1)
        val recovered = CountDownLatch(1)
        val first = channel(firstBound)
        val socket = first.socket()
        doAnswer { firstBound.countDown(); throw IOException("bind failed") }
            .`when`(wifi).bindSocket(socket)
        var opens = 0
        LatestUdpSender { if (opens++ == 0) first else channel(recovered) }.use { sender ->
            sender.configure(target, wifi)
            sender.offer(emptyArray(), System.nanoTime())
            await(firstBound)
            sender.offer(emptyArray(), System.nanoTime())
            await(recovered)
            assertTrue(sender.worker.isAlive)
            assertTrue(sender.error.orEmpty().contains("bind failed"))
        }
    }
}
