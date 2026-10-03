import Network
@testable import PhoneSaberSender
import XCTest

/// Wire format, duplicate filtering, liveness and LAN fallback of the optional
/// peer-to-peer link. The Mac bridge itself is exercised by
/// Tools/test_phone_saber_p2p_bridge.py; here a loopback fake bridge answers.
final class P2PTransportTests: XCTestCase {
    // MARK: Wire format

    func testCoordinatesRoundTripWithTheUnchangedPayload() throws {
        for (text, color) in [("10,20,30,40", P2PColor.red), ("-5,0,1919,1079", .blue),
                              ("ts=1759400000.123456;1,2,3,4", .red), ("1.5,2.25,3,4", .blue)] {
            let message = try XCTUnwrap(P2PMessage.coordinates(text, color: color, session: 77, sequence: 9))
            let decoded = try P2PMessage.decode(message.encoded()).get()
            XCTAssertEqual(decoded, message)
            XCTAssertEqual(String(data: decoded.body, encoding: .ascii), text, "body is the payload byte for byte")
            XCTAssertEqual(decoded.color.port, color == .red ? 5005 : 5006)
        }
        XCTAssertEqual(P2PColor(port: 5005), .red)
        XCTAssertEqual(P2PColor(port: 5006), .blue)
        XCTAssertNil(P2PColor(port: 5007))
    }

    func testMalformedDatagramsAreRejected() {
        let valid = P2PMessage.coordinates("1,2,3,4", color: .red, session: 1, sequence: 1)!.encoded()
        func decode(_ data: Data) -> P2PMessage.DecodeError? {
            if case .failure(let error) = P2PMessage.decode(data) { return error }
            return nil
        }
        XCTAssertEqual(decode(Data("garbage".utf8)), .tooShort)
        var badMagic = valid; badMagic[0] = 0x58
        XCTAssertEqual(decode(badMagic), .badMagic)
        var badVersion = valid; badVersion[4] = 9
        XCTAssertEqual(decode(badVersion), .unsupportedVersion(9))
        var badKind = valid; badKind[5] = 42
        XCTAssertEqual(decode(badKind), .unknownKind(42))
        var badColor = valid; badColor[6] = 2
        XCTAssertEqual(decode(badColor), .unknownColor(2))
        let header = valid.prefix(PhoneSaberP2P.headerSize)
        for body in ["1,2,3", "1,2,3,4,5", "a,b,c,d", "nan,1,2,3", "inf,1,2,3", "1,2,3,4\n", "",
                     "ts=;1,2,3,4", "ts=x;1,2,3,4", "1,,3,4"] {
            XCTAssertEqual(decode(header + Data(body.utf8)), .invalidPayload, body)
        }
        XCTAssertEqual(decode(header + Data("1,2,3,é".utf8)), .invalidPayload)
        XCTAssertEqual(decode(header + Data(repeating: 0x31, count: 300)), .bodyTooLarge(300))
        var pingWithBody = P2PMessage.ping(session: 1, sequence: 1).encoded()
        pingWithBody.append(contentsOf: [0x31])
        XCTAssertEqual(decode(pingWithBody), .unexpectedBody)
        XCTAssertNil(P2PMessage.coordinates("1,2,3", color: .red, session: 1, sequence: 1))
    }

    // MARK: Duplicate / reorder filtering

    func testSequenceFilterDropsDuplicatesAndAcceptsANewSession() {
        var filter = P2PSequenceFilter(forgetAfter: 30)
        XCTAssertTrue(filter.accept(session: 1, color: .red, sequence: 5, now: 0))
        XCTAssertFalse(filter.accept(session: 1, color: .red, sequence: 5, now: 0.01), "duplicate")
        XCTAssertFalse(filter.accept(session: 1, color: .red, sequence: 4, now: 0.02), "reordered")
        XCTAssertTrue(filter.accept(session: 1, color: .blue, sequence: 4, now: 0.02), "colors are independent")
        XCTAssertTrue(filter.accept(session: 1, color: .red, sequence: 6, now: 0.03))
        XCTAssertTrue(filter.accept(session: 2, color: .red, sequence: 1, now: 0.04), "app restart: new session")
        XCTAssertTrue(filter.accept(session: 1, color: .red, sequence: 1, now: 40), "forgotten after a long silence")
    }

    // MARK: Liveness / fallback decision

    func testLivenessDecidesFallbackAndReconnect() {
        var monitor = P2PLivenessMonitor(timing: .init(staleAfter: 1.5, reconnectAfter: 4, connectTimeout: 3))
        XCTAssertFalse(monitor.isUsable(at: 0), "no connection")
        XCTAssertFalse(monitor.shouldReconnect(at: 100))
        monitor.connectionStarted(at: 10)
        XCTAssertFalse(monitor.isUsable(at: 10.5), "UDP ready alone never counts")
        XCTAssertFalse(monitor.shouldReconnect(at: 12.9))
        XCTAssertTrue(monitor.shouldReconnect(at: 13.1), "no first pong in time")
        monitor.pongReceived(at: 11)
        XCTAssertTrue(monitor.isUsable(at: 12.4))
        XCTAssertFalse(monitor.isUsable(at: 12.6), "stale: fall back to LAN")
        XCTAssertFalse(monitor.shouldReconnect(at: 14.9))
        XCTAssertTrue(monitor.shouldReconnect(at: 15.1))
        monitor.reset()
        XCTAssertFalse(monitor.isUsable(at: 11))
    }

    // MARK: Round-trip statistics

    func testRoundTripStatsSummariseLatencyAndLostPings() {
        var stats = P2PRoundTripStats(window: 10, expireAfter: 2)
        XCTAssertNil(stats.summary)
        for (index, ms) in [3.0, 5, 4, 20, 4].enumerated() {
            let sequence = UInt64(index + 1)
            stats.pingSent(sequence, at: Double(index))
            XCTAssertEqual(stats.pongReceived(sequence, at: Double(index) + ms / 1000)!, ms / 1000, accuracy: 1e-9)
        }
        XCTAssertNil(stats.pongReceived(99, at: 10), "unknown or duplicate pong is ignored")
        stats.pingSent(6, at: 10)
        stats.pingSent(7, at: 10.5)
        stats.expire(at: 12.2)                           // 6 is lost, 7 still pending
        let summary = try! XCTUnwrap(stats.summary)
        XCTAssertEqual(summary.samples, 5)
        XCTAssertEqual(summary.lastMs, 4, accuracy: 1e-6)
        XCTAssertEqual(summary.medianMs, 4, accuracy: 1e-6)
        XCTAssertEqual(summary.maxMs, 20, accuracy: 1e-6)
        XCTAssertEqual(summary.p95Ms, 20, accuracy: 1e-6)
        XCTAssertEqual(summary.lostPercent, 100.0 / 6, accuracy: 1e-6)
        XCTAssertNil(stats.pongReceived(6, at: 12.3), "a pong after expiry does not count")
        stats.reset()
        XCTAssertNil(stats.summary)
    }

    // MARK: Loopback link against a fake bridge

    private func fastTiming() -> P2PSender.Timing {
        var timing = P2PSender.Timing()
        timing.pingInterval = 0.05
        timing.liveness = .init(staleAfter: 0.3, reconnectAfter: 0.8, connectTimeout: 0.8)
        return timing
    }

    func testLinkCarriesRedAndBlueOnlyWhileTheBridgeAnswersAndRecoversAfterARestart() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let sender = P2PSender(timing: fastTiming(),
                               endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected, "pong makes the link usable")
        if case .connected = sender.stateForTesting {} else { XCTFail("state \(sender.stateForTesting)") }
        let measured = await waitFor { (sender.roundTripSummaryForTesting?.samples ?? 0) >= 3 }
        XCTAssertTrue(measured, "pongs produce round-trip samples")
        XCTAssertLessThan(sender.roundTripSummaryForTesting?.medianMs ?? .infinity, 100, "loopback is fast")

        sender.send("100,200,300,400", to: 5005) { _ in }
        sender.send("ts=1.000000;5,6,7,8", to: 5006) { _ in }
        let delivered = await waitFor { bridge.coordinates.count >= 2 }
        XCTAssertTrue(delivered)
        let byColor = Dictionary(bridge.coordinates.map { ($0.color, String(data: $0.body, encoding: .ascii)!) },
                                 uniquingKeysWith: { first, _ in first })
        XCTAssertEqual(byColor[.red], "100,200,300,400")
        XCTAssertEqual(byColor[.blue], "ts=1.000000;5,6,7,8")
        XCTAssertEqual(Set(bridge.coordinates.map(\.session)), [sender.sessionForTesting])

        // Mac bridge stops answering (receiver stopped): fall back, sends go to the fallback.
        bridge.answering = false
        let fellBack = await waitFor { !sender.isUsable }
        XCTAssertTrue(fellBack)
        let fallbackUsed = expectation(description: "fallback")
        sender.send("1,2,3,4", to: 5005, completion: { _ in XCTFail("no P2P completion when falling back") },
                    fallback: { fallbackUsed.fulfill() })
        await fulfillment(of: [fallbackUsed], timeout: 2)

        // Bridge answers again (receiver restarted): reconnect without restarting the sender.
        bridge.answering = true
        let recovered = await waitFor(timeout: 5) { sender.isUsable }
        XCTAssertTrue(recovered)
    }

    func testOnlyTheNewestCoordinateWaitsAndPortsOutsideRedBlueAreRejected() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let sender = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected)
        for index in 0..<200 { sender.send("\(index),0,1,1", to: 5005) { _ in } }
        let last = await waitFor { bridge.coordinates.last.flatMap { String(data: $0.body, encoding: .ascii) } == "199,0,1,1" }
        XCTAssertTrue(last, "the newest coordinate always arrives")
        XCTAssertLessThanOrEqual(bridge.coordinates.count, 200)
        let sequences = bridge.coordinates.map(\.sequence)
        XCTAssertEqual(sequences, sequences.sorted(), "never older after newer")

        let rejected = expectation(description: "rejected")
        sender.send("1,2,3,4", to: 9999) { result in
            if case .failure = result { rejected.fulfill() }
        }
        await fulfillment(of: [rejected], timeout: 2)
    }

    /// Opt-in (needs a bridge running on this Mac with Bonjour, forwarding to
    /// 47431/47432): `TEST_RUNNER_PHONESABER_P2P_DISCOVERY_TEST=1 xcodebuild test ...`.
    /// Proves Bonjour discovery of `_phonesaber-p2p._udp` with includePeerToPeer
    /// and the bridge's localhost forwarding, end to end.
    func testDiscoversARunningBridgeOverBonjourAndItForwardsToLocalhost() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["PHONESABER_P2P_DISCOVERY_TEST"] == "1",
                          "opt-in: start the bridge with --red-port 47431 --blue-port 47432 first")
        let unity = try LocalUDPReceiver(port: 47431)
        defer { unity.close() }
        let sender = P2PSender()
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor(timeout: 15) { sender.isUsable }
        XCTAssertTrue(connected, "state \(sender.stateForTesting)")
        sender.send("11,22,33,44", to: 5005) { _ in }
        XCTAssertEqual(unity.receive(timeout: 3), "11,22,33,44")
    }

    // MARK: CameraViewModel routing

    @MainActor
    func testViewModelPrefersP2PAndFallsBackToLANWithoutRestarting() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let lanPackets = LockedBox<[(String, Int)]>([])
        let lan = UDPSender { text, port, completion in
            lanPackets.mutate { $0.append((text, port)) }
            completion(.success(1))
        }
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let viewModel = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: true, idleTimerUpdater: { _ in })
        defer { viewModel.stop(); p2p.stop(); lan.stop() }
        viewModel.startForTesting()
        let ready = await waitFor { p2p.isUsable }
        XCTAssertTrue(ready)
        let labelled = await waitUntilMain { viewModel.transportLabel.hasPrefix("P2P Connected") }
        XCTAssertTrue(labelled, viewModel.transportLabel)

        let dimensions = (width: 640, height: 480)
        viewModel.processDetectedForTesting([(.red, (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20)))],
                                            at: 1, dimensions: dimensions)
        let viaP2P = await waitFor { !bridge.coordinates.isEmpty }
        XCTAssertTrue(viaP2P)
        XCTAssertTrue(lanPackets.value.isEmpty, "no duplicate over LAN while P2P is used")

        bridge.answering = false
        let fellBack = await waitFor { !p2p.isUsable }
        XCTAssertTrue(fellBack)
        viewModel.processDetectedForTesting([(.blue, (PixelPoint(x: 30, y: 40), PixelPoint(x: 130, y: 40)))],
                                            at: 2, dimensions: dimensions)
        let viaLAN = await waitFor { lanPackets.value.contains { $0.1 == 5006 } }
        XCTAssertTrue(viaLAN, "LAN carries coordinates while P2P is down, with no restart")
        let labelledLAN = await waitUntilMain { viewModel.transportLabel == "Manual IP" }
        XCTAssertTrue(labelledLAN, viewModel.transportLabel)
    }

    @MainActor
    func testP2PDisabledKeepsTheExistingLANPathUntouched() async {
        let lanPackets = LockedBox<[(String, Int)]>([])
        let lan = UDPSender { text, port, completion in
            lanPackets.mutate { $0.append((text, port)) }
            completion(.success(1))
        }
        let viewModel = CameraViewModel(sender: lan, p2pEnabled: false, idleTimerUpdater: { _ in })
        defer { viewModel.stop(); lan.stop() }
        viewModel.startForTesting()
        XCTAssertEqual(viewModel.p2pState, .disabled)
        viewModel.processDetectedForTesting([(.red, (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20)))],
                                            at: 1, dimensions: (640, 480))
        let sent = await waitFor { lanPackets.value.contains { $0.1 == 5005 } }
        XCTAssertTrue(sent)
    }

    // MARK: Helpers

    private func waitFor(timeout: TimeInterval = 3, _ condition: @escaping () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(10))
        }
        return condition()
    }

    @MainActor
    private func waitUntilMain(timeout: TimeInterval = 3, _ condition: @escaping @MainActor () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(10))
        }
        return condition()
    }
}

final class LockedBox<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value
    init(_ value: Value) { stored = value }
    var value: Value { lock.lock(); defer { lock.unlock() }; return stored }
    func mutate(_ change: (inout Value) -> Void) { lock.lock(); change(&stored); lock.unlock() }
}

/// Minimal stand-in for the Mac bridge on the loopback interface: answers pings
/// (while `answering`) and records coordinate datagrams.
final class FakeBridge: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "FakeBridge")
    private let state = LockedBox<(coordinates: [P2PMessage], answering: Bool)>(([], true))
    private var connections: [NWConnection] = []

    init() throws {
        let parameters = NWParameters.udp
        parameters.requiredInterfaceType = .loopback
        listener = try NWListener(using: parameters, on: .any)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            self.connections.append(connection)
            connection.start(queue: self.queue)
            self.receive(connection)
        }
    }

    var coordinates: [P2PMessage] { state.value.coordinates }
    var answering: Bool {
        get { state.value.answering }
        set { state.mutate { $0.answering = newValue } }
    }

    func ready() async throws -> NWEndpoint.Port {
        try await withCheckedThrowingContinuation { continuation in
            let resumed = LockedBox(false)
            listener.stateUpdateHandler = { [weak self] newState in
                guard !resumed.value else { return }
                switch newState {
                case .ready:
                    resumed.mutate { $0 = true }
                    continuation.resume(returning: self!.listener.port!)
                case .failed(let error):
                    resumed.mutate { $0 = true }
                    continuation.resume(throwing: error)
                default: break
                }
            }
            listener.start(queue: queue)
        }
    }

    private func receive(_ connection: NWConnection) {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, case .success(let message) = P2PMessage.decode(data) {
                switch message.kind {
                case .ping where self.answering:
                    connection.send(content: P2PMessage.pong(session: message.session, echoing: message.sequence).encoded(),
                                    completion: .idempotent)
                case .coordinates:
                    self.state.mutate { $0.coordinates.append(message) }
                default:
                    break
                }
            }
            if error == nil { self.receive(connection) }
        }
    }

    func stop() {
        queue.sync {
            listener.cancel()
            connections.forEach { $0.cancel() }
        }
    }
}

/// Blocking loopback UDP receiver standing in for Unity's InputPoint.
final class LocalUDPReceiver {
    private let fd: Int32

    init(port: UInt16) throws {
        fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard fd >= 0, bound == 0 else { throw POSIXError(.EADDRINUSE) }
    }

    func receive(timeout: TimeInterval) -> String? {
        var tv = timeval(tv_sec: Int(timeout), tv_usec: Int32((timeout - floor(timeout)) * 1_000_000))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var buffer = [UInt8](repeating: 0, count: 512)
        let count = recv(fd, &buffer, buffer.count, 0)
        return count > 0 ? String(decoding: buffer[0..<count], as: UTF8.self) : nil
    }

    func close() { Darwin.close(fd) }
}
