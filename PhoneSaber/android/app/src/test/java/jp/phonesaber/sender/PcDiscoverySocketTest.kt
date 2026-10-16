package jp.phonesaber.sender

import android.content.Context
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
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
                              connectivity: ConnectivityManager = mock(ConnectivityManager::class.java),
                              test: (PcDiscovery) -> Unit) {
        val context = mock(Context::class.java)
        val wifi = mock(WifiManager::class.java)
        val multicast = mock(WifiManager.MulticastLock::class.java)
        `when`(context.applicationContext).thenReturn(context)
        `when`(context.getSystemService(ConnectivityManager::class.java))
            .thenReturn(connectivity)
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

    @Test fun lostWifiIsNotReselectedFromStaleNetworkSnapshot() {
        checkLostNetwork(developerOverride = false)
    }

    @Test fun lostDeveloperNetworkIsNotReselectedFromStaleDefaultNetwork() {
        org.junit.Assume.assumeTrue(BuildConfig.DEBUG)
        checkLostNetwork(developerOverride = true)
    }

    private fun checkLostNetwork(developerOverride: Boolean) {
        val connectivity = mock(ConnectivityManager::class.java)
        val lost = mock(Network::class.java)
        val replacement = mock(Network::class.java)
        val fallback = mock(Network::class.java)
        val capabilities = mock(NetworkCapabilities::class.java)
        `when`(capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)).thenReturn(true)
        `when`(connectivity.getNetworkCapabilities(lost)).thenReturn(capabilities)
        `when`(connectivity.getNetworkCapabilities(replacement)).thenReturn(capabilities)
        `when`(connectivity.getNetworkCapabilities(fallback)).thenReturn(capabilities)
        `when`(connectivity.allNetworks).thenReturn(arrayOf(lost))
        `when`(connectivity.activeNetwork).thenReturn(lost)
        val networks = mutableListOf<Network?>()
        val request = mock(NetworkRequest::class.java)
        mockConstruction(NetworkRequest.Builder::class.java, withSettings().defaultAnswer(RETURNS_SELF)) { builder, _ ->
            `when`(builder.build()).thenReturn(request)
        }.use {
            // 実ネットワークへ探索を送らず、callbackと同期getterの順序だけを再現する。
            mockConstruction(DatagramSocket::class.java) { socket, _ ->
                `when`(socket.isClosed).thenReturn(true)
            }.use { sockets ->
                withDiscovery(runPosts = true, connectivity = connectivity,
                    onUpdate = { _, network, _ -> networks.add(network) }) { discovery ->
                    try {
                        discovery.setDeveloperNetworkOverride(developerOverride)
                        discovery.start()
                        val events = ArgumentCaptor.forClass(ConnectivityManager.NetworkCallback::class.java)
                        if (developerOverride) verify(connectivity).registerDefaultNetworkCallback(events.capture())
                        else verify(connectivity).registerNetworkCallback(any(NetworkRequest::class.java), events.capture())
                        assertEquals(lost, networks.last())
                        // onLost後も同期getterが切断前のネットワークを返す。
                        events.value.onLost(lost)
                        assertNull("切断通知済みの経路を同期getterから復活させない", networks.last())
                        assertEquals(1, sockets.constructed().size)
                        // 新しい接続通知では、停止操作なしで通常どおり探索を再開する。
                        events.value.onAvailable(replacement)
                        assertEquals(replacement, networks.last())
                        assertEquals(2, sockets.constructed().size)
                        if (!developerOverride) {
                            // 別のWi-Fiが既にある場合は、配列の先頭が切断済みでも生存側へ戻る。
                            events.value.onAvailable(fallback)
                            events.value.onAvailable(replacement)
                            `when`(connectivity.allNetworks).thenReturn(arrayOf(replacement, fallback))
                            events.value.onLost(replacement)
                            assertEquals(fallback, networks.last())
                            events.value.onAvailable(replacement)
                            events.value.onLost(fallback)
                            assertEquals("非選択側の切断では現在の接続を維持する", replacement, networks.last())
                            events.value.onLost(replacement)
                            assertNull("連続切断でも以前の切断済み経路へ戻らない", networks.last())
                        }
                        events.value.onAvailable(replacement)
                        discovery.stop()
                        `when`(connectivity.allNetworks).thenReturn(emptyArray())
                        `when`(connectivity.activeNetwork).thenReturn(null)
                        discovery.start()
                        events.value.onAvailable(lost)
                        assertNull("停止前の接続とcallbackを次の世代へ持ち越さない", networks.last())
                    } finally { discovery.stop() }
                }
            }
        }
    }

    @Test fun wifiCallbackStartsDiscoveryBeforeSynchronousCapabilitiesCatchUp() {
        val connectivity = mock(ConnectivityManager::class.java)
        val nsd = mock(NsdManager::class.java)
        val wifi = mock(Network::class.java)
        val capabilities = mock(NetworkCapabilities::class.java)
        `when`(capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)).thenReturn(true)
        `when`(connectivity.allNetworks).thenReturn(emptyArray())
        // 通知と同期getterは競合し得る。Wi-Fi通知と初期LinkPropertiesが先に届く。
        `when`(connectivity.getNetworkCapabilities(wifi)).thenReturn(null)
        val address = mock(LinkAddress::class.java)
        `when`(address.address).thenReturn(InetAddress.getByName("192.168.1.50"))
        `when`(address.prefixLength).thenReturn(24)
        val properties = mock(LinkProperties::class.java)
        `when`(properties.linkAddresses).thenReturn(listOf(address))
        `when`(connectivity.getLinkProperties(wifi)).thenReturn(properties)
        val request = mock(NetworkRequest::class.java)
        mockConstruction(NetworkRequest.Builder::class.java, withSettings().defaultAnswer(RETURNS_SELF)) { builder, _ ->
            `when`(builder.build()).thenReturn(request)
        }.use {
            // 探索開始を検証する。実ネットワークへbroadcastは送らない。
            mockConstruction(DatagramSocket::class.java) { socket, _ ->
                `when`(socket.isClosed).thenReturn(true)
            }.use { sockets ->
                withDiscovery(nsd, runPosts = true, connectivity = connectivity) { discovery ->
                    try {
                        discovery.start()
                        val events = ArgumentCaptor.forClass(ConnectivityManager.NetworkCallback::class.java)
                        verify(connectivity).registerNetworkCallback(eq(request), events.capture())
                        events.value.onAvailable(wifi)
                        events.value.onCapabilitiesChanged(wifi, capabilities)
                        events.value.onLinkPropertiesChanged(wifi, properties)
                        // この後ネットワーク変更がなくても、UDPとBonjourの探索が開始済みである。
                        verify(nsd).discoverServices(eq("_phonesaber._udp."), eq(NsdManager.PROTOCOL_DNS_SD),
                            any(NsdManager.DiscoveryListener::class.java))
                        assertEquals(1, sockets.constructed().size)
                        verify(wifi).bindSocket(sockets.constructed().single())
                        discovery.stop()
                        verify(connectivity).unregisterNetworkCallback(events.value)
                        verify(nsd).stopServiceDiscovery(any(NsdManager.DiscoveryListener::class.java))
                        verify(sockets.constructed().single(), atLeastOnce()).close()
                        // 能力情報が揃った通常の停止→再開でも探索を再生成する。
                        `when`(connectivity.getNetworkCapabilities(wifi)).thenReturn(capabilities)
                        `when`(connectivity.allNetworks).thenReturn(arrayOf(wifi))
                        discovery.start()
                        assertEquals(2, sockets.constructed().size)
                        verify(wifi).bindSocket(sockets.constructed().last())
                        verify(nsd, times(2)).discoverServices(eq("_phonesaber._udp."), eq(NsdManager.PROTOCOL_DNS_SD),
                            any(NsdManager.DiscoveryListener::class.java))
                    } finally { discovery.stop() }
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
