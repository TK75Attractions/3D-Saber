package jp.phonesaber.sender

import android.content.Context
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
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
import org.mockito.ArgumentCaptor
import org.mockito.Mockito.*

class PcDiscoverySocketTest {
    private fun withDiscovery(nsd: NsdManager = mock(NsdManager::class.java), runPosts: Boolean = false,
                              onUpdate: (Destination?, android.net.Network?, String) -> Unit = { _, _, _ -> },
                              test: (PcDiscovery) -> Unit) {
        val context = mock(Context::class.java)
        val wifi = mock(WifiManager::class.java)
        val multicast = mock(WifiManager.MulticastLock::class.java)
        `when`(context.applicationContext).thenReturn(context)
        `when`(context.getSystemService(ConnectivityManager::class.java))
            .thenReturn(mock(ConnectivityManager::class.java))
        `when`(context.getSystemService(NsdManager::class.java)).thenReturn(nsd)
        `when`(context.getSystemService(WifiManager::class.java)).thenReturn(wifi)
        `when`(wifi.createMulticastLock("PhoneSaber-discovery")).thenReturn(multicast)
        val prefs = mock(SharedPreferences::class.java)
        `when`(prefs.edit()).thenReturn(mock(SharedPreferences.Editor::class.java, RETURNS_SELF))
        `when`(context.getSharedPreferences("destination", Context.MODE_PRIVATE)).thenReturn(prefs)
        mockStatic(ContextCompat::class.java).use { compat ->
            compat.`when`<Executor> { ContextCompat.getMainExecutor(context) }.thenReturn(Executor { it.run() })
            mockStatic(Looper::class.java).use {
                mockConstruction(Handler::class.java) { handler, _ ->
                    if (runPosts) doAnswer { call -> call.getArgument<Runnable>(0).run(); true }
                        .`when`(handler).post(any(Runnable::class.java))
                }.use {
                    test(PcDiscovery(context, { _, _ -> }, onUpdate))
                }
            }
        }
    }

    @Suppress("DEPRECATION")
    @Test fun rediscoveredServiceDoesNotAcceptAnOldAddressResolution() {
        val nsd = mock(NsdManager::class.java)
        val destinations = mutableListOf<Destination?>()
        withDiscovery(nsd, runPosts = true, onUpdate = { pc, _, _ -> destinations.add(pc) }) { discovery ->
            PcDiscovery::class.java.getDeclaredField("active").apply { isAccessible = true }
                .setBoolean(discovery, true)
            PcDiscovery::class.java.getDeclaredMethod("beginNsd", Int::class.javaPrimitiveType)
                .apply { isAccessible = true }.invoke(discovery, 0)
            val events = ArgumentCaptor.forClass(NsdManager.DiscoveryListener::class.java)
            verify(nsd).discoverServices(eq("_phonesaber._udp."), eq(NsdManager.PROTOCOL_DNS_SD), events.capture())
            fun service(address: String? = null) = mock(NsdServiceInfo::class.java).apply {
                `when`(serviceName).thenReturn("Phone Saber Unity")
                if (address != null) `when`(host).thenReturn(InetAddress.getByName(address))
            }
            val first = service()
            val returned = service()
            events.value.onServiceFound(first)
            val oldResolve = ArgumentCaptor.forClass(NsdManager.ResolveListener::class.java)
            verify(nsd).resolveService(eq(first), oldResolve.capture())
            // PCの再起動・IP変更中に、消失前の解決callbackだけが遅れて届く。
            events.value.onServiceLost(first)
            events.value.onServiceFound(returned)
            oldResolve.value.onServiceResolved(service("192.168.1.10"))
            assertTrue("消失前のIPを再び送信先にしない", destinations.all { it == null })
            val newResolve = ArgumentCaptor.forClass(NsdManager.ResolveListener::class.java)
            verify(nsd).resolveService(eq(returned), newResolve.capture())
            newResolve.value.onServiceResolved(service("192.168.1.20"))
            assertEquals("192.168.1.20", destinations.last()?.address?.hostAddress)
        }
    }

    @Suppress("DEPRECATION")
    @Test fun ordinaryDiscoveryIgnoresDuplicateNotificationsAndClearsLostService() {
        val nsd = mock(NsdManager::class.java)
        val destinations = mutableListOf<Destination?>()
        withDiscovery(nsd, runPosts = true, onUpdate = { pc, _, _ -> destinations.add(pc) }) { discovery ->
            PcDiscovery::class.java.getDeclaredField("active").apply { isAccessible = true }
                .setBoolean(discovery, true)
            PcDiscovery::class.java.getDeclaredMethod("beginNsd", Int::class.javaPrimitiveType)
                .apply { isAccessible = true }.invoke(discovery, 0)
            val events = ArgumentCaptor.forClass(NsdManager.DiscoveryListener::class.java)
            verify(nsd).discoverServices(eq("_phonesaber._udp."), eq(NsdManager.PROTOCOL_DNS_SD), events.capture())
            fun service() = mock(NsdServiceInfo::class.java).apply {
                `when`(serviceName).thenReturn("Phone Saber Unity")
            }
            val first = service()
            events.value.onServiceFound(first)
            events.value.onServiceFound(service())
            val resolve = ArgumentCaptor.forClass(NsdManager.ResolveListener::class.java)
            verify(nsd).resolveService(eq(first), resolve.capture())
            val resolved = service().apply { `when`(host).thenReturn(InetAddress.getByName("192.168.1.10")) }
            resolve.value.onServiceResolved(resolved)
            assertEquals("192.168.1.10", destinations.last()?.address?.hostAddress)
            // 同名の重複通知で別の解決を始めず、消失通知で従来どおり宛先を消す。
            events.value.onServiceFound(service())
            verify(nsd, times(1)).resolveService(any(NsdServiceInfo::class.java), any(NsdManager.ResolveListener::class.java))
            events.value.onServiceLost(first)
            assertNull(destinations.last())
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
