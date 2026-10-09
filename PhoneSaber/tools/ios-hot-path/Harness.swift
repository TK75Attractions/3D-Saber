import Foundation
import Network
import Darwin
import CoreMedia
import CoreVideo

@_silgen_name("ps_alloc_start") func allocationStart()
@_silgen_name("ps_alloc_count") func allocationCount() -> UInt64
@_silgen_name("ps_cpu_seconds") func cpuSeconds() -> Double

// 本番ポートを占有せず、実 NWConnection の送信・pong を検証する。
final class Receiver {
    let fd: Int32
    let port: UInt16
    private let stopped = DispatchSemaphore(value: 0)
    init(p2p: Bool) {
        let socketFD = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        fd = socketFD
        precondition(socketFD >= 0)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        precondition(withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(socketFD, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        } == 0)
        var size = socklen_t(MemoryLayout<sockaddr_in>.size)
        precondition(withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(socketFD, $0, &size) }
        } == 0)
        port = UInt16(bigEndian: address.sin_port)
        var timeout = timeval(tv_sec: 0, tv_usec: 100_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        DispatchQueue.global().async { [self] in
            var buffer = [UInt8](repeating: 0, count: 512)
            while stopped.wait(timeout: .now()) != .success {
                var from = sockaddr_storage()
                var size = socklen_t(MemoryLayout<sockaddr_storage>.size)
                let n = withUnsafeMutablePointer(to: &from) { address in
                    address.withMemoryRebound(to: sockaddr.self, capacity: 1) { pointer in
                        buffer.withUnsafeMutableBytes { recvfrom(socketFD, $0.baseAddress, $0.count, 0, pointer, &size) }
                    }
                }
                guard n > 0 else { continue }
                let bytes = Data(buffer.prefix(n))
                if p2p {
                    let message = try! P2PMessage.decode(bytes).get()
                    if message.kind == .ping {
                        let reply = P2PMessage.pong(session: message.session, echoing: message.sequence).encoded()
                        withUnsafePointer(to: &from) { address in
                            address.withMemoryRebound(to: sockaddr.self, capacity: 1) { pointer in
                                reply.withUnsafeBytes { _ = sendto(socketFD, $0.baseAddress, $0.count, 0, pointer, size) }
                            }
                        }
                    } else { precondition(message.body == Data("30,45,330,45".utf8)) }
                } else { precondition(bytes == Data("30,45,330,45".utf8)) }
            }
            Darwin.close(socketFD)
        }
    }
    func stop() { stopped.signal() }
}

@main
struct HotPathHarness {
    static func main() {
        let frames = Int(CommandLine.arguments.dropFirst().first ?? "240")!
        precondition(frames > 0)
        // main queue を空け、Network.framework の callbacks を実行する。
        DispatchQueue.global(qos: .userInteractive).async { run(frames: frames); exit(0) }
        dispatchMain()
    }

    static func measure(_ name: String, frames: Int, work: (Int) -> Void) {
        for i in 0..<30 { work(i) }
        usleep(250_000) // warmup の expiry / Network callback を待つ。
        let wall = HostMonotonicClock.now()
        let cpu = cpuSeconds()
        let allocations = allocationCount()
        for i in 0..<frames {
            autoreleasepool { work(i) }
            let remaining = wall + Double(i + 1) / 60 - HostMonotonicClock.now()
            if remaining > 0 { usleep(useconds_t(remaining * 1_000_000)) }
        }
        let count = allocationCount() - allocations
        let used = (cpuSeconds() - cpu) * 1_000_000 / Double(frames)
        print(String(format: "%@ frames=%d fps=60 malloc/frame=%.2f processCPU_us/frame=%.2f", name, frames, Double(count) / Double(frames), used))
    }

    static func run(frames: Int) {
        allocationStart()
        let endpoints = (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20))
        let detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = [(.red, endpoints), (.blue, endpoints)]
        let processor = FrameProcessor()
        processor.onResult = { results, _, _, _, _, _ in precondition(results.count == 0 || results.count == 2) }
        measure("frame-results+expiry (extracted)", frames: frames) { _ in
            processor.processDetectedForTesting(detected, at: HostMonotonicClock.now(), dimensions: (640, 480))
        }
        processor.onResult = nil
        var pixelBuffer: CVPixelBuffer?
        precondition(CVPixelBufferCreate(kCFAllocatorDefault, 640, 480, kCVPixelFormatType_32BGRA, nil, &pixelBuffer) == kCVReturnSuccess)
        var format: CMVideoFormatDescription?
        precondition(CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixelBuffer!, formatDescriptionOut: &format) == noErr)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60), presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        precondition(CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixelBuffer!, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample) == noErr)
        let frameDone = DispatchSemaphore(value: 0)
        processor.onResult = { results, _, _, _, _, _ in if results.contains(where: { $0.isFresh }) { frameDone.signal() } }
        measure("frame-mailbox+results (detector stub)", frames: frames) { _ in
            processor.submit(sample!)
            precondition(frameDone.wait(timeout: .now() + 5) == .success)
        }
        processor.queue.sync { processor.onResult = nil }
        let ui = CameraUIPendingState()
        ui.setAcceptance(running: true, processor: 0, lifecycle: 0)
        let results = detected.map { DetectedSaber(endpoints: $0.1!, color: $0.0, isFresh: true) }
        measure("ui-pending (extracted)", frames: frames) { i in
            ui.cameraFrame(at: HostMonotonicClock.now())
            ui.frame(results, width: 640, height: 480, generation: 0, lifecycle: 0,
                     redEpoch: nil, blueEpoch: nil, trace: nil,
                     redEnqueuedAt: nil, blueEnqueuedAt: nil, requestMs: 0)
            ui.completed(port: 5005, generation: 0, result: .success(HostMonotonicClock.now()), processingStart: HostMonotonicClock.now())
            if i % 4 == 0 { precondition(ui.takeSnapshot() != nil) }
        }
        measure("payload+ASCII", frames: frames) { _ in
            let text = payload(for: endpoints, source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
            precondition(text.data(using: .ascii) == Data("30,45,330,45".utf8))
        }
        measure("P2P-encode", frames: frames) { i in
            let text = payload(for: endpoints, source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
            let message = P2PMessage.coordinates(text, color: .red, session: 42, sequence: UInt64(i))!
            precondition(message.encoded().count == PhoneSaberP2P.headerSize + text.utf8.count)
        }
        let probe = LANLivenessProbe()
        probe.recordReplyForTesting("PHONESABER_UNITY 1")
        measure("LAN-probe-read", frames: frames) { _ in _ = probe.isAlive }
        let lanReceiver = Receiver(p2p: false)
        let sender = UDPSender()
        sender.configure(host: "127.0.0.1", ports: [Int(lanReceiver.port)])
        let p2pReceiver = Receiver(p2p: true)
        let p2p = P2PSender(endpointOverride: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: p2pReceiver.port)!))
        p2p.start()
        let readyDeadline = HostMonotonicClock.now() + 5
        while !p2p.isUsable && HostMonotonicClock.now() < readyDeadline { usleep(10_000) }
        precondition(p2p.isUsable)
        let delivery = CoordinateDelivery(sender: sender, p2pSender: p2p, lanProbe: probe)
        var settings = CoordinateDelivery.Settings()
        settings.running = true
        settings.lanConfigured = true
        delivery.update(settings)
        measure("LAN-route+NW-send", frames: frames) { _ in
            let done = DispatchSemaphore(value: 0)
            let text = payload(for: endpoints, source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
            delivery.route(text, port: Int(lanReceiver.port), settings: settings, onSendStarted: nil,
                           completion: { result in if case .failure = result { fatalError("LAN failed") }; done.signal() },
                           noRoute: { fatalError("no LAN") })
            precondition(done.wait(timeout: .now() + 5) == .success)
        }
        settings.lanConfigured = false
        settings.p2pEnabled = true
        delivery.update(settings)
        measure("P2P-route+NW-send", frames: frames) { _ in
            let done = DispatchSemaphore(value: 0)
            let text = payload(for: endpoints, source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
            delivery.route(text, port: 5005, settings: settings, onSendStarted: nil,
                           completion: { result in if case .failure = result { fatalError("P2P failed") }; done.signal() },
                           noRoute: { fatalError("no P2P") })
            precondition(done.wait(timeout: .now() + 5) == .success)
        }
        p2p.stop(); sender.stop(); lanReceiver.stop(); p2pReceiver.stop()
    }
}
