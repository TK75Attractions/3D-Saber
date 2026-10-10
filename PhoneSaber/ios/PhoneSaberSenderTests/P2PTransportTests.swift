#if !PIPELINE_ROUTING_STANDALONE
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

    func testLivenessNeedsConsecutivePongsAfterGoingStale() {
        var monitor = P2PLivenessMonitor(timing: .init(staleAfter: 1.5, reconnectAfter: 4, connectTimeout: 4,
                                                       recoveryPongs: 3, recoveryMaxGap: 0.5))
        monitor.connectionStarted(at: 0)
        monitor.pongReceived(at: 0.1)
        XCTAssertTrue(monitor.isUsable(at: 0.2), "the first link of a run is usable on its first pong")
        XCTAssertFalse(monitor.isUsable(at: 1.7), "stale")
        monitor.pongReceived(at: 1.7)
        XCTAssertFalse(monitor.isUsable(at: 1.7), "one pong after going stale is not enough")
        monitor.pongReceived(at: 1.95)
        XCTAssertFalse(monitor.isUsable(at: 1.95))
        monitor.pongReceived(at: 2.2)
        XCTAssertTrue(monitor.isUsable(at: 2.2), "three consecutive pongs")

        // At the stale edge (a pong every 1.6 s) the link never flips back to P2P.
        var now = 2.2
        for _ in 0..<5 {
            now += 1.6
            monitor.pongReceived(at: now)
            XCTAssertFalse(monitor.isUsable(at: now), "t=\(now)")
        }
        // Pongs too far apart do not count as consecutive.
        for _ in 0..<4 {
            now += 0.6
            monitor.pongReceived(at: now)
            XCTAssertFalse(monitor.isUsable(at: now), "t=\(now)")
        }
        for _ in 0..<2 { now += 0.25; monitor.pongReceived(at: now) }
        XCTAssertTrue(monitor.isUsable(at: now), "healthy again")

        // A replacement connection must earn usability too.
        monitor.connectionEnded()
        XCTAssertFalse(monitor.isUsable(at: now))
        XCTAssertFalse(monitor.shouldReconnect(at: now + 100), "no connection, nothing to reconnect")
        monitor.connectionStarted(at: now)
        monitor.pongReceived(at: now + 0.05)
        XCTAssertFalse(monitor.isUsable(at: now + 0.05))
        monitor.pongReceived(at: now + 0.3)
        monitor.pongReceived(at: now + 0.55)
        XCTAssertTrue(monitor.isUsable(at: now + 0.55))

        // Switching P2P off and on starts over: one pong is enough again.
        monitor.reset()
        monitor.connectionStarted(at: 50)
        monitor.pongReceived(at: 50.1)
        XCTAssertTrue(monitor.isUsable(at: 50.1))
    }

    func testReconnectBackoffDoublesUpToFourSecondsAndResets() {
        var backoff = P2PReconnectBackoff(initial: 0.25, maximum: 4)
        XCTAssertTrue(backoff.mayConnect(at: 0))
        let delays = (0..<7).map { _ in backoff.connectionLost(at: 10) }
        XCTAssertEqual(delays, [0.25, 0.5, 1, 2, 4, 4, 4])
        XCTAssertFalse(backoff.mayConnect(at: 13.9))
        XCTAssertTrue(backoff.mayConnect(at: 14))
        backoff.reset()
        XCTAssertTrue(backoff.mayConnect(at: 10))
        XCTAssertEqual(backoff.connectionLost(at: 20), 0.25, "first pong resets the delay")
    }

    // MARK: Service choice, interface and browse errors

    func testStationFiltersLANP2PAndDiagnosticsSelection() {
        let a = "Phone Saber Unity P2P (Mac Z) A"
        let b = "Phone Saber Unity P2P (Mac A) B"
        let legacy = "Phone Saber Unity P2P (Old Mac)"
        XCTAssertTrue(PhoneSaberStation.matches(service: "Phone Saber Unity A", station: "A"))
        for name in ["Phone Saber Unity", "Phone Saber Unity B", "Phone Saber Unity AA", "Phone Saber UnityA"] {
            XCTAssertFalse(PhoneSaberStation.matches(service: name, station: "A"))
            XCTAssertTrue(PhoneSaberStation.matches(service: name, station: ""))
        }
        var selection = P2PServiceSelection()
        XCTAssertEqual(selection.choose(from: [b, legacy, a], station: "A"), a)
        XCTAssertNil(selection.choose(from: [b, legacy], station: "A"))
        XCTAssertEqual(selection.choose(from: [a, b], station: "B"), b, "old lock cannot bypass station")
        XCTAssertNil(P2PDiagnosticsServicePicker.pick(from: [b, legacy], preferred: a, final: true, station: "A"))
        XCTAssertEqual(P2PDiagnosticsServicePicker.pick(from: [a, b], preferred: b, final: true, station: "A"), a)
        XCTAssertNil(P2PDiagnosticsServicePicker.pick(from: [a, b], preferred: b, final: false, station: "A"))
        XCTAssertEqual(P2PDiagnosticsServicePicker.pick(from: [a, b], preferred: b, final: false), b)
    }

    @MainActor
    func testStationChangeClearsAutomaticHostAndPreservesManualHost() {
        let model = CameraViewModel(p2pEnabled: false, idleTimerUpdater: { _ in })
        let saved = UserDefaults.standard.object(forKey: PhoneSaberStation.preferenceKey)
        defer {
            UserDefaults.standard.set(saved, forKey: PhoneSaberStation.preferenceKey)
            P2PPreferredMac.configure(station: "", lanHost: "", manual: false)
        }
        model.applyBonjourForTesting(host: "192.168.1.1", serviceName: "Phone Saber Unity")
        model.setStation("A")
        XCTAssertEqual(model.host, "")
        model.applyBonjourForTesting(host: "192.168.1.2", serviceName: "Phone Saber Unity B")
        XCTAssertEqual(model.host, "")
        model.applyBonjourForTesting(host: "192.168.1.3", serviceName: "Phone Saber Unity A")
        XCTAssertEqual(model.host, "192.168.1.3")
        model.setManualHost("192.168.1.9")
        model.setStation("B")
        model.applyBonjourForTesting(host: "192.168.1.2", serviceName: "Phone Saber Unity B")
        XCTAssertEqual(model.host, "192.168.1.9")
        XCTAssertEqual(model.transportLabel, "Manual IP")
    }

    func testServiceSelectionKeepsTheFirstChosenMacWhileItIsAdvertised() {
        var selection = P2PServiceSelection()
        XCTAssertNil(selection.choose(from: []))
        XCTAssertEqual(selection.choose(from: ["Saber Mac B"]), "Saber Mac B")
        XCTAssertEqual(selection.choose(from: ["Saber Mac A", "Saber Mac B"]), "Saber Mac B",
                       "a second Mac that sorts first never steals the link")
        XCTAssertNil(selection.choose(from: []), "nothing advertised for a moment")
        XCTAssertEqual(selection.choose(from: ["Saber Mac A", "Saber Mac B"]), "Saber Mac B", "lock kept")
        XCTAssertEqual(selection.choose(from: ["Saber Mac C", "Saber Mac A"]), "Saber Mac A",
                       "switches only when the locked Mac disappears; then the first name")
        XCTAssertEqual(selection.choose(from: ["Saber Mac A", "Saber Mac B"]), "Saber Mac A")
        selection.reset()
        XCTAssertEqual(selection.choose(from: ["Saber Mac B", "Saber Mac A"]), "Saber Mac A")

        XCTAssertEqual(P2PLinkState.connected(service: "Saber Mac", interface: "awdl0").label,
                       "P2P Connected (awdl0 · Saber Mac)")
    }

    func testDiagnosticsUploadsGoToTheMacTheCoordinateLinkLockedOnto() {
        let both = ["Saber Mac A", "Saber Mac B"]
        XCTAssertEqual(P2PDiagnosticsServicePicker.pick(from: both, preferred: "Saber Mac B", final: false),
                       "Saber Mac B", "not the name that sorts first")
        XCTAssertNil(P2PDiagnosticsServicePicker.pick(from: ["Saber Mac A"], preferred: "Saber Mac B", final: false),
                     "waits while the preferred Mac may still resolve")
        XCTAssertEqual(P2PDiagnosticsServicePicker.pick(from: ["Saber Mac A"], preferred: "Saber Mac B", final: true),
                       "Saber Mac A", "after discovery, any relay beats LAN-only")
        XCTAssertEqual(P2PDiagnosticsServicePicker.pick(from: ["Saber Mac B", "Saber Mac A"], preferred: nil,
                                                        final: false), "Saber Mac A", "no link: first name as before")
        XCTAssertNil(P2PDiagnosticsServicePicker.pick(from: [], preferred: nil, final: true))
    }

    func testInterfaceLabelPrefersTheInterfaceTheLinkIsScopedTo() {
        // AWDL and infrastructure Wi-Fi are both `.wifi`: the scope decides.
        XCTAssertEqual(P2PInterfaceLabel.pick(scoped: [nil, "awdl0"],
                                              available: [("en0", true), ("awdl0", true)]), "awdl0")
        XCTAssertEqual(P2PInterfaceLabel.pick(scoped: [nil, nil],
                                              available: [("lo0", false), ("en0", true)]), "en0")
        XCTAssertEqual(P2PInterfaceLabel.pick(scoped: [""], available: [("en0", false)]), "en0")
        XCTAssertEqual(P2PInterfaceLabel.pick(scoped: [], available: []), "?")
    }

    func testBrowseErrorsExplainTheMissingLocalNetworkPermission() {
        let denied = NWError.dns(DNSServiceErrorType(-65570))
        XCTAssertEqual(P2PBrowseErrorText.describe(denied, waiting: true),
                       "ローカルネットワークの許可が必要: 設定 > PhoneSaberSender")
        XCTAssertEqual(P2PBrowseErrorText.describe(denied, waiting: false), P2PBrowseErrorText.localNetworkDenied)
        XCTAssertEqual(P2PLinkState.failed(reason: P2PBrowseErrorText.localNetworkDenied).label,
                       "P2P Failed (ローカルネットワークの許可が必要: 設定 > PhoneSaberSender)")
        XCTAssertTrue(P2PBrowseErrorText.describe(.posix(.ENETDOWN), waiting: true).hasPrefix("browse waiting: "))
        XCTAssertTrue(P2PBrowseErrorText.describe(.dns(-65537), waiting: false).hasPrefix("browse: "))
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
        timing.reconnectInitialDelay = 0.05
        timing.reconnectMaximumDelay = 0.4
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

    func testCoordinateHookCompletesInlineAndFromAnotherQueue() async throws {
        for background in [false, true] {
            let bridge = try FakeBridge()
            defer { bridge.stop() }
            let port = try await bridge.ready()
            let sender = P2PSender(timing: fastTiming(),
                                   endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                                   coordinateSendHook: { _, completion in
                                       if background { DispatchQueue.global().async { completion(nil) } }
                                       else { completion(nil) }
                                   })
            defer { sender.stop() }
            sender.start()
            let connected = await waitFor { sender.isUsable }
            XCTAssertTrue(connected)
            let delivered = expectation(description: "hook completed background=\(background)")
            sender.send("1,2,3,4", to: 5005) { result in
                if case .success = result { delivered.fulfill() }
                else { XCTFail("hook completion failed") }
            }
            await fulfillment(of: [delivered], timeout: 2)
            XCTAssertEqual(sender.stalledSendCountForTesting, 0)
        }
    }

    func testAStuckCoordinateSendDropsTheLinkAfterTheWatchdog() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let stuck = LockedBox(true)
        let datagrams = LockedBox(0)
        var timing = fastTiming()
        timing.sendWatchdogTimeout = 0.15
        let sender = P2PSender(timing: timing, endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                               coordinateSendHook: { _, completion in
                                   datagrams.mutate { $0 += 1 }
                                   if !stuck.value { completion(nil) }  // stuck: .contentProcessed never fires
                               })
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected)
        XCTAssertEqual(sender.connectCountForTesting, 1)

        sender.send("1,2,3,4", to: 5005) { _ in XCTFail("a stuck send never completes") }
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("the waiting coordinate is dropped with the link") }
        let stalled = await waitFor { sender.stalledSendCountForTesting == 1 }
        XCTAssertTrue(stalled, "watchdog fires although pings are still answered")
        XCTAssertEqual(datagrams.value, 1, "only the in-flight coordinate reached the link")
        let rebuilt = await waitFor { sender.connectCountForTesting >= 2 }
        XCTAssertTrue(rebuilt, "the stuck link is rebuilt after the backoff")

        stuck.mutate { $0 = false }
        let recovered = await waitFor { sender.isUsable }
        XCTAssertTrue(recovered)
        let delivered = expectation(description: "delivered")
        sender.send("9,9,9,9", to: 5006) { result in
            if case .success = result { delivered.fulfill() }
        }
        await fulfillment(of: [delivered], timeout: 2)
        XCTAssertEqual(sender.stalledSendCountForTesting, 1)
    }

    func testForegroundRecoveryRebuildsAStaleLinkAndOnlyPingsALiveOne() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let now = LockedBox<TimeInterval>(100)
        let sender = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                               clock: { now.value })
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected)

        sender.recoverIfNeeded()
        XCTAssertEqual(sender.connectCountForTesting, 1, "a link with a recent pong is kept")

        now.mutate { $0 += 30 }  // suspended in the background: no pong for 30 s
        sender.recoverIfNeeded()
        XCTAssertEqual(sender.connectCountForTesting, 2, "rebuilt at once, without waiting for the backoff")
        let recovered = await waitFor { sender.isUsable }
        XCTAssertTrue(recovered, "fresh pongs make the rebuilt link usable")
        XCTAssertEqual(sender.connectCountForTesting, 2)
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

    // MARK: Triage bundle upload over P2P

    func testOnlyRecordingsOfAboutOneSecondOrMoreAreAutoTransferred() {
        XCTAssertFalse(DebugRecordingLimits.shouldAutoTransfer(recordedFrames: 0))
        XCTAssertFalse(DebugRecordingLimits.shouldAutoTransfer(recordedFrames: 1))
        XCTAssertFalse(DebugRecordingLimits.shouldAutoTransfer(recordedFrames: 29))
        XCTAssertTrue(DebugRecordingLimits.shouldAutoTransfer(recordedFrames: 30))
        XCTAssertTrue(DebugRecordingLimits.shouldAutoTransfer(recordedFrames: 2958))
    }

    func testTestRunsNeverUploadTriageBundlesToARealReceiver() {
        let key = DebugBundleTransfer.preferenceKey
        let old = UserDefaults.standard.object(forKey: key)
        defer { UserDefaults.standard.set(old, forKey: key) }
        UserDefaults.standard.set(true, forKey: key)
        XCTAssertFalse(DebugBundleTransfer.automaticTransferEnabled)
    }


    func testBundleUploaderSendsTheHTTPRequestAndReadsTheStatus() async throws {
        let receiver = try FakeHTTPReceiver(status: 201)
        defer { receiver.stop() }
        let port = try await receiver.ready()
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("p2p-upload-\(UUID()).psbt")
        let payload = Data((0..<(700 * 1024)).map { UInt8($0 % 251) })
        try payload.write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let uploader = P2PBundleUploader(endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let outcome = await withCheckedContinuation { continuation in
            uploader.upload(fileURL: file) { continuation.resume(returning: $0) }
        }
        guard case .uploaded = outcome else { return XCTFail("\(outcome)") }
        let request = receiver.request
        XCTAssertTrue(request.starts(with: Data("POST /v1/bundle HTTP/1.1\r\n".utf8)))
        XCTAssertNotNil(request.range(of: Data("Content-Length: \(payload.count)\r\n".utf8)))
        let headerEnd = try XCTUnwrap(request.range(of: Data("\r\n\r\n".utf8)))
        XCTAssertEqual(request[headerEnd.upperBound...], payload[...], "body unchanged")
    }

    func testBundleUploaderReportsHTTPErrorsAndMissingReceivers() async throws {
        let receiver = try FakeHTTPReceiver(status: 413)
        defer { receiver.stop() }
        let port = try await receiver.ready()
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("p2p-upload-\(UUID()).psbt")
        try Data("x".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let rejected = await withCheckedContinuation { continuation in
            P2PBundleUploader(endpointOverride: .hostPort(host: "127.0.0.1", port: port))
                .upload(fileURL: file) { continuation.resume(returning: $0) }
        }
        guard case .failed = rejected else { return XCTFail("\(rejected)") }
        // No service of this (test-only) type exists: fall through to LAN quickly.
        let started = Date()
        let missing = await withCheckedContinuation { continuation in
            P2PBundleUploader(serviceType: "_psbt-none._tcp", discoveryTimeout: 0.3)
                .upload(fileURL: file) { continuation.resume(returning: $0) }
        }
        guard case .notFound = missing else { return XCTFail("\(missing)") }
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
        XCTAssertEqual(P2PBundleUploader.statusCode(Data("HTTP/1.0 201 Created\r\n\r\n".utf8)), 201)
        XCTAssertNil(P2PBundleUploader.statusCode(Data("garbage".utf8)))
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
        viewModel.startForTesting(manual: false)
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
        let labelledLAN = await waitUntilMain { viewModel.transportLabel == "LAN Connected" }
        XCTAssertTrue(labelledLAN, viewModel.transportLabel)
    }

    @MainActor
    func testManualIPWinsEvenWhenP2PIsUsable() async {
        let packets = LockedBox<[(String, Int)]>([])
        let lan = UDPSender { text, port, completion in
            packets.mutate { $0.append((text, port)) }
            completion(.success(1))
        }
        let p2p = P2PSender()
        p2p.overrideUsabilityForTesting(true)
        let model = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: false, idleTimerUpdater: { _ in })
        defer { model.stop(); p2p.stop(); lan.stop() }
        model.startForTesting()
        let saved = UserDefaults.standard.object(forKey: "PhoneSaber.p2pEnabled")
        defer { UserDefaults.standard.set(saved, forKey: "PhoneSaber.p2pEnabled") }
        model.setP2PEnabled(true)
        model.processDetectedForTesting([(.red, (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20)))],
                                        at: 1, dimensions: (640, 480))
        let sent = await waitFor { packets.value.contains { $0.1 == 5005 } }
        XCTAssertTrue(sent)
        XCTAssertFalse(model.transportLabel.hasPrefix("P2P"))
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

    @MainActor
    func testLANNeverDeliversACoordinateQueuedBeforeP2PReturned() async throws {
        let bridge = try FakeBridge()
        bridge.answering = false
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
        viewModel.startForTesting(manual: false)
        lan.simulateConnectionStateForTesting(port: 5006, state: .waiting)

        // P2P down, LAN BLUE port not ready: the coordinate waits in UDPSender.
        let dimensions = (width: 640, height: 480)
        viewModel.processDetectedForTesting([(.blue, (PixelPoint(x: 30, y: 40), PixelPoint(x: 130, y: 40)))],
                                            at: 1, dimensions: dimensions)
        let queued = await waitFor { lan.pendingCountForTesting == 1 }
        XCTAssertTrue(queued)

        // P2P comes back and carries newer coordinates.
        bridge.answering = true
        let usable = await waitFor { p2p.isUsable }
        XCTAssertTrue(usable)
        viewModel.processDetectedForTesting([(.blue, (PixelPoint(x: 300, y: 40), PixelPoint(x: 400, y: 40)))],
                                            at: 2, dimensions: dimensions)
        let viaP2P = await waitFor { bridge.coordinates.contains { $0.color == .blue } }
        XCTAssertTrue(viaP2P)
        let discarded = await waitFor { lan.discardedForP2PCountForTesting >= 1 }
        XCTAssertTrue(discarded)

        // The LAN port becoming ready later must not deliver the old coordinate.
        lan.simulateConnectionStateForTesting(port: 5006, state: .ready)
        XCTAssertEqual(lan.pendingCountForTesting, 0)
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertTrue(lanPackets.value.isEmpty, "\(lanPackets.value)")
    }

    @MainActor
    func testFallbackWithoutLANCountsTheDroppedCoordinate() async throws {
        let bridge = try FakeBridge()
        bridge.answering = false
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
        viewModel.startForTesting(host: "", manual: false)
        XCTAssertFalse(viewModel.lanConfiguredForTesting)

        // Usable when route() checks, gone when the P2P queue sends: the fallback runs with no LAN.
        p2p.overrideUsabilityForTesting(true)
        viewModel.processDetectedForTesting([(.red, (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20)))],
                                            at: 1, dimensions: (640, 480))
        let counted = await waitUntilMain { viewModel.p2pNoRouteCountForTesting == 1 }
        XCTAssertTrue(counted, "dropped coordinate is counted like the no-route branch")
        XCTAssertEqual(viewModel.redCompletedCount + viewModel.redErrorCount, 0, "no completion, as without P2P")

        // Neither link usable at route() time: the existing no-route branch counts too.
        p2p.overrideUsabilityForTesting(false)
        viewModel.processDetectedForTesting([(.red, (PixelPoint(x: 12, y: 20), PixelPoint(x: 112, y: 20)))],
                                            at: 2, dimensions: (640, 480))
        let countedAgain = await waitUntilMain { viewModel.p2pNoRouteCountForTesting == 2 }
        XCTAssertTrue(countedAgain)
        XCTAssertTrue(lanPackets.value.isEmpty)
        XCTAssertTrue(bridge.coordinates.isEmpty)
        p2p.overrideUsabilityForTesting(nil)
    }

    @MainActor
    func testCoordinatesStartSendingWhileMainActorIsOccupied() {
        let started = DispatchSemaphore(value: 0)
        let packets = LockedBox<[(String, Int)]>([])
        let lan = UDPSender { text, port, completion in
            packets.mutate { $0.append((text, port)) }
            started.signal()
            completion(.success(ProcessInfo.processInfo.systemUptime))
        }
        let processor = FrameProcessor(expiryScheduler: nil)
        let model = CameraViewModel(processor: processor, sender: lan, p2pEnabled: false, idleTimerUpdater: { _ in })
        defer { model.stop(); lan.stop() }
        model.startForTesting()
        model.outputWidth = 1280
        model.outputHeight = 960
        model.mirrorX = true
        model.measurementMode = true
        let endpoints = (PixelPoint(x: 10, y: 20), PixelPoint(x: 110, y: 20))
        model.processDetectedForTesting([(.red, endpoints)], at: 1, dimensions: (640, 480))
        // MainActor を意図的に待機させても検出→送信は完了する。
        XCTAssertEqual(started.wait(timeout: .now() + 1), .success)
        let packet = packets.value.first
        XCTAssertEqual(packet?.1, 5005)
        XCTAssertTrue(packet?.0.hasPrefix("ts=") == true)
        let coordinates = packet?.0.split(separator: ";").last.map(String.init)
        XCTAssertEqual(coordinates, payload(for: endpoints, source: (640, 480), output: (1280, 960),
                                           mirrorX: true, mirrorY: false))
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

    func testLANLivenessProbeRequiresARecentUnityReply() {
        var now = 100.0
        let probe = LANLivenessProbe(clock: { now })
        XCTAssertFalse(probe.isAlive)
        probe.recordReplyForTesting("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac station=A")
        XCTAssertTrue(probe.isAlive)
        now += LANLivenessProbe.aliveWindow + 0.1
        XCTAssertFalse(probe.isAlive, "応答が途絶えたら P2P へ退避できる")
        probe.recordReplyForTesting("PHONESABER_DISCOVER 1")
        XCTAssertFalse(probe.isAlive, "Unity の応答以外では生存とみなさない")
        probe.recordReplyForTesting("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac")
        XCTAssertTrue(probe.isAlive)
        probe.stop()
        XCTAssertFalse(probe.isAlive)
    }

    // 受信待ちが error で終わった接続は作り直し、その後も Unity の応答で生存を保つ。
    func testLANLivenessProbeRebuildsTheConnectionAfterAReceiveError() async throws {
        let unity = try FakeUnityDiscovery()
        defer { unity.stop() }
        let port = try await unity.ready()
        let probe = LANLivenessProbe(port: port.rawValue)
        defer { probe.stop() }
        probe.start(host: "127.0.0.1")
        let alive = await waitFor { probe.isAlive }
        XCTAssertTrue(alive, "実ソケットで Unity の応答を受信できる")
        XCTAssertEqual(probe.createdConnectionCountForTesting, 1)

        probe.simulateReceiveErrorForTesting()
        let rebuilt = await waitFor { probe.createdConnectionCountForTesting == 2 }
        XCTAssertTrue(rebuilt, "次の tick で接続を作り直す")
        // 旧接続の応答は aliveWindow で失効する。新しい接続が応答を受け続けていれば生存のまま。
        try await Task.sleep(for: .seconds(LANLivenessProbe.aliveWindow + 0.5))
        XCTAssertTrue(probe.isAlive)
        XCTAssertGreaterThanOrEqual(unity.connectionCount, 2)

        probe.start(host: "127.0.0.2")
        XCTAssertEqual(probe.hostForTesting, "127.0.0.2")
        XCTAssertFalse(probe.isAlive, "host を変えたら旧 host の応答履歴は使わない")
    }

    // 画面上部の状態表示は経路選択と同じく、確認済み LAN を P2P より優先する。
    @MainActor
    func testNetworkStateLabelFollowsTheVerifiedLANLikeTheRoute() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let viewModel = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: true, idleTimerUpdater: { _ in })
        defer { viewModel.stop(); p2p.stop(); lan.stop() }
        viewModel.startForTesting(manual: false)
        let viaP2P = await waitUntilMain { viewModel.p2pState.isConnected && viewModel.senderStates.count == 2 }
        XCTAssertTrue(viaP2P)
        XCTAssertEqual(viewModel.networkStateLabel, "NETWORK READY (P2P)")
        viewModel.recordLANReplyForTesting()
        XCTAssertEqual(viewModel.networkStateLabel, "NETWORK READY")
        XCTAssertEqual(viewModel.transportLabel, "LAN Connected")
    }
}

/// UDP 5007 の探索に答える Unity の代役（loopback・空きポート）。
final class FakeUnityDiscovery: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "FakeUnityDiscovery")
    private let connections = LockedBox<[NWConnection]>([])

    init() throws {
        let parameters = NWParameters.udp
        parameters.requiredInterfaceType = .loopback
        listener = try NWListener(using: parameters, on: .any)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            self.connections.mutate { $0.append(connection) }
            connection.start(queue: self.queue)
            self.receive(connection)
        }
    }

    var connectionCount: Int { connections.value.count }

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
            if let data, String(data: data, encoding: .ascii) == LANLivenessProbe.request {
                connection.send(content: Data("\(LANLivenessProbe.replyPrefix) red=5005 blue=5006 name=Test".utf8),
                                completion: .idempotent)
            }
            if error == nil { self.receive(connection) }
        }
    }

    func stop() {
        queue.sync {
            listener.cancel()
            connections.value.forEach { $0.cancel() }
        }
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

/// Loopback TCP stand-in for the diagnostics receiver: records the request and
/// answers with a fixed HTTP status once the request is complete.
final class FakeHTTPReceiver: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "FakeHTTPReceiver")
    private let received = LockedBox(Data())
    private let status: Int

    init(status: Int) throws {
        self.status = status
        let parameters = NWParameters.tcp
        parameters.requiredInterfaceType = .loopback
        listener = try NWListener(using: parameters, on: .any)
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            connection.start(queue: self.queue)
            self.read(connection)
        }
    }

    var request: Data { received.value }

    func ready() async throws -> NWEndpoint.Port {
        try await withCheckedThrowingContinuation { continuation in
            let resumed = LockedBox(false)
            listener.stateUpdateHandler = { [weak self] state in
                guard !resumed.value else { return }
                switch state {
                case .ready: resumed.mutate { $0 = true }; continuation.resume(returning: self!.listener.port!)
                case .failed(let error): resumed.mutate { $0 = true }; continuation.resume(throwing: error)
                default: break
                }
            }
            listener.start(queue: queue)
        }
    }

    private func read(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, isComplete, _ in
            guard let self else { return }
            if let data { self.received.mutate { $0.append(data) } }
            if isComplete || self.requestComplete() {
                let reply = "HTTP/1.0 \(self.status) X\r\nContent-Length: 2\r\n\r\n{}"
                connection.send(content: Data(reply.utf8), contentContext: .finalMessage, isComplete: true,
                                completion: .contentProcessed { _ in connection.cancel() })
                return
            }
            self.read(connection)
        }
    }

    private func requestComplete() -> Bool {
        let request = received.value
        guard let headerEnd = request.range(of: Data("\r\n\r\n".utf8)),
              let header = String(data: request[..<headerEnd.lowerBound], encoding: .ascii),
              let line = header.split(separator: "\r\n").first(where: { $0.hasPrefix("Content-Length: ") }),
              let length = Int(line.dropFirst("Content-Length: ".count)) else { return false }
        return request.count - headerEnd.upperBound >= length
    }

    func stop() { queue.sync { listener.cancel() } }
}

#else
import XCTest
import Foundation
#endif

// このクラスは Mac の standalone XCTest でも同じ production 経路を検証する。
final class CoordinateDeliveryTests: XCTestCase {
    private func settings() -> CoordinateDelivery.Settings {
        var value = CoordinateDelivery.Settings()
        value.running = true
        value.processorGeneration = 7
        value.lifecycleGeneration = 3
        value.lanConfigured = true
        value.p2pEnabled = true
        return value
    }

    func testVerifiedLANAndManualIPKeepPendingLANCoordinates() {
        for manual in [false, true] {
            let sent = expectation(description: "BLUE via LAN")
            let lan = UDPSender { text, port, completion in
                XCTAssertEqual(text, "1,2,3,4")
                XCTAssertEqual(port, 5006)
                completion(.success(1))
                sent.fulfill()
            }
            let p2p = P2PSender()
            p2p.overrideUsabilityForTesting(true)
            let probe = LANLivenessProbe()
            if !manual { probe.recordReplyForTesting("PHONESABER_UNITY 1") }
            let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: probe)
            defer { lan.stop(); p2p.stop(); probe.stop() }
            var config = settings()
            config.manual = manual
            delivery.update(config)
            lan.configure(host: "127.0.0.1")
            lan.simulateConnectionStateForTesting(port: 5005, state: .waiting)
            lan.send("5,6,7,8", to: 5005, completion: { _ in XCTFail("RED still waiting") })
            XCTAssertEqual(lan.pendingCountForTesting, 1)
            delivery.route("1,2,3,4", port: 5006, settings: config, onSendStarted: nil,
                           completion: { _ in }, noRoute: { XCTFail("LAN exists") })
            wait(for: [sent], timeout: 2)
            XCTAssertEqual(lan.pendingCountForTesting, 1)
            XCTAssertEqual(lan.discardedForP2PCountForTesting, 0)
        }
    }

    func testP2PCheckThenLinkLossFallsBackToLANAndDiscardsOlderWaitingCoordinates() {
        let sent = expectation(description: "fallback via LAN")
        let lan = UDPSender { text, port, completion in
            XCTAssertEqual(text, "1,2,3,4")
            XCTAssertEqual(port, 5006)
            completion(.success(1))
            sent.fulfill()
        }
        let p2p = P2PSender()
        // route 時だけ usable。送信キューでは未接続なので fallback を実行する。
        p2p.overrideUsabilityForTesting(true)
        let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
        defer { lan.stop(); p2p.stop() }
        let config = settings()
        delivery.update(config)
        lan.configure(host: "127.0.0.1")
        lan.simulateConnectionStateForTesting(port: 5005, state: .waiting)
        lan.send("5,6,7,8", to: 5005, completion: { _ in XCTFail("obsolete RED") })
        XCTAssertEqual(lan.pendingCountForTesting, 1)
        delivery.route("1,2,3,4", port: 5006, settings: config, onSendStarted: nil,
                       completion: { _ in }, noRoute: { XCTFail("LAN exists") })
        wait(for: [sent], timeout: 2)
        XCTAssertEqual(lan.pendingCountForTesting, 0)
        XCTAssertEqual(lan.discardedForP2PCountForTesting, 1)
    }

    func testNoRouteAndFallbackWithoutLANAreCountedWithoutCompletion() {
        for usable in [false, true] {
            let dropped = expectation(description: "no route")
            let lan = UDPSender { _, _, _ in XCTFail("no LAN") }
            let p2p = P2PSender()
            p2p.overrideUsabilityForTesting(usable)
            defer { lan.stop(); p2p.stop() }
            let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
            var config = settings()
            config.lanConfigured = false
            delivery.update(config)
            delivery.route("1,2,3,4", port: 5005, settings: config, onSendStarted: nil,
                           completion: { _ in XCTFail("no completion") }, noRoute: { dropped.fulfill() })
            wait(for: [dropped], timeout: 2)
        }
    }

    func testP2PRejectsCancelledRequestBeforeFallbackOrCompletion() {
        let p2p = P2PSender()
        p2p.send("1,2,3,4", to: 5005,
                 onSendStarted: { _, _ in XCTFail("cancelled start") },
                 completion: { _ in XCTFail("cancelled completion") },
                 fallback: { XCTFail("cancelled fallback") }, isCurrent: { false })
        // stop の同期処理をバリアにし、先行する send が検査されたことを保証する。
        p2p.stop()
    }

    func testStoppedAndOldGenerationsAreRejectedBeforeRouting() {
        let lan = UDPSender { _, _, _ in XCTFail("obsolete frame") }
        let p2p = P2PSender()
        defer { lan.stop(); p2p.stop() }
        let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
        let old = settings()
        for change in 0..<3 {
            var current = old
            if change == 0 { current.running = false }
            if change == 1 { current.processorGeneration += 1 }
            if change == 2 { current.lifecycleGeneration += 1 }
            delivery.update(current)
            delivery.route("1,2,3,4", port: 5005, settings: old, onSendStarted: nil,
                           completion: { _ in XCTFail("obsolete completion") }, noRoute: { XCTFail("obsolete drop") })
        }
        XCTAssertNil(delivery.snapshot(generation: old.processorGeneration - 1))
        delivery.update(old)
        XCTAssertNotNil(delivery.snapshot(generation: old.processorGeneration))
        delivery.suspend()
        XCTAssertNil(delivery.snapshot(generation: old.processorGeneration))
    }

    func testPendingCoordinatesAreRevalidatedAfterRecoveryAndCompletion() {
        for recovery in [false, true] {
            let active = expectation(description: "first active")
            let completed = expectation(description: "first completed")
            let callback = PipelineLockedValue<((Result<TimeInterval, Error>) -> Void)?>(nil)
            let packets = PipelineLockedValue<[String]>([])
            let lan = UDPSender { text, _, completion in
                packets.mutate { $0.append(text) }
                callback.mutate { $0 = completion }
                active.fulfill()
            }
            let p2p = P2PSender()
            defer { lan.stop(); p2p.stop() }
            let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
            var config = settings()
            config.p2pEnabled = false
            delivery.update(config)
            lan.configure(host: "127.0.0.1")
            if recovery { lan.simulateConnectionStateForTesting(port: 5005, state: .waiting) }
            delivery.route("1,2,3,4", port: 5005, settings: config, onSendStarted: nil,
                           completion: { _ in completed.fulfill() }, noRoute: {})
            if !recovery { wait(for: [active], timeout: 2) }
            delivery.route("5,6,7,8", port: 5005, settings: config, onSendStarted: nil,
                           completion: { _ in XCTFail("stale completion") }, noRoute: {})
            XCTAssertEqual(lan.pendingCountForTesting, 1)
            config.processorGeneration += 1
            delivery.update(config)
            if recovery {
                lan.simulateConnectionStateForTesting(port: 5005, state: .ready)
                XCTAssertEqual(lan.pendingCountForTesting, 0)
                XCTAssertTrue(packets.value.isEmpty)
                // 未使用の expectation は正常に終了させる。
                active.fulfill(); completed.fulfill()
                wait(for: [active, completed], timeout: 2)
            } else {
                callback.value?(.success(1))
                wait(for: [completed], timeout: 2)
                XCTAssertEqual(lan.pendingCountForTesting, 0)
                XCTAssertEqual(packets.value, ["1,2,3,4"])
            }
        }
    }
}

private final class PipelineLockedValue<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value
    init(_ value: Value) { stored = value }
    var value: Value { lock.lock(); defer { lock.unlock() }; return stored }
    func mutate(_ body: (inout Value) -> Void) { lock.lock(); defer { lock.unlock() }; body(&stored) }
}
