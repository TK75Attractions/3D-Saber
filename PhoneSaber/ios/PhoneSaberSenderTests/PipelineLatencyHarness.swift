// Mac で実送信キューの待ち時間を再現する。カメラ・検出・無線伝送は計測対象外。
// 実行コマンドは docs/claude/ios-pipeline.md を参照。
#if PIPELINE_LATENCY_HARNESS || PIPELINE_ROUTING_STANDALONE
import Foundation
import CoreMedia
import Darwin

enum HostMonotonicClock {
    static func now() -> TimeInterval {
        CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock()))
    }
}

#if PIPELINE_LATENCY_HARNESS
@main
struct PipelineLatencyHarness {
    static func main() {
        DispatchQueue.global(qos: .userInteractive).async {
            let network = CommandLine.arguments.contains("--network")
            let receiver = network ? LoopbackReceiver() : nil
            defer { receiver?.closeSocket() }
            let port = receiver?.port ?? 5005
            for busy in [false, true] {
                let sender = network ? UDPSender() : UDPSender { _, _, completion in
                    completion(.success(ProcessInfo.processInfo.systemUptime))
                }
                sender.configure(host: "127.0.0.1", ports: [port])
#if PIPELINE_DIRECT
                let delivery = CoordinateDelivery(sender: sender, p2pSender: P2PSender(), lanProbe: LANLivenessProbe())
                var settings = CoordinateDelivery.Settings()
                settings.running = true
                settings.lanConfigured = true
                delivery.update(settings)
#endif
                var times: [Double] = []
                for index in 0..<220 {
                    if busy {
                        let occupied = DispatchSemaphore(value: 0)
                        DispatchQueue.main.async {
                            occupied.signal()
                            usleep(8_000)
                        }
                        occupied.wait()
                    }
                    let done = DispatchSemaphore(value: 0)
                    let start = HostMonotonicClock.now()
                    let send = {
                        let text = payload(for: (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20)),
                                           source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
                        let started: (TimeInterval, Int) -> Void = { _, _ in
                            if index >= 20 { times.append((HostMonotonicClock.now() - start) * 1000) }
                        }
#if PIPELINE_DIRECT
                        delivery.route(text, port: port, settings: settings, onSendStarted: started,
                                       completion: { _ in done.signal() }, noRoute: { fatalError("no route") })
#else
                        sender.send(text, to: port, onSendStarted: started, completion: { _ in done.signal() })
#endif
                    }
#if PIPELINE_DIRECT
                    send()
#else
                    Task { @MainActor in send() }
#endif
                    guard done.wait(timeout: .now() + 5) == .success else { fatalError("send timeout") }
                    // 送信完了と受信を待ち、次のサンプルへバックログを持ち越さない。
                    receiver?.receive()
                    if busy { DispatchQueue.main.sync {} }
                }
                sender.stop()
                times.sort()
                let mode = busy ? "main-busy-8ms" : "idle"
                print(String(format: "[PipelineLatency] %@ %@ n=%d median=%.3f p95=%.3f max=%.3f ms",
                             network ? "NW-loopback" : "send-hook", mode,
                             times.count, times[100], times[189], times.last!))
            }
            exit(0)
        }
        dispatchMain()
    }
}

// 実 NWConnection 経路も測定できる。アプリの RED/BLUE ポートは占有しない。
private final class LoopbackReceiver {
    let fd: Int32
    let port: Int
    init() {
        let socketFD = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        fd = socketFD
        guard socketFD >= 0 else { fatalError("socket errno=\(errno)") }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(socketFD, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0 else { fatalError("bind errno=\(errno)") }
        var size = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(socketFD, $0, &size) }
        }
        guard named == 0 else { fatalError("getsockname errno=\(errno)") }
        port = Int(UInt16(bigEndian: address.sin_port))
        var timeout = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
    }
    func receive() {
        var bytes = [UInt8](repeating: 0, count: 256)
        let count = bytes.withUnsafeMutableBytes { recv(fd, $0.baseAddress, $0.count, 0) }
        guard count > 0 else { fatalError("recv errno=\(errno)") }
    }
    func closeSocket() { Darwin.close(fd) }
}
#else
import XCTest

@main
struct PipelineRoutingHarness {
    static func main() {
        let suite = CoordinateDeliveryTests.defaultTestSuite
        suite.run()
        guard let run = suite.testRun, run.executionCount == 6, run.hasSucceeded else { exit(1) }
    }
}
#endif
#endif
