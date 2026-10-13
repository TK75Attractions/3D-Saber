#!/usr/bin/env python3
"""Host-only comparison of the current UDP worker and a synchronous experiment.

CameraX/JNI/Wi-Fi/Android scheduling are excluded. The experiment rewrites a
TEMPORARY copy of LatestUdpSender; app sources are never modified. Run at repo
root: python3 PhoneSaber/android/core/tools/udp_dispatch_benchmark.py
"""
import os
from pathlib import Path
import subprocess
import tempfile

ANDROID = Path(__file__).resolve().parents[2]
SOURCES = ANDROID / "app/src/main/java/jp/phonesaber/sender"


def main():
    jbr = Path(os.environ.get("JAVA_HOME", "/Applications/Android Studio.app/Contents/jbr/Contents/Home"))
    cache = Path.home() / ".gradle/caches/modules-2/files-2.1"

    def jar(group, name, version):
        matches = list((cache / group / name / version).glob(f"*/{name}-{version}.jar"))
        if len(matches) != 1:
            raise RuntimeError(f"Need cached {group}:{name}:{version}")
        return matches[0]

    stdlib = jar("org.jetbrains.kotlin", "kotlin-stdlib", "2.2.10")
    annotations = jar("org.jetbrains", "annotations", "13.0")
    compiler = [jar("org.jetbrains.kotlin", name, "2.2.10") for name in
                ("kotlin-compiler-embeddable", "kotlin-reflect", "kotlin-script-runtime", "kotlin-daemon-embeddable")]
    compiler += [stdlib, annotations, jar("org.jetbrains.kotlinx", "kotlinx-coroutines-core-jvm", "1.8.0")]
    original = (SOURCES / "LatestUdpSender.kt").read_text()
    direct = original
    replacements = {
        "class LatestUdpSender": "class DirectUdpSender",
        '    private val worker = Thread(::loop, "PhoneSaber-UDP").apply { isDaemon = true; start() }': "",
        "            gate.notifyAll()": "            loop()",
        "        while (true) synchronized(gate)": "        synchronized(gate)",
        "            while (!closed && pending == null) gate.wait()": "            if (pending == null) return",
    }
    for before, after in replacements.items():
        if direct.count(before) != 1:
            raise RuntimeError(f"Sender changed; review experimental rewrite: {before}")
        direct = direct.replace(before, after)
    with tempfile.TemporaryDirectory(prefix="phonesaber-udp-host-") as directory:
        work = Path(directory)
        (work / "DirectUdpSender.kt").write_text(direct)
        # bindSocketはAndroidの経路指定だけを省略。実際のDatagramChannel.sendはそのまま。
        (work / "Network.kt").write_text("""package android.net
class Network { fun bindSocket(socket: java.net.DatagramSocket) { require(!socket.isClosed) } }
""")
        (work / "SystemClock.kt").write_text("""package android.os
object SystemClock { fun elapsedRealtimeNanos(): Long = System.nanoTime() }
""")
        (work / "Benchmark.kt").write_text('''package jp.phonesaber.sender
import android.net.Network
import java.net.DatagramSocket
import java.net.DatagramPacket
import java.net.InetAddress
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.locks.LockSupport

fun main() {
    val loopback = InetAddress.getByName("127.0.0.1")
    val sockets = listOf(Ports.RED, Ports.BLUE).map { DatagramSocket(it, loopback) }
    val received = AtomicInteger()
    val wrongPayloads = AtomicInteger()
    val expected = "0.100000,0.200000,0.300000,0.400000"
    sockets.forEach { socket ->
        Thread {
            val packet = DatagramPacket(ByteArray(512), 512)
            try { while (!socket.isClosed) {
                socket.receive(packet)
                if (String(packet.data, 0, packet.length, Charsets.US_ASCII) != expected) wrongPayloads.incrementAndGet()
                received.incrementAndGet()
                packet.length = 512
            } } catch (_: java.net.SocketException) { }
        }.apply { isDaemon = true; start() }
    }
    try {
        repeat(3) { pair ->
            for (direct in if (pair % 2 == 0) listOf(false, true) else listOf(true, false)) {
                val worker = if (!direct) LatestUdpSender() else null
                val inline = if (direct) DirectUdpSender() else null
                val target = Destination(loopback, "host", "benchmark")
                worker?.configure(target, Network()); inline?.configure(target, Network())
                val meter = worker?.healthMeter ?: inline!!.healthMeter
                val results = arrayOf(NativeResult(0, true, false, Ports.RED, expected),
                    NativeResult(1, true, false, Ports.BLUE, expected))
                val before = received.get()
                fun handled(count: Int) {
                    val until = System.nanoTime() + 1_000_000_000L
                    while (System.nanoTime() < until) {
                        val rates = meter.snapshot(System.nanoTime() / 1e9)
                        if (rates.sentSamples + rates.udpDroppedFrames == count.toLong()) return
                        LockSupport.parkNanos(1_000_000)
                    }
                    error("Sender did not finish within 1 second")
                }
                var deadline = System.nanoTime()
                for (i in 0 until 210) {
                    while (System.nanoTime() < deadline) LockSupport.parkNanos(deadline - System.nanoTime())
                    if (i == 30) { handled(30); meter.reset(System.nanoTime() / 1e9) }
                    val start = System.nanoTime()
                    worker?.offer(results, start, start); inline?.offer(results, start, start)
                    deadline = System.nanoTime() + 16_666_667L
                }
                handled(180)
                val osSent = (worker?.redSent?.get() ?: inline!!.redSent.get()) +
                    (worker?.blueSent?.get() ?: inline!!.blueSent.get())
                val until = System.nanoTime() + 1_000_000_000L
                while (received.get() - before < osSent && System.nanoTime() < until) LockSupport.parkNanos(1_000_000)
                val rates = meter.snapshot(System.nanoTime() / 1e9)
                check(rates.sentSamples + rates.udpDroppedFrames == 180L)
                check(wrongPayloads.get() == 0) { "Wrong payload" }
                check(received.get() - before == osSent) { "Loopback datagram loss" }
                println(String.format(Locale.US, "pair=%d mode=%s samples=%d median_ms=%.6f p95_ms=%.6f dropped_frames=%d os_sent=%d received=%d",
                    pair + 1, if (direct) "direct-experiment" else "production-worker", rates.sentSamples,
                    rates.medianCaptureToSendMs, rates.p95CaptureToSendMs, rates.udpDroppedFrames,
                    osSent, received.get() - before))
                worker?.close(); inline?.close()
            }
        }
    } finally { sockets.forEach { it.close() } }
}
''')
        cp = os.pathsep.join(map(str, [stdlib, annotations]))
        subprocess.run([str(jbr / "bin/java"), "-cp", os.pathsep.join(map(str, compiler)),
                        "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler", "-no-stdlib", "-no-reflect",
                        "-jvm-target", "17", "-classpath", cp, "-d", str(work / "classes"),
                        *(str(SOURCES / f"{name}.kt") for name in ("Protocol", "DeviceHealth", "LatestUdpSender")),
                        *(str(path) for path in sorted(work.glob("*.kt")))], check=True)
        subprocess.run([str(jbr / "bin/java"), "-cp", str(work / "classes") + os.pathsep + cp,
                        "jp.phonesaber.sender.BenchmarkKt"], check=True)


if __name__ == "__main__":
    main()
