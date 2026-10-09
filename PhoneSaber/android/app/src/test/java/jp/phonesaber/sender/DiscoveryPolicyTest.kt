package jp.phonesaber.sender

import org.junit.Assert.*
import org.junit.Test
import java.net.InetAddress

class DiscoveryPolicyTest {
    private fun ip(text: String) = InetAddress.getByName(text) // numeric literals only: no DNS

    @Test fun broadcastsIncludeLimitedAndDirectedIpv4() {
        val result = DiscoveryBroadcasts.addresses(listOf(ip("192.168.1.10") to 24, ip("10.0.0.5") to 8))
        assertEquals(listOf(ip("255.255.255.255"), ip("192.168.1.255"), ip("10.255.255.255")), result.toList())
    }

    @Test fun broadcastsSkipIpv6AndPointToPointPrefixes() {
        val result = DiscoveryBroadcasts.addresses(listOf(
            ip("fe80::1") to 64, ip("2001:db8::5") to 64, ip("192.168.137.2") to 31, ip("172.16.0.9") to 32,
            ip("172.16.5.9") to 30))
        assertEquals(setOf(ip("255.255.255.255"), ip("172.16.5.11")), result)
        assertEquals(setOf(ip("255.255.255.255")), DiscoveryBroadcasts.addresses(emptyList()))
    }

    @Test fun ipv6OrSameSubnetUpdatesKeepTheSameBroadcastTargets() {
        // IPv6 の RA 更新や同じサブネット内の IPv4 変更では再探索（候補消去）しない。
        val before = DiscoveryBroadcasts.addresses(listOf(ip("192.168.1.10") to 24, ip("2001:db8::5") to 64))
        val ipv6Changed = DiscoveryBroadcasts.addresses(listOf(ip("2001:db8::77") to 64, ip("192.168.1.10") to 24))
        val sameSubnet = DiscoveryBroadcasts.addresses(listOf(ip("192.168.1.42") to 24))
        val newSubnet = DiscoveryBroadcasts.addresses(listOf(ip("192.168.2.10") to 24))
        assertEquals(before, ipv6Changed)
        assertEquals(before, sameSubnet)
        assertNotEquals(before, newSubnet)
    }

    @Test fun preferredPcMatchesByNameOrAddress() {
        val udp = Destination(ip("192.168.1.5"), "MacBook-Air", "UDP探索")
        val bonjour = Destination(ip("192.168.1.5"), "Phone Saber Unity", "Bonjour")
        val other = Destination(ip("192.168.1.9"), "Windows-PC", "UDP探索")
        // 未指定なら最初の候補を受理。
        assertTrue(PreferredPcMatcher.accepts("", null, null, other))
        // UDP の PC 名で固定しても、同じ IPv4 の Bonjour 候補は同じ PC。
        assertTrue(PreferredPcMatcher.accepts("", "MacBook-Air", "192.168.1.5", udp))
        assertTrue(PreferredPcMatcher.accepts("", "MacBook-Air", "192.168.1.5", bonjour))
        assertFalse(PreferredPcMatcher.accepts("", "MacBook-Air", "192.168.1.5", other))
        // 以前の版で保存された名前だけの固定は名前で照合する。
        assertTrue(PreferredPcMatcher.accepts("", "MacBook-Air", null, udp))
        assertFalse(PreferredPcMatcher.accepts("", "MacBook-Air", null, bonjour))
        // 台指定中は台の照合が優先され、前回の PC で絞らない。
        assertTrue(PreferredPcMatcher.accepts("A", "MacBook-Air", "192.168.1.5", other))
    }

    @Test fun statusExplainsWhyAFoundPcIsNotSelected() {
        val waiting = DiscoveryStatusText.message(true, false, "", false, "", "MacBook-Air")
        assertTrue(waiting, waiting.contains("前回のPC「MacBook-Air」"))
        assertTrue(waiting, waiting.contains("停止"))
        assertEquals("PCを探索中（見つからない場合は手入力）",
            DiscoveryStatusText.message(true, false, "", false, "", null))
        assertEquals("同じ Wi-Fi の送信先", DiscoveryStatusText.message(true, false, "", true, "", null))
        assertEquals("同じ Wi-Fi に接続してください", DiscoveryStatusText.message(false, false, "", false, "", "X"))
        assertEquals("台Aの PC が見つかりません（探索中・手入力も可能）",
            DiscoveryStatusText.message(true, false, "", false, "A", null))
        assertEquals("Bonjour探索失敗 (3)", DiscoveryStatusText.message(true, false, "Bonjour探索失敗 (3)", false, "", "X"))
        assertEquals("開発用: 非Wi-Fi経路（PCのIPを手入力してください）",
            DiscoveryStatusText.message(true, true, "w", true, "", null))
    }
}
