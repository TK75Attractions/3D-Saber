// Mac で iPhone 送信側の長時間運転(soak)を再現する。カメラ・無線・UI は対象外。
// 60fps で FrameProcessor(Debug Recording なしの通常経路)→ payload → CoordinateDelivery → UDPSender を回し、
// LANLivenessProbe も本番と同じ 0.5 秒周期で動かす。送信先は 127.0.0.1 の自動割当ポート(Unity の 5005/5006/5007 は使わない)。
// 10 秒ごとに常駐メモリ(resident / phys_footprint)、thread 数、fd 数、送信遅延などを CSV へ書く。
// ビルドと集計は PhoneSaber/tools/sender_soak.py。
#if SENDER_SOAK_HARNESS
import CoreGraphics
import CoreMedia
import CoreVideo
import Darwin
import Foundation
import ImageIO

@main
struct SenderSoakHarness {
    static func main() {
        let options: Options
        do { options = try Options(CommandLine.arguments) } catch {
            FileHandle.standardError.write(Data("\(error)\n".utf8))
            exit(2)
        }
        let soak = Soak(options: options)
        DispatchQueue.global(qos: .userInitiated).async { soak.run() }
        dispatchMain()
    }
}

struct Options {
    var seconds = 1800.0
    var sampleSeconds = 10.0
    var fps = 60.0
    var output = "sender-soak.csv"
    var fixture = ""
    // lan-reply: 偽 Unity が 5007 相当の探索に応答する。lan-silent: 応答先ポートが閉じている(Unity 未起動)。
    var scenario = "lan-reply"
    // frames: FrameProcessor を通す。direct: 認識なしで payload を直接送る。
    var source = "frames"

    struct UsageError: Error, CustomStringConvertible { let description: String }

    init(_ arguments: [String]) throws {
        var index = 1
        func value() throws -> String {
            index += 1
            guard index < arguments.count else { throw UsageError(description: "missing value for \(arguments[index - 1])") }
            return arguments[index]
        }
        while index < arguments.count {
            switch arguments[index] {
            case "--seconds": seconds = Double(try value()) ?? seconds
            case "--sample": sampleSeconds = Double(try value()) ?? sampleSeconds
            case "--fps": fps = Double(try value()) ?? fps
            case "--out": output = try value()
            case "--fixture": fixture = try value()
            case "--scenario": scenario = try value()
            case "--source": source = try value()
            default: throw UsageError(description: "unknown argument \(arguments[index])")
            }
            index += 1
        }
        guard ["lan-reply", "lan-silent"].contains(scenario) else { throw UsageError(description: "bad --scenario") }
        guard ["frames", "direct"].contains(source) else { throw UsageError(description: "bad --source") }
        if source == "frames" && fixture.isEmpty { throw UsageError(description: "--fixture is required for frames") }
    }
}

/// 送信ごとの時間を窓単位で集める(lock 付き。窓ごとに消去するので増え続けない)。
final class WindowRecorder {
    private let lock = NSLock()
    private var values: [Double] = []
    init() { values.reserveCapacity(4096) }
    func add(_ value: Double) { lock.lock(); values.append(value); lock.unlock() }
    func take() -> [Double] {
        lock.lock(); defer { lock.unlock() }
        let result = values
        values.removeAll(keepingCapacity: true)
        return result
    }
}

final class Counter {
    private let lock = NSLock()
    private var value = 0
    func increment() { lock.lock(); value += 1; lock.unlock() }
    var current: Int { lock.lock(); defer { lock.unlock() }; return value }
}

/// 座標の受信側(Unity の代わり)。受信数と、直前の route 呼び出しからの到着時間を記録する。
final class CoordinateReceiver {
    let fd: Int32
    let port: UInt16
    let received = Counter()
    let routeToReceive = WindowRecorder()
    private let lastRouteLock = NSLock()
    private var lastRouteAt = 0.0
    private var running = true

    init() {
        (fd, port) = bindLoopback()
        var timeout = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        let thread = Thread { [weak self] in self?.loop() }
        thread.name = "soak receiver \(port)"
        thread.start()
    }

    func routed(at time: TimeInterval) { lastRouteLock.lock(); lastRouteAt = time; lastRouteLock.unlock() }

    private func loop() {
        var buffer = [UInt8](repeating: 0, count: 512)
        while running {
            let count = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress, $0.count, 0) }
            guard count > 0 else { continue }
            let now = HostMonotonicClock.now()
            received.increment()
            lastRouteLock.lock(); let routedAt = lastRouteAt; lastRouteLock.unlock()
            if routedAt > 0 && now >= routedAt { routeToReceive.add((now - routedAt) * 1000) }
        }
    }
}

/// UDP 5007 の探索応答(PhoneSaberDiscoveryResponder と同じ文言)。
final class DiscoveryResponder {
    let fd: Int32
    let port: UInt16
    let replies = Counter()

    init() {
        (fd, port) = bindLoopback()
        let thread = Thread { [weak self] in self?.loop() }
        thread.name = "soak discovery"
        thread.start()
    }

    private func loop() {
        let reply = Array("\(LANLivenessProbe.replyPrefix) red=5005 blue=5006 name=soak".utf8)
        var buffer = [UInt8](repeating: 0, count: 128)
        while true {
            var address = sockaddr_in()
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let count = withUnsafeMutablePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                    buffer.withUnsafeMutableBytes { recvfrom(fd, $0.baseAddress, $0.count, 0, socketAddress, &length) }
                }
            }
            guard count > 0,
                  String(decoding: buffer[0..<count], as: UTF8.self).trimmingCharacters(in: .whitespaces)
                    == LANLivenessProbe.request else { continue }
            _ = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                    reply.withUnsafeBytes { sendto(fd, $0.baseAddress, $0.count, 0, socketAddress, length) }
                }
            }
            replies.increment()
        }
    }
}

func bindLoopback() -> (Int32, UInt16) {
    let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
    guard fd >= 0 else { fatalError("socket errno=\(errno)") }
    var address = sockaddr_in()
    address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
    address.sin_family = sa_family_t(AF_INET)
    address.sin_addr.s_addr = inet_addr("127.0.0.1")
    let bound = withUnsafePointer(to: &address) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
    }
    guard bound == 0 else { fatalError("bind errno=\(errno)") }
    var size = socklen_t(MemoryLayout<sockaddr_in>.size)
    let named = withUnsafeMutablePointer(to: &address) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &size) }
    }
    guard named == 0 else { fatalError("getsockname errno=\(errno)") }
    return (fd, UInt16(bigEndian: address.sin_port))
}

/// 一度 bind して閉じたポート。Unity が起動していない Mac と同じく ICMP port unreachable になる。
func closedLoopbackPort() -> UInt16 {
    let (fd, port) = bindLoopback()
    close(fd)
    return port
}

enum ProcessMetrics {
    static func memory() -> (resident: Double, footprint: Double) {
        var basic = mach_task_basic_info()
        var basicCount = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<natural_t>.size)
        let basicResult = withUnsafeMutablePointer(to: &basic) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(basicCount)) {
                task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &basicCount)
            }
        }
        var vm = task_vm_info_data_t()
        var vmCount = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let vmResult = withUnsafeMutablePointer(to: &vm) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(vmCount)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &vmCount)
            }
        }
        return (basicResult == KERN_SUCCESS ? Double(basic.resident_size) : -1,
                vmResult == KERN_SUCCESS ? Double(vm.phys_footprint) : -1)
    }

    static func threads() -> Double {
        var list: thread_act_array_t?
        var count: mach_msg_type_number_t = 0
        guard task_threads(mach_task_self_, &list, &count) == KERN_SUCCESS, let list else { return -1 }
        for index in 0..<Int(count) { mach_port_deallocate(mach_task_self_, list[index]) }
        vm_deallocate(mach_task_self_, vm_address_t(UInt(bitPattern: list)),
                      vm_size_t(Int(count) * MemoryLayout<thread_t>.stride))
        return Double(count)
    }

    /// /dev/fd は呼び出し process の fd 一覧。列挙のための1つを引く。
    static func openFiles() -> Double {
        guard let entries = try? FileManager.default.contentsOfDirectory(atPath: "/dev/fd") else { return -1 }
        return Double(entries.count - 1)
    }
}

func percentile(_ sorted: [Double], _ p: Double) -> Double {
    guard !sorted.isEmpty else { return -1 }
    return sorted[max(0, Int((Double(sorted.count) * p).rounded(.up)) - 1)]
}

final class Soak {
    let options: Options
    let sender = UDPSender()
    let lanProbe: LANLivenessProbe
    let delivery: CoordinateDelivery
    let processor = FrameProcessor()
    let red = CoordinateReceiver()
    let blue = CoordinateReceiver()
    let responder: DiscoveryResponder?
    let probePort: UInt16
    let routeToComplete = WindowRecorder()
    let queueWait = WindowRecorder()
    let routed = Counter(), completed = Counter(), failed = Counter(), noRoute = Counter()
    let results = Counter(), freshRed = Counter(), freshBlue = Counter()
    let probeAliveTicks = Counter(), ticks = Counter()
    var settings = CoordinateDelivery.Settings()
    private var buffers: [CMSampleBuffer] = []

    init(options: Options) {
        self.options = options
        if options.scenario == "lan-reply" {
            let responder = DiscoveryResponder()
            self.responder = responder
            probePort = responder.port
        } else {
            responder = nil
            probePort = closedLoopbackPort()
        }
        lanProbe = LANLivenessProbe(port: probePort)
        // P2P は起動しない(実 Mac の P2P bridge と接続しないため)。isUsable=false なので LAN 経路になる。
        delivery = CoordinateDelivery(sender: sender, p2pSender: P2PSender(), lanProbe: lanProbe)
    }

    func run() {
        sender.configure(host: "127.0.0.1", ports: [Int(red.port), Int(blue.port)])
        lanProbe.start(host: "127.0.0.1")
        settings.running = true
        settings.processorGeneration = processor.reset()
        settings.lanConfigured = true
        settings.p2pEnabled = true
        delivery.update(settings)
        if options.source == "frames" {
            buffers = (0..<8).map { _ in Self.sampleBuffer(fixture: options.fixture) }
            processor.onResult = { [weak self] detected, width, height, processingStart, generation, _ in
                self?.send(detected, width: width, height: height, generation: generation)
            }
        }
        let csvURL = URL(fileURLWithPath: options.output)
        FileManager.default.createFile(atPath: csvURL.path, contents: nil)
        guard let csv = try? FileHandle(forWritingTo: csvURL) else { fatalError("cannot open \(csvURL.path)") }
        csv.write(Data(("t_min,resident_bytes,footprint_bytes,threads,fds,frames_hz,fresh_red_hz,fresh_blue_hz," +
                        "routed_hz,completed_hz,received_red_hz,received_blue_hz,failed,no_route," +
                        "route_complete_ms_median,route_complete_ms_p95,route_complete_ms_max," +
                        "queue_wait_ms_median,queue_wait_ms_p95,route_receive_ms_median,route_receive_ms_p95," +
                        "route_receive_ms_max,probe_alive_ratio,probe_connections,probe_replies,udp_reconnects\n").utf8))

        let frameQueue = DispatchQueue(label: "soak.camera", qos: .userInteractive)
        let timer = DispatchSource.makeTimerSource(flags: .strict, queue: frameQueue)
        var frameIndex = 0
        timer.schedule(deadline: .now() + 0.5, repeating: 1.0 / options.fps, leeway: .milliseconds(1))
        timer.setEventHandler { [unowned self] in
            self.ticks.increment()
            if self.lanProbe.isAlive { self.probeAliveTicks.increment() }
            if self.options.source == "frames" {
                self.processor.submit(self.buffers[frameIndex % self.buffers.count])
            } else {
                let fixed = (PixelPoint(x: 100 + frameIndex % 40, y: 200), PixelPoint(x: 260, y: 420))
                self.send([DetectedSaber(endpoints: fixed, color: .red, isFresh: true),
                           DetectedSaber(endpoints: fixed, color: .blue, isFresh: true)],
                          width: 480, height: 640, generation: self.settings.processorGeneration)
            }
            frameIndex &+= 1
        }
        timer.resume()

        let started = HostMonotonicClock.now()
        var last = (time: started, results: 0, red: 0, blue: 0, routed: 0, completed: 0, rxRed: 0, rxBlue: 0, ticks: 0, alive: 0)
        var nextSample = started + options.sampleSeconds
        while true {
            let now = HostMonotonicClock.now()
            if now < nextSample { usleep(UInt32(min(0.2, nextSample - now) * 1_000_000)); continue }
            nextSample += options.sampleSeconds
            let dt = max(1e-3, now - last.time)
            let memory = ProcessMetrics.memory()
            let completeWindow = routeToComplete.take().sorted()
            let waitWindow = queueWait.take().sorted()
            let receiveWindow = (red.routeToReceive.take() + blue.routeToReceive.take()).sorted()
            let current = (time: now, results: results.current, red: freshRed.current, blue: freshBlue.current,
                           routed: routed.current, completed: completed.current, rxRed: red.received.current,
                           rxBlue: blue.received.current, ticks: ticks.current, alive: probeAliveTicks.current)
            let tickDelta = max(1, current.ticks - last.ticks)
            let reconnects = sender.reconnectCountForTesting(port: Int(red.port)) +
                sender.reconnectCountForTesting(port: Int(blue.port))
            let fields: [Double] = [
                (now - started) / 60, memory.resident, memory.footprint, ProcessMetrics.threads(), ProcessMetrics.openFiles(),
                Double(current.results - last.results) / dt, Double(current.red - last.red) / dt,
                Double(current.blue - last.blue) / dt, Double(current.routed - last.routed) / dt,
                Double(current.completed - last.completed) / dt, Double(current.rxRed - last.rxRed) / dt,
                Double(current.rxBlue - last.rxBlue) / dt, Double(failed.current), Double(noRoute.current),
                percentile(completeWindow, 0.5), percentile(completeWindow, 0.95), completeWindow.last ?? -1,
                percentile(waitWindow, 0.5), percentile(waitWindow, 0.95),
                percentile(receiveWindow, 0.5), percentile(receiveWindow, 0.95), receiveWindow.last ?? -1,
                Double(current.alive - last.alive) / Double(tickDelta), Double(lanProbe.createdConnectionCountForTesting),
                Double(responder?.replies.current ?? 0), Double(reconnects)
            ]
            csv.write(Data((fields.map { String(format: "%.6g", $0) }.joined(separator: ",") + "\n").utf8))
            print(String(format: "[SenderSoak] %@ %@ t=%.1fmin resident=%.1fMB footprint=%.1fMB threads=%.0f fds=%.0f " +
                         "routed=%.1fHz rx=%.1f/%.1fHz complete p95=%.3fms probe-alive=%.2f probe-conns=%.0f",
                         options.scenario, options.source, fields[0], memory.resident / 1_048_576, memory.footprint / 1_048_576,
                         fields[3], fields[4], fields[8], fields[10], fields[11], fields[15], fields[22], fields[23]))
            fflush(stdout)
            last = current
            if now - started >= options.seconds {
                timer.cancel()
                lanProbe.stop()
                sender.stop()
                try? csv.close()
                exit(0)
            }
        }
    }

    /// CameraViewModel.sendResults と同じ: 新しい検出だけを payload にして経路へ渡す。
    func send(_ detected: [DetectedSaber], width: Int, height: Int, generation: Int) {
        results.increment()
        guard let settings = delivery.snapshot(generation: generation) else { return }
        for result in detected where result.isFresh {
            let coordinates = payload(for: result.endpoints, source: (width, height),
                                      output: (settings.outputWidth, settings.outputHeight),
                                      mirrorX: settings.mirrorX, mirrorY: settings.mirrorY)
            let receiver = result.color == .red ? red : blue
            if result.color == .red { freshRed.increment() } else { freshBlue.increment() }
            let routedAt = HostMonotonicClock.now()
            receiver.routed(at: routedAt)
            routed.increment()
            delivery.route(coordinates, port: Int(receiver.port), settings: settings,
                           onSendStarted: { [queueWait] wait, _ in queueWait.add(wait * 1000) },
                           completion: { [routeToComplete, completed, failed] outcome in
                               switch outcome {
                               case .success:
                                   completed.increment()
                                   routeToComplete.add((HostMonotonicClock.now() - routedAt) * 1000)
                               case .failure: failed.increment()
                               }
                           },
                           noRoute: { [noRoute] in noRoute.increment() })
        }
    }

    /// fixture PNG(赤・青の剣が両方写る正解フレーム)を BGRA の CMSampleBuffer にする。
    static func sampleBuffer(fixture: String) -> CMSampleBuffer {
        guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: fixture) as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { fatalError("cannot read \(fixture)") }
        let width = image.width, height = image.height
        var pixelBuffer: CVPixelBuffer?
        guard CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA, nil, &pixelBuffer)
                == kCVReturnSuccess, let buffer = pixelBuffer else { fatalError("CVPixelBufferCreate") }
        CVPixelBufferLockBaseAddress(buffer, [])
        let context = CGContext(data: CVPixelBufferGetBaseAddress(buffer), width: width, height: height,
                                bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        context?.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        CVPixelBufferUnlockBaseAddress(buffer, [])
        var format: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer,
                                                     formatDescriptionOut: &format)
        var sample: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer,
                                                 formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
        return sample!
    }
}
#endif
