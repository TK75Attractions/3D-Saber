package jp.phonesaber.sender

import android.net.Network
import java.io.IOException
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.channels.DatagramChannel
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
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
            // gate の取得は最初の送信と後始末が終わってから。
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
