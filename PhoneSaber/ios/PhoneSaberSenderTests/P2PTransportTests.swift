import Network
#if !PIPELINE_ROUTING_STANDALONE
@testable import PhoneSaberSender
import XCTest

final class DebugBundleOutboxTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_000)

    func testFailureStaysPendingWithBackoffAndManualRetry() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "one", bundlePath: "bundle-one", now: now)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.state, .uploading)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true), "one upload at a time")
        outbox.finish(sessionID: "one", now: now, reason: "offline", attempts: 3)
        XCTAssertEqual(outbox.pendingCount, 1)
        XCTAssertEqual(outbox.latest?.state, .failed)
        XCTAssertEqual(outbox.latest?.attempts, 3)
        XCTAssertTrue(outbox.latest?.statusText.contains("自動で再試行") == true)
        XCTAssertNil(outbox.beginNext(now: now.addingTimeInterval(29), recording: false, automatic: true))
        XCTAssertEqual(outbox.beginNext(now: now.addingTimeInterval(30), recording: false, automatic: true)?.sessionID, "one")
        outbox.finish(sessionID: "one", now: now, reason: "offline", attempts: 3)
        XCTAssertEqual(outbox.latest?.nextAttemptAt, now.addingTimeInterval(60))
        for _ in 0..<8 { outbox.finish(sessionID: "one", now: now, reason: "offline", attempts: 3) }
        XCTAssertEqual(outbox.latest?.nextAttemptAt, now.addingTimeInterval(900))
        let manual = outbox.retryNow()
        XCTAssertEqual(manual, ["one"])
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: false, manualIDs: manual)?.sessionID, "one")
    }

    func testRecordingBlocksUploadsAndPauseRetainsTheBundle() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "one", bundlePath: "bundle-one", now: now)
        outbox.register(sessionID: "two", bundlePath: "bundle-two", now: now.addingTimeInterval(1), automatic: false)
        XCTAssertNil(outbox.beginNext(now: now, recording: true, automatic: true))
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "one")
        outbox.pause(sessionID: "one")
        XCTAssertEqual(outbox.latest?.state, .pending)
        XCTAssertEqual(outbox.pendingCount, 2)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "one")
        outbox.finish(sessionID: "one", now: now, reason: nil, attempts: 1)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true), "short recording stays local until explicit send")
        let manual = outbox.retryNow()
        XCTAssertEqual(outbox.latest?.automatic, true, "an explicit send opts this short bundle into retries")
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: false,
                                       manualIDs: manual)?.sessionID, "two")
    }

    func testRelaunchRecoversUploadingAndDropsMissingFiles() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("outbox.json")
        var outbox = DebugBundleOutbox()
        XCTAssertEqual(try DebugBundleOutbox.load(from: url).pendingCount, 0)
        outbox.register(sessionID: "one", bundlePath: "bundle-one", now: now)
        outbox.register(sessionID: "gone", bundlePath: "bundle-gone", now: now)
        _ = outbox.beginNext(now: now, recording: false, automatic: true)
        try outbox.save(to: url)
        var relaunched = try DebugBundleOutbox.load(from: url)
        XCTAssertEqual(relaunched.recover { $0 == "bundle-one" }, ["bundle-gone"])
        XCTAssertEqual(relaunched.pendingCount, 1)
        XCTAssertEqual(relaunched.latest?.bundlePath, "bundle-one", "relative to the new Documents container")
        XCTAssertEqual(relaunched.latest?.bundleURL(in: directory), directory.appendingPathComponent("bundle-one"))
        XCTAssertEqual(relaunched.latest?.state, .pending)
        XCTAssertEqual(relaunched.beginNext(now: now, recording: false, automatic: true)?.sessionID, "one")
        relaunched.finish(sessionID: "one", now: now, reason: "offline", attempts: 3)
        try relaunched.save(to: url)
        let failed = try DebugBundleOutbox.load(from: url)
        XCTAssertEqual(failed.latest?.reason, "offline")
        XCTAssertEqual(failed.latest?.nextAttemptAt, now.addingTimeInterval(30))
        XCTAssertEqual(failed.latest?.attempts, 3)
    }

    func testReceivedPruningKeepsTwentyAndDoesNotRediscoverPrunedBundles() throws {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "pending", bundlePath: "pending", now: now)
        for index in 0..<25 {
            let id = "session-\(index)"
            let created = now.addingTimeInterval(Double(index + 1))
            outbox.register(sessionID: id, bundlePath: id, now: created)
            outbox.finish(sessionID: id, now: created, reason: nil, attempts: 1)
        }
        XCTAssertEqual(outbox.entries.count, 21)
        XCTAssertEqual(outbox.pendingCount, 1)
        XCTAssertEqual(outbox.entries.filter { $0.state == .received }.count, 20)
        XCTAssertEqual(outbox.latest?.sessionID, "session-24")
        XCTAssertTrue(outbox.latest?.statusText.hasPrefix("Mac 受信済み") == true)
        outbox.register(sessionID: "session-0", bundlePath: "session-0", now: now.addingTimeInterval(1))
        XCTAssertEqual(outbox.entries.count, 21)
        let relaunched = try JSONDecoder().decode(DebugBundleOutbox.self, from: JSONEncoder().encode(outbox))
        XCTAssertEqual(relaunched.entries, outbox.entries)
        XCTAssertEqual(relaunched.prunedBefore, outbox.prunedBefore)
    }

    func testDuplicateReceiptRequiresMatchingSessionAndNeverAccepts409() {
        let body = Data(#"{"status":"duplicate","sessionID":"one"}"#.utf8)
        XCTAssertTrue(DebugBundleReceipt.accepts(status: 200, body: body, sessionID: "one"))
        XCTAssertFalse(DebugBundleReceipt.accepts(status: 200, body: body, sessionID: "different"))
        XCTAssertFalse(DebugBundleReceipt.accepts(status: 200, body: Data(), sessionID: "one"))
        XCTAssertFalse(DebugBundleReceipt.accepts(status: 409, body: body, sessionID: "one"))
        XCTAssertTrue(DebugBundleReceipt.accepts(status: 201, body: Data(#"{"accepted":true}"#.utf8), sessionID: "one"),
                      "old receivers still work")
    }

    @MainActor
    func testManualTransferResumesAfterRecordingInterruptsPackaging() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let bundle = directory.appendingPathComponent("phone_saber_triage_pause_test")
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try Data(#"{"sessionID":"pause_test","selectedImageCount":0,"images":[],"recordedFrameCount":30}"#.utf8)
            .write(to: bundle.appendingPathComponent("summary.json"))
        try Data("test".utf8).write(to: bundle.appendingPathComponent("prompt.md"))
        let transfer = DebugBundleTransfer(directoryForTesting: directory)
        XCTAssertFalse(DebugBundleTransfer.automaticTransferEnabled, "XCTest must never upload to the venue receiver")
        let firstPause = expectation(description: "first manual transfer paused by recording")
        let secondPause = expectation(description: "manual transfer resumed and paused again")
        var starts = 0
        let observer = NotificationCenter.default.addObserver(
            forName: DebugBundleTransfer.didChange, object: transfer, queue: .main
        ) { _ in
            // 録画開始を packaging の前に差し込み、実際の送信・Bonjour 探索を行わせない。
            if transfer.outbox.latest?.state == .uploading {
                starts += 1
                transfer.setRecordingActive(true)
            } else if transfer.outbox.latest?.state == .pending {
                if starts == 1 { firstPause.fulfill() }
                if starts == 2 { secondPause.fulfill() }
            }
        }
        defer { NotificationCenter.default.removeObserver(observer) }

        transfer.retryPending(immediately: true)
        wait(for: [firstPause], timeout: 5)
        XCTAssertEqual(transfer.outbox.latest?.attempts, 0)
        XCTAssertEqual(transfer.outbox.pendingCount, 1)
        transfer.setRecordingActive(false)
        wait(for: [secondPause], timeout: 5)
        XCTAssertEqual(starts, 2, "recording pause must retain the explicit send request")
        XCTAssertEqual(transfer.outbox.latest?.state, .pending)
        XCTAssertEqual(transfer.outbox.latest?.attempts, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath:
            directory.appendingPathComponent("pause_test_triage_transfer.psbt").path))
    }
}

/// Wire format, duplicate filtering, liveness and LAN fallback of the optional
/// peer-to-peer link. The Mac bridge itself is exercised by
/// Tools/test_phone_saber_p2p_bridge.py; here a loopback fake bridge answers.
final class P2PTransportTests: XCTestCase {
    // MARK: Wire format

    func testCoordinatesRoundTripWithTheUnchangedPayload() throws {
        for (text, color) in [("10,20,30,40", P2PColor.red), ("-5,0,1919,1079", .blue),
                              ("ts=1759400000.123456;1,2,3,4", .red),
                              ("timestamp=1759400000.123456;1,2,3,4", .blue), ("1.5,2.25,3,4", .blue)] {
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

    // 2026-10-10: Mac が Wi-Fi を変えた後、再接続が毎回同じ（死んだ）経路へ向かい約100秒届かなかった。
    func testPathRotationKeepsTheSystemChoiceFirstThenPinsEachAdvertisedInterface() {
        var rotation = P2PPathRotation()
        XCTAssertNil(rotation.next(from: ["en0", "awdl0"]), "最初の接続は従来どおり OS に任せる")
        rotation.attemptFailed()
        XCTAssertEqual(rotation.next(from: ["en0", "awdl0"]), "awdl0", "失敗後はまず peer-to-peer")
        rotation.attemptFailed()
        XCTAssertEqual(rotation.next(from: ["en0", "awdl0"]), "en0")
        rotation.attemptFailed()
        XCTAssertNil(rotation.next(from: ["en0", "awdl0"]), "一巡したら OS の選択へ戻る")
        rotation.attemptFailed()
        XCTAssertEqual(rotation.next(from: ["en0", "awdl0"]), "awdl0")
        rotation.reset()
        XCTAssertNil(rotation.next(from: ["en0", "awdl0"]), "リンクが使える状態に戻ったら元に戻る")
        rotation.attemptFailed()
        XCTAssertNil(rotation.next(from: []), "interface 情報が無ければ固定しない（override・古い OS）")
        XCTAssertEqual(P2PPathRotation.order(["en0", "awdl0", "en0", "", "llw0"]), ["awdl0", "en0", "llw0"])
    }

    func testPathRotationTriesANewlyAdvertisedInterfaceOnce() {
        var rotation = P2PPathRotation()
        rotation.prefer("awdl0")
        XCTAssertEqual(rotation.next(from: ["en0", "awdl0"]), "awdl0")
        XCTAssertNil(rotation.next(from: ["en0", "awdl0"]), "優先は1回だけ")
        rotation.prefer("awdl0")
        XCTAssertNil(rotation.next(from: ["en0"]), "もう広告されていない interface には固定しない")
    }

    func testPCReplyWatchWarnsOnlyAfterTheRouteHasBeenUnprovenForAWhile() {
        var watch = PCReplyWatch()
        XCTAssertFalse(watch.update(sending: true, lanVerified: false, p2pConnected: false, now: 10))
        XCTAssertFalse(watch.update(sending: true, lanVerified: false, p2pConnected: false, now: 12.9))
        XCTAssertTrue(watch.update(sending: true, lanVerified: false, p2pConnected: false, now: 13))
        XCTAssertFalse(watch.update(sending: true, lanVerified: true, p2pConnected: false, now: 14), "Unity の応答で解除")
        XCTAssertFalse(watch.update(sending: true, lanVerified: false, p2pConnected: false, now: 15), "数え直す")
        XCTAssertFalse(watch.update(sending: true, lanVerified: false, p2pConnected: true, now: 30), "P2P 接続中は警告しない")
        XCTAssertFalse(watch.update(sending: false, lanVerified: false, p2pConnected: false, now: 60), "停止中は警告しない")
        XCTAssertTrue(PCReplyWatch.message(manual: true).contains("手動 IP"), "手動 IP は Unity 以外の受信先でも使う")
        XCTAssertTrue(PCReplyWatch.message(manual: false).contains("同じ Wi-Fi"))
    }

    func testConnectivityLogKeepsTheNewestEntriesAndAppendsToAFile() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("Logs/connectivity.log")
        let log = ConnectivityLog(fileURL: file)
        XCTAssertEqual(log.revision, 0)
        for index in 0..<(ConnectivityLog.capacity + 5) { log.record("P2P", "event \(index)") }
        XCTAssertEqual(log.revision, ConnectivityLog.capacity + 5)
        XCTAssertEqual(log.recent.count, ConnectivityLog.capacity)
        XCTAssertEqual(log.recent.first?.message, "event 5")
        XCTAssertEqual(log.displayLines(last: 2).count, 2)
        XCTAssertTrue(log.displayLines(last: 1)[0].hasSuffix("[P2P] event \(ConnectivityLog.capacity + 4)"))
        log.flushForTesting()
        let text = try String(contentsOf: file, encoding: .utf8)
        XCTAssertEqual(text.split(separator: "\n").count, ConnectivityLog.capacity + 5)
        XCTAssertTrue(text.hasSuffix("[P2P] event \(ConnectivityLog.capacity + 4)\n"))
        XCTAssertNil(ConnectivityLog.defaultFileURL, "XCTest 中は端末にログファイルを作らない")
    }

    // 10秒ごとの計測行はファイルにだけ残す。画面と直近の出来事（経路の切替など）を押し出さない。
    func testPeriodicMetricsGoToTheFileWithoutPushingEventsOffTheScreen() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("Logs/connectivity.log")
        let log = ConnectivityLog(fileURL: file)
        log.record("経路", "LAN → P2P")
        let revision = log.revision
        for index in 0..<(ConnectivityLog.capacity + 5) { log.recordToFileOnly("CAPTURE_METRICS", "sample \(index)") }
        XCTAssertEqual(log.revision, revision, "画面の読み直しを起こさない")
        XCTAssertEqual(log.recent.map(\.message), ["LAN → P2P"])
        XCTAssertTrue(log.displayLines(last: CameraViewModel.connectivityLogTailCount)[0].hasSuffix("[経路] LAN → P2P"))
        log.flushForTesting()
        let lines = try String(contentsOf: file, encoding: .utf8).split(separator: "\n")
        XCTAssertEqual(lines.count, ConnectivityLog.capacity + 6)
        XCTAssertTrue(lines.last?.hasSuffix("[CAPTURE_METRICS] sample \(ConnectivityLog.capacity + 4)") == true)
        // 8時間ぶんの計測行（10秒ごと、1行 約250 byte）が1世代に収まる。
        XCTAssertGreaterThan(ConnectivityLog.maximumFileBytes, 8 * 360 * 250)
    }

    func testConnectivityLogRotationDoesNotDeleteADirectoryAtTheGenerationPath() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("connectivity.log")
        let previous = file.appendingPathExtension("1")
        try FileManager.default.createDirectory(at: previous, withIntermediateDirectories: true)
        let evidence = previous.appendingPathComponent("keep.txt")
        try Data("keep".utf8).write(to: evidence)
        let log = ConnectivityLog(fileURL: file)
        log.record("P2P", String(repeating: "x", count: ConnectivityLog.maximumFileBytes))
        log.flushForTesting()
        log.record("P2P", "after blocked rotation")
        log.flushForTesting()
        XCTAssertTrue(FileManager.default.fileExists(atPath: evidence.path), "回転先のディレクトリを再帰削除しない")
        XCTAssertTrue(try String(contentsOf: file, encoding: .utf8).contains("after blocked rotation"))
    }

    func testRouteChangesAreLoggedOncePerChange() {
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let p2p = P2PSender(endpointOverride: .hostPort(host: "127.0.0.1", port: 9))
        var now = 100.0
        let probe = LANLivenessProbe(clock: { now })
        let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: probe)
        var settings = CoordinateDelivery.Settings()
        settings.running = true
        settings.lanConfigured = true
        delivery.update(settings)
        for _ in 0..<3 { delivery.route("1,2,3,4", port: 5005, settings: settings, onSendStarted: nil, completion: { _ in }, noRoute: {}) }
        probe.recordReplyForTesting(LANLivenessProbe.replyPrefix)
        for _ in 0..<3 { delivery.route("1,2,3,4", port: 5005, settings: settings, onSendStarted: nil, completion: { _ in }, noRoute: {}) }
        now += LANLivenessProbe.aliveWindow + 1
        delivery.route("1,2,3,4", port: 5005, settings: settings, onSendStarted: nil, completion: { _ in }, noRoute: {})
        let logged = Array(ConnectivityLog.shared.recent.filter { $0.source == "経路" }.suffix(3).map(\.message))
        XCTAssertEqual(logged, [CoordinateDelivery.Route.lanUnverified.rawValue, CoordinateDelivery.Route.lanVerified.rawValue,
                                CoordinateDelivery.Route.lanUnverified.rawValue])
        lan.stop()
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

    // MARK: Browser recovery

    func testFailedBrowserReplacesPendingWaitingRetry() {
        let failed = expectation(description: "探索の失敗を通知する")
        let restarted = expectation(description: "待機中の再試行予約があっても探索を再開する")
        let sawFailure = LockedBox(false)
        let sender = P2PSender(timing: .init(browserRestartDelay: 0.01, browserWaitingRetryDelay: 0.05))
        defer { sender.stop() }
        // 実際の会場の bridge は選ばず、状態通知だけを再現する。
        sender.start(station: UUID().uuidString) { state in
            if case .failed = state, !sawFailure.value {
                sawFailure.mutate { $0 = true }
                failed.fulfill()
            } else if case .searching = state, sawFailure.value {
                restarted.fulfill()
            }
        }
        sender.simulateBrowserStatesForTesting([
            .waiting(NWError.dns(P2PBrowseErrorText.policyDeniedCode)),
            .failed(NWError.posix(.ENETDOWN))
        ])
        wait(for: [failed, restarted], timeout: 1)
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

    func testExpiredPongsBeforeTheTimerDoNotRestoreTheLinkOrCountAsSuccessful() throws {
        for expireBeforeReply in [false, true] {
            var stats = P2PRoundTripStats(window: 10, expireAfter: 2)
            var monitor = P2PLivenessMonitor()
            monitor.connectionStarted(at: 100)
            stats.pingSent(1, at: 100)
            monitor.pongReceived(at: 100.05)
            XCTAssertNotNil(stats.pongReceived(1, at: 100.05))
            for sequence in 2...4 {
                stats.pingSent(UInt64(sequence), at: 100 + Double(sequence) / 10)
            }
            // 復帰直後、timer の失効処理より先に古い応答が3枚届いても疎通の証拠にしない。
            if expireBeforeReply { stats.expire(at: 103) }
            for sequence in 2...4 {
                let now = 103 + Double(sequence) / 100
                let roundTrip = stats.pongReceived(UInt64(sequence), at: now)
                XCTAssertNil(roundTrip, "timer と受信の実行順で期限が変わらない")
                if roundTrip != nil { monitor.pongReceived(at: now) }
            }
            XCTAssertFalse(monitor.isUsable(at: 103.04), "古い3枚で P2P に復帰しない")
            let summary = try XCTUnwrap(stats.summary)
            XCTAssertEqual(summary.samples, 1)
            XCTAssertEqual(summary.lostPercent, 75)
            stats.expire(at: 104)
            XCTAssertNil(stats.pongReceived(2, at: 104))
            XCTAssertEqual(stats.summary?.lostPercent, 75, "失効済みの重複応答を二重集計しない")
            // 新しく送った ping の応答なら、従来どおり連続3枚で復帰する。
            for sequence in 5...7 {
                let now = 104 + Double(sequence) / 10
                stats.pingSent(UInt64(sequence), at: now)
                if stats.pongReceived(UInt64(sequence), at: now + 0.05) != nil {
                    monitor.pongReceived(at: now + 0.05)
                }
            }
            XCTAssertTrue(monitor.isUsable(at: 104.75))
        }
        var boundary = P2PRoundTripStats(expireAfter: 2)
        boundary.pingSent(1, at: 10)
        XCTAssertEqual(boundary.pongReceived(1, at: 12), 2, "期限ちょうどは既存の失効処理と同じく受理する")
    }

    // MARK: Loopback link against a fake bridge

    @MainActor
    func testUnchangedBonjourAndPathRefreshPreserveBothLANConnections() {
        let lan = UDPSender { _, _, _ in }
        let p2p = P2PSender()
        let model = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: false, idleTimerUpdater: { _ in })
        defer { model.stop(); p2p.stop() }
        model.startForTesting(host: "", manual: false)
        model.applyBonjourForTesting(host: "127.0.0.1")
        let generations = [5005, 5006].map { lan.connectionGenerationForTesting(port: $0) }
        let processorGeneration = model.processor.currentGeneration
        let lifecycleGeneration = model.lifecycleGenerationForTesting
        lan.simulateConnectionStateForTesting(port: 5005, state: .waiting)
        lan.simulateConnectionStateForTesting(port: 5006, state: .waiting)
        for port in [5005, 5006] { lan.send("1,2,3,4", to: port) { _ in } }
        XCTAssertEqual(lan.pendingCountForTesting, 2)
        for _ in 0..<3 {
            model.receiveNetworkPathUpdate(status: "Wi-Fi経路あり", interface: "wifi")
            model.applyBonjourForTesting(host: "127.0.0.1")
        }
        XCTAssertEqual([5005, 5006].map { lan.connectionGenerationForTesting(port: $0) }, generations)
        XCTAssertEqual(model.processor.currentGeneration, processorGeneration)
        XCTAssertEqual(model.lifecycleGenerationForTesting, lifecycleGeneration)
        XCTAssertEqual(lan.pendingCountForTesting, 2, "広告再解決で両色の待機座標を捨てない")
    }

    @MainActor
    func testP2PConnectedNotificationPreservesPendingVerifiedLANCoordinates() async throws {
        let bridge = try FakeBridge()
        bridge.answering = false
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let lan = UDPSender { _, _, _ in }
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let model = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: true, idleTimerUpdater: { _ in })
        defer { model.stop(); p2p.stop() }
        model.startForTesting(manual: false)
        model.recordLANReplyForTesting()
        lan.simulateConnectionStateForTesting(port: 5005, state: .waiting)
        lan.send("1,2,3,4", to: 5005) { _ in }
        XCTAssertEqual(lan.pendingCountForTesting, 1)
        bridge.answering = true
        let connected = await waitUntilMain { model.p2pState.isConnected }
        XCTAssertTrue(connected)
        XCTAssertEqual(lan.pendingCountForTesting, 1, "P2P の接続通知は座標経路の切替ではない")
    }

    func testCoordinateSendFailureInvalidatesP2PAndFallsBackToLAN() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                            coordinateSendHook: { _, complete in complete(POSIXError(.ENETDOWN)) })
        let lan = UDPSender { _, _, complete in complete(.success(1)) }
        defer { p2p.stop(); lan.stop() }
        p2p.start()
        let usable = await waitFor { p2p.isUsable }
        XCTAssertTrue(usable)
        lan.configure(host: "127.0.0.1")
        let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
        var config = CoordinateDelivery.Settings()
        config.running = true; config.lanConfigured = true; config.p2pEnabled = true
        delivery.update(config)
        let completed = expectation(description: "failed P2P coordinate goes to LAN")
        delivery.route("1,2,3,4", port: 5005, settings: config, onSendStarted: nil,
                       completion: { result in
                           if case .failure = result { XCTFail("LAN fallback missing") }
                           completed.fulfill()
                       }, noRoute: { XCTFail("LAN exists") })
        await fulfillment(of: [completed], timeout: 3)
        XCTAssertFalse(p2p.isUsable)
        XCTAssertEqual(delivery.currentRoute, .lanUnverified)
    }

    func testLateP2PFailureDoesNotSendOlderCoordinatesAfterNewerLANCoordinates() async throws {
        for newerPort in [5005, 5006] {
            let bridge = try FakeBridge()
            defer { bridge.stop() }
            let port = try await bridge.ready()
            let callback = LockedBox<((Error?) -> Void)?>(nil)
            let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                                coordinateSendHook: { _, complete in callback.mutate { $0 = complete } })
            let packets = LockedBox<[(String, Int)]>([])
            let lan = UDPSender { text, port, complete in
                packets.mutate { $0.append((text, port)) }
                complete(.success(1))
            }
            let probe = LANLivenessProbe()
            defer { p2p.stop(); lan.stop(); probe.stop() }
            p2p.start()
            let usable = await waitFor { p2p.isUsable }
            XCTAssertTrue(usable)
            lan.configure(host: "127.0.0.1")
            let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: probe)
            var config = CoordinateDelivery.Settings()
            config.running = true; config.lanConfigured = true; config.p2pEnabled = true
            delivery.update(config)
            let oldCompleted = LockedBox(false)
            delivery.route("1,2,3,4", port: 5005, settings: config, onSendStarted: nil,
                           completion: { _ in oldCompleted.mutate { $0 = true } }, noRoute: { XCTFail("LAN exists") })
            let started = await waitFor { callback.value != nil }
            XCTAssertTrue(started)
            // P2P の完了待ちの間に LAN が復帰し、新しい座標が先に届く。
            probe.recordReplyForTesting(LANLivenessProbe.replyPrefix)
            let newerSent = expectation(description: "newer coordinate via LAN")
            delivery.route("5,6,7,8", port: newerPort, settings: config, onSendStarted: nil,
                           completion: { _ in newerSent.fulfill() }, noRoute: { XCTFail("LAN exists") })
            await fulfillment(of: [newerSent], timeout: 2)
            callback.value?(POSIXError(.ENETDOWN))
            // 両キューを通過させてから、遅延した fallback の有無を確認する。
            _ = p2p.stateForTesting
            _ = lan.pendingCountForTesting
            if newerPort != 5005 {
                let completed = await waitFor { oldCompleted.value }
                XCTAssertTrue(completed)
            }
            XCTAssertEqual(packets.value.map { $0.0 }, newerPort == 5005 ? ["5,6,7,8"] : ["5,6,7,8", "1,2,3,4"])
            XCTAssertEqual(oldCompleted.value, newerPort != 5005, "別の色の更新では fallback を破棄しない")
            if newerPort == 5005 { XCTAssertEqual(delivery.currentRoute, .lanVerified) }
        }
    }

    func testRecoveredP2PDiscardsCoordinatesQueuedByTheLANFallback() async throws {
        for colorPort in [5005, 5006] {
            let bridge = try FakeBridge()
            defer { bridge.stop() }
            let port = try await bridge.ready()
            let failNext = LockedBox(true)
            let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                                coordinateSendHook: { _, complete in
                                    let fail = failNext.value
                                    failNext.mutate { $0 = false }
                                    complete(fail ? POSIXError(.ENETDOWN) : nil)
                                })
            let lanPackets = LockedBox<[String]>([])
            let lan = UDPSender { text, _, complete in
                lanPackets.mutate { $0.append(text) }
                complete(.success(1))
            }
            defer { p2p.stop(); lan.stop() }
            p2p.start()
            let usable = await waitFor { p2p.isUsable }
            XCTAssertTrue(usable)
            lan.configure(host: "127.0.0.1")
            lan.simulateConnectionStateForTesting(port: colorPort, state: .waiting)
            let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: LANLivenessProbe())
            var config = CoordinateDelivery.Settings()
            config.running = true; config.lanConfigured = true; config.p2pEnabled = true
            delivery.update(config)
            delivery.route("1,2,3,4", port: colorPort, settings: config, onSendStarted: nil,
                           completion: { _ in XCTFail("古い座標を LAN の復帰後に送らない") },
                           noRoute: { XCTFail("LAN exists") })
            let queued = await waitFor { lan.pendingCountForTesting == 1 }
            XCTAssertTrue(queued, "P2P の送信失敗で LAN の接続待ちへ退避する")
            XCTAssertEqual(delivery.currentRoute, .lanUnverified)
            let recovered = await waitFor { p2p.isUsable }
            XCTAssertTrue(recovered)

            let sent = expectation(description: "newer coordinate via recovered P2P")
            delivery.route("5,6,7,8", port: colorPort, settings: config, onSendStarted: nil,
                           completion: { result in
                               if case .failure = result { XCTFail("P2P recovery failed") }
                               sent.fulfill()
                           }, noRoute: { XCTFail("P2P exists") })
            await fulfillment(of: [sent], timeout: 3)
            XCTAssertEqual(delivery.currentRoute, .p2p)
            XCTAssertEqual(lan.pendingCountForTesting, 0)
            XCTAssertEqual(lan.discardedForP2PCountForTesting, 1)
            lan.simulateConnectionStateForTesting(port: colorPort, state: .ready)
            // UDP キューの先行処理を完了させ、LAN 復帰時の実送信も検査する。
            XCTAssertEqual(lan.pendingCountForTesting, 0)
            XCTAssertTrue(lanPackets.value.isEmpty)
        }
    }

    func testPendingP2PCoordinateFallsBackWhenPongsExpireBeforeCompletion() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let now = LockedBox(100.0)
        let callback = LockedBox<((Error?) -> Void)?>(nil)
        let p2p = P2PSender(endpointOverride: .hostPort(host: "127.0.0.1", port: port), clock: { now.value },
                            coordinateSendHook: { _, complete in callback.mutate { $0 = complete } })
        defer { p2p.stop() }
        p2p.start()
        let usable = await waitFor { p2p.isUsable }
        XCTAssertTrue(usable)
        p2p.send("1,2,3,4", to: 5005) { _ in }
        let started = await waitFor { callback.value != nil }
        XCTAssertTrue(started)
        let fallback = expectation(description: "latest pending coordinate uses LAN")
        p2p.send("5,6,7,8", to: 5005, completion: { _ in XCTFail("expired P2P cannot send") },
                 fallback: { fallback.fulfill() })
        // 同じキューの先行 send を完了させてから pong を止め、liveness の時刻を進める。
        _ = p2p.stateForTesting
        bridge.answering = false
        now.mutate { $0 += 2 }
        callback.value?(nil)
        await fulfillment(of: [fallback], timeout: 3)
    }

    func testUDPNetworkSendFailureReconnectsOnlyTheFailedPortAndKeepsTheLatestCoordinate() async {
        let callback = LockedBox<((Result<TimeInterval, Error>) -> Void)?>(nil)
        let packets = LockedBox<[(String, Int)]>([])
        let first = expectation(description: "first RED send")
        let failed = expectation(description: "RED failure reported")
        let latest = expectation(description: "latest RED delivered after reconnect")
        let blue = expectation(description: "BLUE remains usable")
        let timing = UDPSender.Timing(waitingTimeout: 1, sendWatchdogTimeout: 1,
                                      reconnectInitialDelay: 0.05, reconnectMaximumDelay: 0.05)
        let sender = UDPSender(sendHandler: { text, port, complete in
            packets.mutate { $0.append((text, port)) }
            if text == "1,2,3,4" {
                callback.mutate { $0 = complete }
                first.fulfill()
            } else { complete(.success(1)) }
        }, timing: timing)
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        let redGeneration = sender.connectionGenerationForTesting(port: 5005)
        let blueGeneration = sender.connectionGenerationForTesting(port: 5006)
        sender.send("1,2,3,4", to: 5005) { result in
            if case .success = result { XCTFail("network failure must be reported") }
            failed.fulfill()
        }
        await fulfillment(of: [first], timeout: 2)
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("superseded coordinate") }
        sender.send("9,10,11,12", to: 5005) { result in
            if case .failure = result { XCTFail("latest coordinate must recover") }
            latest.fulfill()
        }
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        // state は ready のまま、Network の送信完了だけが経路のエラーを返す。
        callback.value?(.failure(NWError.posix(.ENETDOWN)))
        await fulfillment(of: [failed, latest], timeout: 2)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), redGeneration + 1)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 1)
        XCTAssertEqual(sender.watchdogTimeoutCountForTesting(port: 5005), 0)
        XCTAssertEqual(packets.value.filter { $0.1 == 5005 }.map(\.0), ["1,2,3,4", "9,10,11,12"])
        sender.send("13,14,15,16", to: 5006) { _ in blue.fulfill() }
        await fulfillment(of: [blue], timeout: 2)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5006), blueGeneration)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5006), 0)
    }

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

    func testDuplicatePongsDoNotRestoreAStaleLink() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let now = LockedBox<TimeInterval>(100)
        let sender = P2PSender(timing: fastTiming(),
                               endpointOverride: .hostPort(host: "127.0.0.1", port: port),
                               clock: { now.value })
        defer { sender.stop() }
        let falseRecovery = expectation(description: "重複 pong で P2P に戻らない")
        falseRecovery.isInverted = true
        let watching = LockedBox(false)
        sender.start(onState: { state in
            if watching.value && state.isConnected { falseRecovery.fulfill() }
        })
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected)
        bridge.answering = false
        // 送信済みの応答を処理してから、最後の pong を期限切れにする。
        try await Task.sleep(for: .milliseconds(150))
        now.mutate { $0 += 0.4 }
        let stale = await waitFor { !sender.isUsable }
        XCTAssertTrue(stale)
        watching.mutate { $0 = true }
        bridge.repeatLastPong(count: 3)
        await fulfillment(of: [falseRecovery], timeout: 0.3)
        XCTAssertFalse(sender.isUsable, "同じ応答3枚では新しい疎通の証拠にならない")
        let fallback = expectation(description: "LAN へ退避する")
        sender.send("1,2,3,4", to: 5005, completion: { _ in XCTFail("古い pong で座標を送らない") },
                    fallback: { fallback.fulfill() })
        await fulfillment(of: [fallback], timeout: 1)

        watching.mutate { $0 = false }
        bridge.answering = true
        let recovered = await waitFor { sender.isUsable }
        XCTAssertTrue(recovered, "新しい ping への応答なら復帰する")
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

    func testBundleUploaderReadsFragmentedDuplicateReceiptAndRejectsWrongSessionAnd409() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("p2p-upload-\(UUID()).psbt")
        try Data("package".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        for (status, id, received) in [(200, "one", true), (200, "other", false), (409, "one", false)] {
            let receiver = try FakeHTTPReceiver(status: status, body: #"{"status":"duplicate","sessionID":""# + id + #""}"#,
                                                fragmentResponse: true)
            defer { receiver.stop() }
            let port = try await receiver.ready()
            let outcome = await withCheckedContinuation { continuation in
                P2PBundleUploader(endpointOverride: .hostPort(host: "127.0.0.1", port: port))
                    .upload(fileURL: file, sessionID: "one") { continuation.resume(returning: $0) }
            }
            if received {
                guard case .uploaded = outcome else { return XCTFail("\(outcome)") }
            } else {
                guard case .failed = outcome else { return XCTFail("\(outcome)") }
            }
        }
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
    func testPCReplyMissingAfterP2POnlyBridgeStopsUsesAutomaticMessage() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let now = LockedBox(100.0)
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let model = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: true,
            connectivityClock: { now.value }, idleTimerUpdater: { _ in })
        defer { model.stop(); p2p.stop(); lan.stop() }
        model.startForTesting(host: "", manual: false)
        XCTAssertFalse(model.lanConfiguredForTesting)
        let connected = await waitUntilMain { model.p2pState.isConnected }
        XCTAssertTrue(connected)
        model.refreshConnectivityStatusForTesting()
        now.mutate { $0 = 110 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing, "P2P 接続は LAN 宛先がなくても到達の証拠になる")

        bridge.stop()
        let disconnected = await waitUntilMain { !model.p2pState.isConnected }
        XCTAssertTrue(disconnected)
        model.refreshConnectivityStatusForTesting()
        now.mutate { $0 = 112.99 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)
        now.mutate { $0 = 113 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertTrue(model.pcReplyMissing, "橋が停止してから3秒で警告する")
        XCTAssertEqual(model.pcReplyMissingMessage, PCReplyWatch.automaticMessage)
    }

    @MainActor
    func testPCReplyMissingTracksLANReplyExpiryRecoveryAndLogsBothTransitions() {
        let now = LockedBox(100.0)
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let model = CameraViewModel(sender: lan, p2pEnabled: false,
            connectivityClock: { now.value }, idleTimerUpdater: { _ in })
        defer { model.stop(); lan.stop() }
        model.startForTesting(host: "192.0.2.1", manual: false)
        XCTAssertTrue(model.lanConfiguredForTesting)
        // 実ネットワークの返信を待たず、通常と同じ LAN 応答記録経路を使う。
        for time in [100.0, 110.0] {
            now.mutate { $0 = time }
            model.recordLANReplyForTesting()
            model.refreshConnectivityStatusForTesting()
            XCTAssertFalse(model.pcReplyMissing)
        }
        now.mutate { $0 = 110 + LANLivenessProbe.aliveWindow }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing, "最後の応答は有効期限の境界まで有効")
        let expiredAt = 110 + LANLivenessProbe.aliveWindow + 0.01
        now.mutate { $0 = expiredAt }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)
        now.mutate { $0 = expiredAt + PCReplyWatch.warnAfter - 0.01 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)

        let revision = ConnectivityLog.shared.revision
        now.mutate { $0 = expiredAt + PCReplyWatch.warnAfter }
        model.refreshConnectivityStatusForTesting()
        XCTAssertTrue(model.pcReplyMissing)
        XCTAssertEqual(ConnectivityLog.shared.revision, revision + 1)
        model.refreshConnectivityStatusForTesting()
        XCTAssertEqual(ConnectivityLog.shared.revision, revision + 1, "同じ状態を繰り返し記録しない")
        model.recordLANReplyForTesting()
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)
        XCTAssertEqual(ConnectivityLog.shared.revision, revision + 2)
        let transitions = Array(ConnectivityLog.shared.recent.suffix(2))
        XCTAssertEqual(transitions.map(\.source), ["状態", "状態"])
        XCTAssertEqual(transitions.map(\.message),
            ["PC から応答なし（Unity 応答なし・P2P 未接続）", "PC への経路が回復"])
        XCTAssertEqual(model.connectivityLogTail,
            ConnectivityLog.shared.displayLines(last: CameraViewModel.connectivityLogTailCount))
    }

    @MainActor
    func testPCReplyMissingManualIPIgnoresAvailableP2PAndUsesManualMessage() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        let now = LockedBox(100.0)
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let p2p = P2PSender(timing: fastTiming(), endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        let model = CameraViewModel(sender: lan, p2pSender: p2p, p2pEnabled: true,
            connectivityClock: { now.value }, idleTimerUpdater: { _ in })
        defer { model.stop(); p2p.stop(); lan.stop() }
        let connected = await waitUntilMain { model.p2pState.isConnected }
        XCTAssertTrue(connected)
        model.startForTesting(host: "192.0.2.1", manual: true)
        XCTAssertTrue(model.p2pState.isConnected, "自動モードで接続済みの橋を残して手動 IP に切り替える")
        model.refreshConnectivityStatusForTesting()
        now.mutate { $0 = 102.99 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)
        now.mutate { $0 = 103 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertTrue(p2p.isUsable, "橋が使えても手動 IP の到達確認にはならない")
        XCTAssertTrue(model.pcReplyMissing)
        XCTAssertEqual(model.pcReplyMissingMessage, PCReplyWatch.manualMessage)
    }

    @MainActor
    func testStopClearsPCReplyMissingAndRestartsTheGracePeriod() {
        let now = LockedBox(100.0)
        let lan = UDPSender { _, _, completion in completion(.success(1)) }
        let model = CameraViewModel(sender: lan, p2pEnabled: false,
            connectivityClock: { now.value }, idleTimerUpdater: { _ in })
        defer { model.stop(); lan.stop() }
        model.startForTesting(host: "", manual: false)
        model.refreshConnectivityStatusForTesting()
        now.mutate { $0 = 103 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertTrue(model.pcReplyMissing)
        model.stop()
        XCTAssertFalse(model.pcReplyMissing, "stop() 自体が警告を解除する")
        now.mutate { $0 = 200 }
        model.startForTesting(host: "", manual: false)
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing, "再開直後に前回の応答待ち時間を引き継がない")
        now.mutate { $0 = 202.99 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertFalse(model.pcReplyMissing)
        now.mutate { $0 = 203 }
        model.refreshConnectivityStatusForTesting()
        XCTAssertTrue(model.pcReplyMissing)
    }

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

    func testLANLivenessProbeAcceptsOnlyRepliesForTheVersionAndPortsThisPhoneUses() {
        XCTAssertTrue(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 1"))
        XCTAssertTrue(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac station=A"))
        XCTAssertTrue(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 1 name=Old Unity"), "port を書かない古い応答は従来どおり")
        XCTAssertFalse(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 10 red=5005 blue=5006"), "版 10 を版 1 と読まない")
        XCTAssertFalse(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 1 red=6005 blue=5006"), "別の port で待つ Unity には届かない")
        XCTAssertFalse(LANLivenessProbe.isCompatibleReply("PHONESABER_UNITY 1 red=5005 blue=6006"))
        XCTAssertFalse(LANLivenessProbe.isCompatibleReply("PHONESABER_DISCOVER 1"))
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
    private var lastPong: (connection: NWConnection, data: Data)?

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
                    let pong = P2PMessage.pong(session: message.session, echoing: message.sequence).encoded()
                    self.lastPong = (connection, pong)
                    connection.send(content: pong, completion: .idempotent)
                case .coordinates:
                    self.state.mutate { $0.coordinates.append(message) }
                default:
                    break
                }
            }
            if error == nil { self.receive(connection) }
        }
    }

    func repeatLastPong(count: Int) {
        queue.sync {
            guard let lastPong else { return }
            for _ in 0..<count {
                lastPong.connection.send(content: lastPong.data, completion: .idempotent)
            }
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
    private let body: String
    private let fragmentResponse: Bool

    init(status: Int, body: String = "{}", fragmentResponse: Bool = false) throws {
        self.status = status
        self.body = body
        self.fragmentResponse = fragmentResponse
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
                let header = "HTTP/1.0 \(self.status) X\r\nContent-Length: \(self.body.utf8.count)\r\n\r\n"
                if self.fragmentResponse {
                    connection.send(content: Data(header.utf8), completion: .contentProcessed { _ in
                        self.queue.asyncAfter(deadline: .now() + 0.05) {
                            connection.send(content: Data(self.body.utf8), contentContext: .finalMessage, isComplete: true,
                                            completion: .contentProcessed { _ in connection.cancel() })
                        }
                    })
                } else {
                    connection.send(content: Data((header + self.body).utf8), contentContext: .finalMessage, isComplete: true,
                                    completion: .contentProcessed { _ in connection.cancel() })
                }
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
import Network
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
            XCTAssertEqual(delivery.currentRoute, manual ? .lanUnverified : .lanVerified)
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
        XCTAssertEqual(delivery.currentRoute, .lanUnverified)
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
            XCTAssertEqual(delivery.currentRoute, .none)
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
        XCTAssertEqual(delivery.currentRoute, .none)
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

// timer を待たず、復帰時の観測更新より前に原因が残ることも検証する。
final class SendGapTests: XCTestCase {
    func testEveryCauseLogsOnceAndSendingAgainRearmsTheMonitor() {
        for cause in ["camera delivered no frame", "processing stalled", "no fresh detection for either colour",
                      "route had no destination", "send error", "generation rejected"] {
            var now = 0.0
            var lines: [String] = []
            let monitor = SendGapMonitor(clock: { now }, automaticPolling: false) { lines.append($0) }
            monitor.setIntent(true)
            monitor.cameraFrame(); monitor.results(fresh: true); monitor.sent()
            if cause != "camera delivered no frame" {
                // 停止中もカメラは動いている（更新で自動 poll する前の時刻を使う）。
                now = 0.4; monitor.cameraFrame()
            }
            if cause != "camera delivered no frame" && cause != "processing stalled" {
                now = 0.4; monitor.results(fresh: cause != "no fresh detection for either colour")
            }
            now = 0.5; monitor.poll()
            XCTAssertTrue(lines.isEmpty, "0.5 秒ちょうどは停止扱いにしない")
            now = 0.6
            if ["route had no destination", "send error", "generation rejected"].contains(cause) {
                monitor.blocked(cause, detail: "test\nline")
            }
            monitor.poll(); monitor.poll()
            XCTAssertEqual(lines.count, 1)
            XCTAssertTrue(lines[0].contains("cause=\(cause)"), lines[0])
            XCTAssertFalse(lines[0].contains("\n"))
            monitor.sent()
            now = 1.2
            monitor.poll()
            XCTAssertEqual(lines.count, 2)
            monitor.setIntent(false)
            now = 2
            monitor.poll()
            XCTAssertEqual(lines.count, 2)
        }
    }

    func testLateTimerDoesNotLoseCameraOrDetectionGapOnRecovery() {
        for cameraGap in [true, false] {
            var now = 0.0
            var lines: [String] = []
            let monitor = SendGapMonitor(clock: { now }, automaticPolling: false) { lines.append($0) }
            monitor.setIntent(true)
            monitor.cameraFrame(); monitor.results(fresh: true); monitor.sent()
            if !cameraGap {
                for time in [0.2, 0.4] { now = time; monitor.cameraFrame(); monitor.results(fresh: false) }
            }
            now = 0.6
            monitor.cameraFrame(); monitor.processing(active: true); monitor.results(fresh: true); monitor.sent()
            XCTAssertEqual(lines.count, 1)
            XCTAssertTrue(lines[0].contains(cameraGap ? "cause=camera delivered no frame" : "cause=no fresh detection for either colour"), lines[0])
        }
    }

    func testReconfigurationKeepsIntentAndStopStillLogsTheLastGap() {
        var now = 0.0
        var lines: [String] = []
        let monitor = SendGapMonitor(clock: { now }, automaticPolling: false) { lines.append($0) }
        monitor.setIntent(true)
        monitor.context("thermal stop/start")
        monitor.beginReconfiguration(); monitor.setIntent(false); monitor.setIntent(true); monitor.endReconfiguration()
        now = 0.6
        monitor.setIntent(false)
        XCTAssertEqual(lines.count, 1)
        XCTAssertTrue(lines.first?.contains("context=thermal stop/start") == true)
        now = 2; monitor.poll()
        XCTAssertEqual(lines.count, 1)
    }

    func testLogExportIncludesRotationAndLimitsWholeUTF8Lines() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("connectivity.log")
        try "2026-10-13T00:00:00.000Z previous SEND_GAP\n".write(to: file.appendingPathExtension("1"), atomically: true, encoding: .utf8)
        let log = ConnectivityLog(fileURL: file)
        log.record("TEST", "current", at: Date(timeIntervalSince1970: 0))
        XCTAssertTrue(log.exportTail().contains("previous SEND_GAP"), log.exportTail())
        XCTAssertTrue(log.exportTail().contains("1970-01-01T00:00:00.000Z [TEST] current"), log.exportTail())
        for _ in 0..<1000 { log.recordToFileOnly("TEST", String(repeating: "日本語", count: 10)) }
        let tail = log.exportTail()
        XCTAssertLessThanOrEqual(tail.utf8.count, 64 * 1024)
        XCTAssertFalse(tail.contains("�"))
        XCTAssertTrue(tail.hasPrefix("1970-") || tail.first?.isNumber == true, String(tail.prefix(200)))
        XCTAssertTrue(tail.hasSuffix("\n"))
    }

    func testUDPSendErrorReconnectsOnlyTheFailedPortAndKeepsNewestCoordinate() {
        let reconnecting = PipelineLockedValue(false)
        let packets = PipelineLockedValue<[(String, Int)]>([])
        let callback = PipelineLockedValue<((Result<TimeInterval, Error>) -> Void)?>(nil)
        let failed = expectation(description: "送信エラーを通知する")
        let redRecovered = expectation(description: "再接続後に最新の赤座標を送る")
        let blueSent = expectation(description: "青の送信は止めない")
        let timing = UDPSender.Timing(sendWatchdogTimeout: 5,
                                      reconnectInitialDelay: 0.05, reconnectMaximumDelay: 0.05)
        let sender = UDPSender(sendHandler: { text, port, complete in
            packets.mutate { $0.append((text, port)) }
            if text == "1,2,3,4" { callback.mutate { $0 = complete } }
            else {
                if port == 5005 { XCTAssertTrue(reconnecting.value, "失敗した接続で次の赤座標を送らない") }
                complete(.success(1))
            }
        }, timing: timing)
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1") { states, _, _ in
            if states[5005] == "reconnecting" { reconnecting.mutate { $0 = true } }
        }
        let redGeneration = sender.connectionGenerationForTesting(port: 5005)
        let blueGeneration = sender.connectionGenerationForTesting(port: 5006)
        sender.send("1,2,3,4", to: 5005) { result in
            if case .success = result { XCTFail("失敗した座標を成功にしない") }
            failed.fulfill()
        }
        // 同期 getter をバリアにし、送信完了通知前に最新スロットを埋める。
        XCTAssertEqual(sender.pendingCountForTesting, 0)
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("置換済みの座標は送らない") }
        sender.send("9,10,11,12", to: 5005) { _ in
            redRecovered.fulfill()
        }
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        callback.value?(.failure(NWError.posix(.ENETDOWN)))
        wait(for: [failed], timeout: 2)
        sender.send("13,14,15,16", to: 5006) { _ in blueSent.fulfill() }
        // 失敗時に接続状態の failed 通知が来なくても、次の赤座標には新しい接続を使う。
        wait(for: [blueSent, redRecovered], timeout: 2)
        XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), redGeneration)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 1)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5006), blueGeneration)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5006), 0)
        XCTAssertEqual(packets.value.filter { $0.1 == 5005 }.map { $0.0 }, ["1,2,3,4", "9,10,11,12"])
        callback.value?(.success(2))
        XCTAssertEqual(sender.rejectedCompletionCountForTesting, 1, "旧接続の遅延通知を拒否する")
    }

    func testStopCancelsReconnectAfterUDPSendErrorAndStartUsesNewConnection() {
        let failed = expectation(description: "旧送信のエラー")
        let unexpectedSend = expectation(description: "停止後に再送しない")
        unexpectedSend.isInverted = true
        let oldCallback = PipelineLockedValue<((Result<TimeInterval, Error>) -> Void)?>(nil)
        let timing = UDPSender.Timing(sendWatchdogTimeout: 5,
                                      reconnectInitialDelay: 0.05, reconnectMaximumDelay: 0.05)
        let sender = UDPSender(sendHandler: { text, _, complete in
            if text == "1,2,3,4" {
                oldCallback.mutate { $0 = complete }
                complete(.failure(NWError.posix(.ECONNREFUSED)))
            } else if text == "5,6,7,8" { unexpectedSend.fulfill() }
            else { complete(.success(1)) }
        }, timing: timing)
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1", ports: [5005])
        sender.send("1,2,3,4", to: 5005) { _ in failed.fulfill() }
        wait(for: [failed], timeout: 2)
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("停止で破棄する座標") }
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        sender.stop()
        wait(for: [unexpectedSend], timeout: 0.1)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 0)
        XCTAssertEqual(sender.pendingCountForTesting, 0)
        sender.configure(host: "127.0.0.1", ports: [5005])
        let generation = sender.connectionGenerationForTesting(port: 5005)
        oldCallback.value?(.failure(NWError.posix(.ENETDOWN)))
        XCTAssertEqual(sender.rejectedCompletionCountForTesting, 1)
        let restarted = expectation(description: "再開後の座標")
        sender.send("9,10,11,12", to: 5005) { result in
            if case .failure = result { XCTFail("再開後の送信が失敗した") }
            restarted.fulfill()
        }
        wait(for: [restarted], timeout: 2)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generation)
    }

    func testInvalidUDPPayloadDoesNotReconnectOrBlockNextCoordinate() {
        let packets = PipelineLockedValue<[String]>([])
        let sender = UDPSender { text, port, complete in
            packets.mutate { $0.append(text) }
            if text == "不正な座標" {
                complete(.failure(UDPSender.SendError.invalidPayload(host: "127.0.0.1", port: port)))
            } else { complete(.success(1)) }
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1", ports: [5005])
        let generation = sender.connectionGenerationForTesting(port: 5005)
        let failed = expectation(description: "ASCII 変換エラー")
        sender.send("不正な座標", to: 5005) { result in
            if case .success = result { XCTFail("不正な座標は失敗する") }
            failed.fulfill()
        }
        wait(for: [failed], timeout: 2)
        let completed = expectation(description: "次の有効な座標")
        sender.send("1,2,3,4", to: 5005) { _ in completed.fulfill() }
        wait(for: [completed], timeout: 2)
        XCTAssertEqual(packets.value, ["不正な座標", "1,2,3,4"])
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generation)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 0)
    }

    func testIdenticalUDPConfigurationPreservesInFlightAndNewestPendingCoordinates() {
        let packets = PipelineLockedValue<[String]>([])
        let callback = PipelineLockedValue<((Result<TimeInterval, Error>) -> Void)?>(nil)
        let started = expectation(description: "active send")
        let latest = expectation(description: "latest pending send")
        let sender = UDPSender { text, _, complete in
            packets.mutate { $0.append(text) }
            callback.mutate { $0 = complete }
            if text == "1,2,3,4" { started.fulfill() } else { latest.fulfill() }
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        sender.send("1,2,3,4", to: 5005) { _ in }
        wait(for: [started], timeout: 2)
        sender.send("5,6,7,8", to: 5005) { _ in }
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        let generation = sender.connectionGenerationForTesting(port: 5005)
        sender.configure(host: "127.0.0.1", ports: [5006, 5005])
        sender.updateHost("127.0.0.1")
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generation)
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        callback.value?(.success(1))
        wait(for: [latest], timeout: 2)
        XCTAssertEqual(packets.value, ["1,2,3,4", "5,6,7,8"])
    }
}

#if !PIPELINE_ROUTING_STANDALONE
final class DebugBundleLogTests: XCTestCase {
    func testOptionalLogTravelsInPackageAndOldBundlesStillPackage() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("{\"sessionID\":\"gap_test\",\"selectedImageCount\":0,\"images\":[]}".utf8)
            .write(to: root.appendingPathComponent("summary.json"))
        try Data("test".utf8).write(to: root.appendingPathComponent("prompt.md"))
        let logURL = root.appendingPathComponent("connectivity.log")
        let log = "2026-10-13T16:15:40.000Z [SEND_GAP] cause=processing stalled 日本語\n"
        for present in [false, true] {
            if present { try log.write(to: logURL, atomically: true, encoding: .utf8) }
            let package = try DebugBundleTransfer.makePackage(for: root)
            defer { try? FileManager.default.removeItem(at: package) }
            let bytes = try Data(contentsOf: package)
            XCTAssertEqual(String(decoding: bytes.prefix(4), as: UTF8.self), "PSBT")
            let length = bytes[4..<8].reduce(0) { ($0 << 8) | Int($1) }
            let manifest = try XCTUnwrap(JSONSerialization.jsonObject(with: bytes[8..<8 + length]) as? [String: Any])
            let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
            XCTAssertEqual(files.count, present ? 3 : 2)
            XCTAssertEqual(files.contains { $0["path"] as? String == "connectivity.log" }, present)
            if present {
                XCTAssertTrue(String(decoding: bytes.suffix(from: 8 + length), as: UTF8.self).contains(log))
                XCTAssertEqual(files.first { $0["path"] as? String == "connectivity.log" }?["contentType"] as? String,
                               "text/plain; charset=utf-8")
            }
        }
        try String(repeating: "x", count: 64 * 1024 + 1).write(to: logURL, atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try DebugBundleTransfer.makePackage(for: root))
        try FileManager.default.removeItem(at: logURL)
        try FileManager.default.createSymbolicLink(at: logURL, withDestinationURL: root.appendingPathComponent("prompt.md"))
        XCTAssertThrowsError(try DebugBundleTransfer.makePackage(for: root))
    }
}

#endif
