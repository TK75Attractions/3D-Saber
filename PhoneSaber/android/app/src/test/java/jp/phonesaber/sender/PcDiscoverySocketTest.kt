package jp.phonesaber.sender

import android.content.Context
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.nsd.NsdManager
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import androidx.core.content.ContextCompat
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketException
import java.util.concurrent.Executor
import org.junit.Assert.*
import org.junit.Test
import org.mockito.Mockito.*

class PcDiscoverySocketTest {
    private fun withDiscovery(test: (PcDiscovery) -> Unit) {
        val context = mock(Context::class.java)
        val wifi = mock(WifiManager::class.java)
        val multicast = mock(WifiManager.MulticastLock::class.java)
        `when`(context.applicationContext).thenReturn(context)
        `when`(context.getSystemService(ConnectivityManager::class.java))
            .thenReturn(mock(ConnectivityManager::class.java))
        `when`(context.getSystemService(NsdManager::class.java)).thenReturn(mock(NsdManager::class.java))
        `when`(context.getSystemService(WifiManager::class.java)).thenReturn(wifi)
        `when`(wifi.createMulticastLock("PhoneSaber-discovery")).thenReturn(multicast)
        `when`(context.getSharedPreferences("destination", Context.MODE_PRIVATE))
            .thenReturn(mock(SharedPreferences::class.java))
        mockStatic(ContextCompat::class.java).use { compat ->
            compat.`when`<Executor> { ContextCompat.getMainExecutor(context) }.thenReturn(Executor { it.run() })
            mockStatic(Looper::class.java).use {
                mockConstruction(Handler::class.java).use {
                    test(PcDiscovery(context, { _, _ -> }, { _, _, _ -> }))
                }
            }
        }
    }

    // 実ソケットの所有権を検証する。Android の通知だけを mock し、UDP の close は置き換えない。
    private fun runLoop(discovery: PcDiscovery, socket: DatagramSocket, token: Int) {
        val loop = PcDiscovery::class.java.getDeclaredMethod("broadcastLoop",
            DatagramSocket::class.java, Set::class.java, Int::class.javaPrimitiveType)
        loop.isAccessible = true
        loop.invoke(discovery, socket, emptySet<InetAddress>(), token)
    }

    @Test fun receiveFailureClosesSocketBeforeWorkerReturns() = withDiscovery { discovery ->
        val socket = object : DatagramSocket(0, InetAddress.getLoopbackAddress()) {
            override fun receive(packet: DatagramPacket) { throw SocketException("Network is unreachable") }
        }
        socket.use {
            runLoop(discovery, socket, 0)
            assertTrue("受信エラーで終了した worker はソケットを閉じる", socket.isClosed)
        }
    }

    @Test fun obsoleteGenerationClosesSocketWithoutReceiving() = withDiscovery { discovery ->
        DatagramSocket(0, InetAddress.getLoopbackAddress()).use { socket ->
            runLoop(discovery, socket, -1)
            assertTrue("古い世代の worker はソケットを閉じる", socket.isClosed)
        }
    }
}
