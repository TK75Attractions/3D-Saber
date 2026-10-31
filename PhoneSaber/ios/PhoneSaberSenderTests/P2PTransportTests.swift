import Network
#if !PIPELINE_ROUTING_STANDALONE
@testable import PhoneSaberSender
import XCTest

final class DiagnosticsDestinationTests: XCTestCase {
    func testManualThenDiscoveredMacThenCoordinateHost() throws {
        let mac = try XCTUnwrap(DiagnosticsEndpoint.receiverURL(host: "triage-mac.local.", port: 9876))
        for coordinateHost in ["192.168.1.10", "zen-windows.local"] {
            let manual = try XCTUnwrap(DiagnosticsEndpoint.choose(
                manualHost: " 10.0.0.42\n", discovered: mac, coordinateHost: coordinateHost))
            XCTAssertEqual(manual.url.absoluteString, "http://10.0.0.42:8765/v1/bundle")
            XCTAssertEqual(manual.source, .manual)
            let discovered = try XCTUnwrap(DiagnosticsEndpoint.choose(
                manualHost: " \n", discovered: mac, coordinateHost: coordinateHost))
            XCTAssertEqual(discovered.url, mac, "A/Bどちらの座標宛先でも診断はMacへ送る")
            XCTAssertEqual(discovered.source, .discovered)
            let fallback = try XCTUnwrap(DiagnosticsEndpoint.choose(
                manualHost: "", discovered: nil, coordinateHost: coordinateHost))
            XCTAssertEqual(fallback.url.host, coordinateHost)
            XCTAssertEqual(fallback.url.port, 8765)
            XCTAssertEqual(fallback.source, .coordinate)
        }
        XCTAssertEqual(DiagnosticsEndpoint.choose(manualHost: "10.0.0.42", discovered: nil,
                                                   coordinateHost: "")?.source, .manual)
        XCTAssertEqual(DiagnosticsEndpoint.choose(manualHost: "", discovered: mac,
                                                   coordinateHost: "")?.source, .discovered)
        XCTAssertNil(DiagnosticsEndpoint.choose(manualHost: "", discovered: nil, coordinateHost: ""))
        XCTAssertNil(DiagnosticsEndpoint.receiverURL(host: "mac.local", port: 0))
    }

    func testManualIPPersistsAndClearingRestoresDiscovery() throws {
        let suite = "DiagnosticsDestinationTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let destination = DiagnosticsDestination(defaults: defaults)
        XCTAssertEqual(destination.manualHost, "")
        destination.manualHost = " 192.168.1.42 "
        let relaunched = DiagnosticsDestination(defaults: defaults)
        XCTAssertEqual(relaunched.manualHost, "192.168.1.42")
        XCTAssertEqual(relaunched.selected(coordinateHost: "zen-windows.local")?.source, .manual)
        relaunched.manualHost = ""
        XCTAssertEqual(DiagnosticsDestination(defaults: defaults).manualHost, "")
        XCTAssertEqual(relaunched.selected(coordinateHost: "zen-windows.local")?.source, .coordinate)
    }
}

final class DebugBundleOutboxTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_000)

    func testUploadAttemptHistorySurvivesManualRetryRelaunchAndLegacyOutbox() throws {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "clip", bundlePath: "clip", now: now)
        XCTAssertEqual(outbox.beginUpload(sessionID: "clip"), 1)
        outbox.finish(sessionID: "clip", now: now, reason: "offline", attempts: 1)
        _ = outbox.retryNow()
        XCTAssertEqual(outbox.latest?.attempts, 0, "再送制御用の上限は従来どおりリセットする")
        outbox = try JSONDecoder().decode(DebugBundleOutbox.self, from: JSONEncoder().encode(outbox))
        XCTAssertEqual(outbox.beginUpload(sessionID: "clip"), 2)
        // 201返信の切断から、重複200を受ける次のHTTP試行まで履歴を保持する。
        outbox.finish(sessionID: "clip", now: now, reason: "reply disconnected", attempts: 1)
        XCTAssertEqual(outbox.beginUpload(sessionID: "clip"), 3)
        outbox.finish(sessionID: "clip", now: now, reason: nil, attempts: 1)
        XCTAssertEqual(outbox.latest?.state, .received)

        var legacy = DebugBundleOutbox()
        legacy.register(sessionID: "legacy", bundlePath: "legacy", now: now)
        legacy.finish(sessionID: "legacy", now: now, reason: "offline", attempts: 4)
        let data = try JSONEncoder().encode(legacy)
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("uploadAttempts"))
        legacy = try JSONDecoder().decode(DebugBundleOutbox.self, from: data)
        _ = legacy.retryNow()
        XCTAssertEqual(legacy.beginUpload(sessionID: "legacy"), 5)
        // HTTP開始後、finish前の中断・再起動でも試行番号を再利用しない。
        legacy = try JSONDecoder().decode(DebugBundleOutbox.self, from: JSONEncoder().encode(legacy))
        XCTAssertEqual(legacy.beginUpload(sessionID: "legacy"), 6)
    }

    func testReceiverOfflineKeepsRetryingEveryTwoMinutesUntilTheReceiverIsBack() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "clip", bundlePath: "clip", now: now)
        var time = now
        // 10-31の台Bのように数時間つながらなくても、上限を使い切らずに2分ごとに試し続ける。
        for _ in 0..<50 {
            XCTAssertEqual(outbox.beginNext(now: time, recording: false, automatic: true)?.sessionID, "clip")
            outbox.finish(sessionID: "clip", now: time, reason: "receiver returned HTTP 503", attempts: 1, receiverOffline: true)
            XCTAssertTrue(outbox.latest?.canRetry == true)
            let next = try! XCTUnwrap(outbox.nextAttemptDate(automatic: true))
            XCTAssertLessThanOrEqual(next.timeIntervalSince(time), DebugBundleOutbox.offlineRetrySeconds)
            time = next
        }
        // 受信機に届いた失敗（400等）は従来どおり上限で止まる。
        outbox.finish(sessionID: "clip", now: time, reason: "receiver returned HTTP 400", attempts: 1)
        XCTAssertFalse(outbox.latest?.canRetry == true)
        XCTAssertTrue(DebugBundleTransfer.receiverOffline(URLError(.cannotConnectToHost)))
        XCTAssertTrue(DebugBundleTransfer.receiverOffline(URLError(.timedOut)))
        XCTAssertFalse(DebugBundleTransfer.receiverOffline(URLError(.badServerResponse)))
    }

    func testAutoClipsWaitForIdleAndRecordingCompletion() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "clip", bundlePath: "clip", now: now, deferDuringPlay: true)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true, liveTraffic: true))
        XCTAssertNil(outbox.beginNext(now: now, recording: true, automatic: true, liveTraffic: false))
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true,
                                       liveTraffic: false)?.sessionID, "clip")
    }

    func testFailureStaysPendingWithBackoffAndManualRetry() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "one", bundlePath: "bundle-one", now: now)
        var time = now
        for attempt in 1...DebugBundleOutbox.maximumAttempts {
            XCTAssertEqual(outbox.beginNext(now: time, recording: false, automatic: true)?.state, .uploading)
            XCTAssertNil(outbox.beginNext(now: time, recording: false, automatic: true), "one upload at a time")
            outbox.finish(sessionID: "one", now: time, reason: "offline", attempts: 1)
            XCTAssertEqual(outbox.pendingCount, 1, "失敗しても端末に残す")
            XCTAssertEqual(outbox.latest?.attempts, attempt)
            let delay = min(900, 30 * pow(2, Double(attempt - 1)))
            XCTAssertEqual(outbox.latest?.nextAttemptAt, time.addingTimeInterval(delay))
            XCTAssertNil(outbox.beginNext(now: time.addingTimeInterval(delay - 1), recording: false, automatic: true))
            time = time.addingTimeInterval(delay)
        }
        XCTAssertNil(outbox.beginNext(now: time.addingTimeInterval(10000), recording: false, automatic: true))
        XCTAssertNil(outbox.nextAttemptDate(automatic: true), "上限到達後はwakeも予約しない")
        XCTAssertTrue(outbox.latest?.statusText.contains("再試行上限") == true)
        let manual = outbox.retryNow()
        XCTAssertEqual(manual, ["one"])
        XCTAssertEqual(outbox.latest?.attempts, 0)
        XCTAssertEqual(outbox.beginNext(now: time, recording: false, automatic: false, manualIDs: manual)?.sessionID, "one")
    }

    func testManualClipOvertakesBacklogAndAutomaticClipsRemainFIFO() {
        var outbox = DebugBundleOutbox()
        // 再起動スキャンの列挙順でもFIFOを崩さない。
        outbox.register(sessionID: "auto-2", bundlePath: "2", now: now.addingTimeInterval(1), deferDuringPlay: true)
        outbox.register(sessionID: "auto-1", bundlePath: "1", now: now, deferDuringPlay: true)
        outbox.register(sessionID: "manual", bundlePath: "m", now: now.addingTimeInterval(100),
                        deferDuringPlay: true, manualClip: true)
        XCTAssertEqual(outbox.pendingCount, 3)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true, liveTraffic: true))
        XCTAssertNil(outbox.beginNext(now: now, recording: true, automatic: true))
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "manual")
        outbox.finish(sessionID: "manual", now: now, reason: nil, attempts: 1)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "auto-1")
        outbox.finish(sessionID: "auto-1", now: now, reason: "offline", attempts: 1)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true), "backoff中も自動clipを追い越さない")
        XCTAssertEqual(outbox.nextAttemptDate(automatic: true), now.addingTimeInterval(30))
        outbox.register(sessionID: "manual-2", bundlePath: "m2", now: now.addingTimeInterval(200),
                        deferDuringPlay: true, manualClip: true)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "manual-2")
        outbox.finish(sessionID: "manual-2", now: now, reason: nil, attempts: 1)
        XCTAssertEqual(outbox.beginNext(now: now.addingTimeInterval(30), recording: false, automatic: true)?.sessionID, "auto-1")
        outbox.finish(sessionID: "auto-1", now: now, reason: nil, attempts: 1)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "auto-2")
    }

    func testNewManualClipDoesNotWaitForOlderManualBackoff() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "old", bundlePath: "old", now: now, manualClip: true)
        outbox.finish(sessionID: "old", now: now, reason: "offline", attempts: 1)
        outbox.register(sessionID: "new", bundlePath: "new", now: now.addingTimeInterval(1), manualClip: true)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "new")
        outbox.finish(sessionID: "new", now: now, reason: nil, attempts: 1)
        XCTAssertEqual(outbox.nextAttemptDate(automatic: true), now.addingTimeInterval(30))
    }

    func testExhaustedAutomaticClipDoesNotBlockTheNextClip() {
        var outbox = DebugBundleOutbox()
        outbox.register(sessionID: "one", bundlePath: "one", now: now)
        outbox.register(sessionID: "two", bundlePath: "two", now: now.addingTimeInterval(1))
        outbox.finish(sessionID: "one", now: now, reason: "too large", attempts: DebugBundleOutbox.maximumAttempts)
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true)?.sessionID, "two")
        XCTAssertEqual(outbox.pendingCount, 2)
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
        relaunched.finish(sessionID: "one", now: now, reason: "offline", attempts: 1)
        try relaunched.save(to: url)
        let failed = try DebugBundleOutbox.load(from: url)
        XCTAssertEqual(failed.latest?.reason, "offline")
        XCTAssertEqual(failed.latest?.nextAttemptAt, now.addingTimeInterval(30))
        XCTAssertEqual(failed.latest?.attempts, 1)
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

    func testRelaunchRestoresLegacyManualClipPriorityAndScansAutoClips() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        func bundle(_ id: String, kind: String) throws -> URL {
            let url = directory.appendingPathComponent("AutoClips/phone_saber_triage_" + id)
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            let summary: [String: Any] = ["sessionID": id, "images": [],
                "autoClip": ["trigger": ["kind": kind]]]
            try JSONSerialization.data(withJSONObject: summary).write(to: url.appendingPathComponent("summary.json"))
            try Data("test".utf8).write(to: url.appendingPathComponent("prompt.md"))
            return url
        }
        _ = try bundle("automatic", kind: "jump")
        let manual = try bundle("manual", kind: "manual")
        // 新しいoptionalフィールドを持たない、旧版の永続化データ。
        var old = DebugBundleOutbox()
        old.register(sessionID: "manual", bundlePath: DebugBundleTransfer.storedPath(for: manual, in: directory), now: now)
        let encoded = try JSONEncoder().encode(old)
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("manualClip"))
        try encoded.write(to: directory.appendingPathComponent("debug-bundle-outbox.json"))
        let transfer = DebugBundleTransfer(directoryForTesting: directory)
        var recovered = transfer.outbox
        XCTAssertEqual(recovered.pendingCount, 2)
        XCTAssertTrue(recovered.entries.allSatisfy { $0.deferDuringPlay == true })
        XCTAssertEqual(recovered.beginNext(now: Date(), recording: false, automatic: true)?.sessionID, "manual")
        XCTAssertEqual(recovered.entries.first { $0.sessionID == "manual" }?.manualClip, true)
        let saved = try DebugBundleOutbox.load(from: directory.appendingPathComponent("debug-bundle-outbox.json"))
        XCTAssertEqual(saved.entries, transfer.outbox.entries)
    }

    @MainActor
    func testLiveTrafficPausesManualClipDuringPackagingWithoutNetworkIO() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let bundle = directory.appendingPathComponent("AutoClips/phone_saber_triage_manual")
        try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try Data(#"{"sessionID":"manual","selectedImageCount":0,"images":[],"autoClip":{"trigger":{"kind":"manual"}}}"#.utf8)
            .write(to: bundle.appendingPathComponent("summary.json"))
        try Data("test".utf8).write(to: bundle.appendingPathComponent("prompt.md"))
        let transfer = DebugBundleTransfer(directoryForTesting: directory)
        let paused = expectation(description: "play resumes during packaging")
        var started = false
        let observer = NotificationCenter.default.addObserver(
            forName: DebugBundleTransfer.didChange, object: transfer, queue: .main
        ) { _ in
            // recordingテストと同様、packaging前に差し込む。通信・Bonjourは行わない。
            if transfer.outbox.latest?.state == .uploading {
                started = true
                transfer.setLiveTrafficActive(true)
            } else if started && transfer.outbox.latest?.state == .pending {
                started = false
                paused.fulfill()
            }
        }
        defer { NotificationCenter.default.removeObserver(observer) }
        transfer.retryPending(immediately: true)
        wait(for: [paused], timeout: 5)
        XCTAssertEqual(transfer.outbox.latest?.state, .pending)
        XCTAssertEqual(transfer.outbox.latest?.attempts, 0)
        XCTAssertEqual(transfer.outbox.pendingCount, 1)
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
        XCTAssertEqual(logged.map { String($0.components(separatedBy: " reason=")[0]) },
                       [CoordinateDelivery.Route.lanUnverified.rawValue, CoordinateDelivery.Route.lanVerified.rawValue,
                        CoordinateDelivery.Route.lanUnverified.rawValue])
        XCTAssertTrue(logged.allSatisfy { $0.contains("reason=") })
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
        XCTAssertEqual(model.phoneIdentity.station, "none")
        XCTAssertFalse(model.phoneIdentity.manualIPOverride)
        model.setStation("A")
        XCTAssertEqual(model.host, "")
        XCTAssertTrue(model.phoneIdentity.destination.contains("送信先なし"))
        model.applyBonjourForTesting(host: "192.168.1.2", serviceName: "Phone Saber Unity B")
        XCTAssertEqual(model.host, "")
        model.applyBonjourForTesting(host: "192.168.1.3", serviceName: "Phone Saber Unity A")
        XCTAssertEqual(model.host, "192.168.1.3")
        XCTAssertEqual(model.phoneIdentity.station, "A")
        XCTAssertTrue(model.phoneIdentity.destination.contains("Phone Saber Unity A"))
        XCTAssertTrue(model.phoneIdentity.destination.contains("192.168.1.3:5005/5006"))
        model.setManualHost("192.168.1.9")
        model.setStation("B")
        model.applyBonjourForTesting(host: "192.168.1.2", serviceName: "Phone Saber Unity B")
        XCTAssertEqual(model.host, "192.168.1.9")
        XCTAssertEqual(model.transportLabel, "Manual IP")
        XCTAssertEqual(model.phoneIdentity.station, "B")
        XCTAssertTrue(model.phoneIdentity.manualIPOverride)
        XCTAssertTrue(model.phoneIdentity.destination.contains("192.168.1.9:5005/5006"))
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

    func testReleasingUDPSenderWithoutStopCancelsBothConnections() async throws {
        let red = try FakeBridge(), blue = try FakeBridge()
        defer { red.stop(); blue.stop() }
        let redPort = try await red.ready(), bluePort = try await blue.ready()
        var sender: UDPSender? = UDPSender()
        sender?.configure(host: "127.0.0.1", ports: [Int(redPort.rawValue), Int(bluePort.rawValue)])
        let connections = try XCTUnwrap(sender?.connectionsForTesting)
        defer { connections.values.forEach { $0.cancel() } }
        XCTAssertEqual(connections.count, 2)
        let ready = await waitFor {
            connections.values.allSatisfy { if case .ready = $0.state { return true }; return false }
        }
        XCTAssertTrue(ready)
        weak var released = sender
        sender = nil
        let deallocated = await waitFor { released == nil }
        XCTAssertTrue(deallocated)
        let cancelled = await waitFor {
            connections.values.allSatisfy { if case .cancelled = $0.state { return true }; return false }
        }
        XCTAssertTrue(cancelled, "所有側の破棄後に両色の LAN 接続を残さない")
    }

    func testReleasingLANProbeWithoutStopCancelsConnectionAndTimer() async throws {
        let unity = try FakeUnityDiscovery()
        defer { unity.stop() }
        let port = try await unity.ready()
        var probe: LANLivenessProbe? = LANLivenessProbe(port: port.rawValue)
        probe?.start(host: "127.0.0.1")
        let alive = await waitFor { probe?.isAlive == true }
        XCTAssertTrue(alive)
        let resources = try XCTUnwrap(probe?.resourcesForTesting)
        let connection = try XCTUnwrap(resources.connection)
        let timer = try XCTUnwrap(resources.timer)
        defer { connection.cancel(); timer.cancel() }
        weak var released = probe
        probe = nil
        let deallocated = await waitFor { released == nil }
        XCTAssertTrue(deallocated)
        XCTAssertTrue(timer.isCancelled, "所有側の破棄後に探索タイマーを残さない")
        let cancelled = await waitFor {
            if case .cancelled = connection.state { return true }
            return false
        }
        XCTAssertTrue(cancelled, "所有側の破棄後に探索・メトリクス用の LAN 接続を残さない")
    }

    func testReleasingSenderWithoutStopCancelsTheConnection() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        var sender: P2PSender? = P2PSender(timing: fastTiming(),
                                          endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        sender?.start()
        let connected = await waitFor { sender?.isUsable == true }
        XCTAssertTrue(connected)
        let connection = try XCTUnwrap(sender?.connectionForTesting)
        // 所有側が破棄されても、Network は cancel まで接続を保持する。
        defer { connection.cancel() }
        weak var released = sender
        sender = nil
        let deallocated = await waitFor { released == nil }
        XCTAssertTrue(deallocated)
        let cancelled = await waitFor {
            if case .cancelled = connection.state { return true }
            return false
        }
        XCTAssertTrue(cancelled, "破棄された sender の接続を残さない")
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

    func testReceiveErrorRebuildsP2PBeforePongTimeoutAndRecovers() async throws {
        let bridge = try FakeBridge()
        defer { bridge.stop() }
        let port = try await bridge.ready()
        var timing = fastTiming()
        timing.liveness.staleAfter = 10
        timing.liveness.reconnectAfter = 20
        timing.liveness.connectTimeout = 20
        let sender = P2PSender(timing: timing,
                               endpointOverride: .hostPort(host: "127.0.0.1", port: port))
        defer { sender.stop() }
        sender.start()
        let connected = await waitFor { sender.isUsable }
        XCTAssertTrue(connected)
        let connection = try XCTUnwrap(sender.connectionForTesting)
        let attempts = sender.connectCountForTesting
        // 実接続の cancel で受信待ちへエラーを返す。pong の期限切れを待たず復旧する。
        connection.cancel()
        let recovered = await waitFor(timeout: 2) {
            sender.connectCountForTesting > attempts && sender.isUsable
        }
        XCTAssertTrue(recovered, "受信待ちが終了した接続を使い続けず、次の接続で pong を受け取る")
        XCTAssertFalse(sender.connectionForTesting === connection)
        for port in [5005, 5006] {
            sender.send("1,2,3,4", to: port) { _ in }
        }
        let delivered = await waitFor { Set(bridge.coordinates.map(\.color)) == Set([.red, .blue]) }
        XCTAssertTrue(delivered, "受信エラーからの復旧後も両色の座標を送る")
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

    @MainActor
    func testBonjourInterfaceChangeReconfiguresSameHostButManualKeepsDefaultRoute() async throws {
        let monitor = NWPathMonitor()
        let seen = expectation(description: "interface")
        let interfaces = LockedBox<[NWInterface]>([])
        monitor.pathUpdateHandler = { path in interfaces.mutate { $0 = path.availableInterfaces }; seen.fulfill() }
        monitor.start(queue: DispatchQueue(label: "test.bonjour-interface"))
        defer { monitor.cancel() }
        await fulfillment(of: [seen], timeout: 3)
        monitor.pathUpdateHandler = nil
        guard let interface = interfaces.value.first else { throw XCTSkip("interface inventory unavailable") }
        for station in ["A", "B"] {
            let sender = UDPSender { _, _, done in done(.success(1)) }
            let p2p = P2PSender(endpointOverride: .hostPort(host: "127.0.0.1", port: 9))
            let model = CameraViewModel(sender: sender, p2pSender: p2p, p2pEnabled: false, idleTimerUpdater: { _ in })
            model.startForTesting(host: "127.0.0.1", manual: false)
            model.applyBonjourForTesting(host: "127.0.0.1", serviceName: "PC " + station)
            let first = sender.connectionGenerationForTesting(port: 5005)
            model.applyBonjourForTesting(host: "127.0.0.1", serviceName: "PC " + station, requiredInterface: interface)
            XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), first)
            XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5006), first)
            let pinned = sender.connectionGenerationForTesting(port: 5005)
            model.applyBonjourForTesting(host: "127.0.0.1", serviceName: "PC " + station, requiredInterface: interface)
            XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), pinned)
            model.stop()
            let manualSender = UDPSender { _, _, done in done(.success(1)) }
            let manual = CameraViewModel(sender: manualSender, p2pSender: p2p, p2pEnabled: false, idleTimerUpdater: { _ in })
            manual.startForTesting(host: "172.20.10.2", manual: true)
            let before = manualSender.connectionGenerationForTesting(port: 5005)
            manual.applyBonjourForTesting(host: "169.254.1.2", serviceName: "PC " + station, requiredInterface: interface)
            XCTAssertEqual(manual.host, "172.20.10.2")
            XCTAssertEqual(manualSender.connectionGenerationForTesting(port: 5005), before)
            manual.stop()
        }
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

    func testLANProbeDoesNotQueueRequestsBeforeReadyAndRecoversAfterHostChange() async throws {
        let unity = try FakeUnityDiscovery()
        defer { unity.stop() }
        let port = try await unity.ready()
        let probe = LANLivenessProbe(port: port.rawValue)
        defer { probe.stop() }
        // DNS がローカルで BadParam を返し、接続が ready にならない宛先。
        probe.start(host: "..")
        let created = await waitFor { probe.createdConnectionCountForTesting > 0 }
        XCTAssertTrue(created)
        try await Task.sleep(for: .seconds(1.1))
        XCTAssertEqual(probe.sentRequestCountForTesting, 0, "待機中に周期送信を蓄積しない")
        XCTAssertFalse(probe.isAlive)

        probe.start(host: "127.0.0.1")
        let recovered = await waitFor {
            probe.isAlive && probe.metricsRoundTrip(at: ProcessInfo.processInfo.systemUptime).count > 0
        }
        XCTAssertTrue(recovered, "到達できる宛先に変えたら探索応答を受け取る")
        XCTAssertGreaterThan(probe.sentRequestCountForTesting, 0)
        XCTAssertGreaterThan(probe.metricsRoundTrip(at: ProcessInfo.processInfo.systemUptime).count, 0)
        let sent = probe.sentRequestCountForTesting
        try await Task.sleep(for: .seconds(0.6))
        XCTAssertGreaterThan(probe.sentRequestCountForTesting, sent, "ready の間は周期送信を続ける")

        probe.stop()
        let stopped = probe.sentRequestCountForTesting
        try await Task.sleep(for: .seconds(0.6))
        XCTAssertEqual(probe.sentRequestCountForTesting, stopped)
        XCTAssertFalse(probe.isAlive)
        probe.start(host: "127.0.0.1")
        let restarted = await waitFor { probe.isAlive }
        XCTAssertTrue(restarted, "停止後も新しい接続で探索を再開できる")
        XCTAssertGreaterThan(probe.sentRequestCountForTesting, stopped)
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
/// 固定時計で実5007応答パーサーと選択ポリシーを通し、Mac/Windows両方の復旧順序を確認する。
final class LANRoutePreferenceTests: XCTestCase {
    func testBonjourRefreshCollectsAdditionalAddressesWithoutDiscardingKnownRoutes() throws {
        final class Service: NetService {
            var hosts: [String] = []
            var calls: [String] = []
            override var addresses: [Data]? {
                hosts.map { host in
                    var address = sockaddr_in()
                    address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                    address.sin_family = sa_family_t(AF_INET)
                    _ = host.withCString { inet_pton(AF_INET, $0, &address.sin_addr) }
                    return Data(bytes: &address, count: MemoryLayout<sockaddr_in>.size)
                }
            }
            override func resolve(withTimeout timeout: TimeInterval) { calls.append("resolve:\(timeout)") }
            override func stop() { calls.append("stop") }
        }
        let discovery = BonjourDiscovery(monitorRoutes: false,
            probeFactory: { LANLivenessProbe(port: 1, expectedStation: $0) }, candidateLog: { _ in })
        let service = Service(domain: "local.", type: "_phonesaber._udp.", name: "Phone Saber Unity A", port: 5005)
        service.hosts = ["192.168.1.9"]
        discovery.netServiceBrowser(NetServiceBrowser(), didFind: service, moreComing: false)
        discovery.netServiceDidResolveAddress(service)
        let wifiID = "\(service.name)|192.168.1.9%0"
        let wifi = try XCTUnwrap(discovery.routeProbesForTesting[wifiID])
        for _ in 0..<6 { discovery.routeTickForTesting() }
        XCTAssertEqual(service.calls, ["resolve:5.0", "stop", "resolve:2.0"], "3秒ごとに解決を開始し直す")
        service.hosts = ["169.254.185.161"]
        discovery.netServiceDidResolveAddress(service)
        XCTAssertEqual(discovery.routeProbesForTesting.count, 2)
        XCTAssertTrue(discovery.routeProbesForTesting[wifiID] === wifi)
        discovery.netService(service, didNotResolve: ["error": NSNumber(value: -1)])
        for _ in 0..<6 { discovery.routeTickForTesting() }
        XCTAssertEqual(service.calls.suffix(2), ["stop", "resolve:2.0"])
        XCTAssertEqual(discovery.routeProbesForTesting.count, 2, "再解決失敗でも既知アドレスを失わない")
    }

    func testUnpinnedMacUSBPromotesEvenWhenPathReportsWiFi() {
        let service = "Phone Saber Unity A"
        for usesWiFi in [false, true] {
            var policy = LANRoutePreference()
            var wifi = LANRoutePreference.Candidate(id: "wifi", service: service, host: "192.168.1.9", kind: .wifi,
                evidence: .init(first: 0, last: 0, count: 1))
            var usb = LANRoutePreference.Candidate(id: "usb", service: service, host: "169.254.185.161",
                kind: LANRoutePreference.kind(host: "169.254.185.161", type: .other, name: "",
                    usesWiFi: usesWiFi, usesWired: false))
            XCTAssertEqual(usb.kind, .wired)
            XCTAssertEqual(policy.select([wifi, usb], service: service, at: 0)?.candidate.id, "wifi")
            wifi.evidence.last = 10
            XCTAssertNil(policy.select([wifi, usb], service: service, at: 10), "無応答のUSB候補へは切り替えない")
            usb.evidence = .init(first: 10, last: 10.5, count: 2)
            XCTAssertNil(policy.select([wifi, usb], service: service, at: 10.5))
            usb.evidence = .init(first: 10, last: 11, count: 3)
            wifi.evidence.last = 11
            XCTAssertEqual(policy.select([wifi, usb], service: service, at: 11)?.candidate.id, "usb",
                           "Wi-Fi生存中も固定なしUSBが安定すれば昇格する（10-29回帰）")
            XCTAssertTrue(policy.decisionReason.contains("wired-recovered-stable"))
        }
    }

    func testBonjourLateUSBAddressFallbackAndReturnUseProductionRoutes() throws {
        var now = 0.0
        let discovery = BonjourDiscovery(monitorRoutes: false, clock: { now },
            probeFactory: { LANLivenessProbe(port: 1, expectedStation: $0, clock: { now }) }, candidateLog: { _ in })
        discovery.station = "A"
        var selected: [String] = []
        discovery.onUpdate = { if !$0.ip.isEmpty { selected.append($0.ip) } }
        let service = "Phone Saber Unity A"
        let wifiHost = "192.168.1.9", usbHost = "169.254.185.161"
        discovery.setResolvedHostsForTesting([wifiHost], service: service)
        let wifi = try XCTUnwrap(discovery.routeProbesForTesting["\(service)|\(wifiHost)%0"])
        func reply(_ probe: LANLivenessProbe) { probe.recordReplyForTesting("PHONESABER_UNITY 1 station=A") }
        reply(wifi); discovery.routeTickForTesting()
        XCTAssertEqual(selected, [wifiHost], "Wi-Fiだけなら即座に使う")
        now = 1
        discovery.setResolvedHostsForTesting([usbHost], service: service)
        let usb = try XCTUnwrap(discovery.routeProbesForTesting["\(service)|\(usbHost)%0"])
        XCTAssertTrue(discovery.routeProbesForTesting["\(service)|\(wifiHost)%0"] === wifi,
                      "追加解決で応答履歴を失わない")
        for time in [1.0, 1.5] {
            now = time; reply(wifi); reply(usb); discovery.routeTickForTesting()
            XCTAssertEqual(selected, [wifiHost])
        }
        now = 2; reply(wifi); reply(usb); discovery.routeTickForTesting()
        XCTAssertEqual(selected, [wifiHost, usbHost])
        now = 3.5; reply(wifi); discovery.routeTickForTesting()
        XCTAssertEqual(selected.last, usbHost)
        now = 3.51; discovery.routeTickForTesting()
        XCTAssertEqual(selected, [wifiHost, usbHost, wifiHost], "USB抜け後1.5秒超でWi-Fi")
        for time in [4.0, 4.5] {
            now = time; reply(wifi); reply(usb); discovery.routeTickForTesting()
            XCTAssertEqual(selected.last, wifiHost)
        }
        now = 5; reply(wifi); reply(usb); discovery.routeTickForTesting()
        XCTAssertEqual(selected, [wifiHost, usbHost, wifiHost, usbHost], "USB戻りは連続3応答で復帰")
    }

    func testBonjourUSBOnlyAndPeriodicCandidateDiagnostics() throws {
        var now = 0.0
        var logs: [String] = []
        let discovery = BonjourDiscovery(monitorRoutes: false, clock: { now },
            probeFactory: { LANLivenessProbe(port: 1, expectedStation: $0, clock: { now }) },
            candidateLog: { logs.append($0) })
        var selected = ""
        discovery.onUpdate = { selected = $0.ip }
        let service = "Phone Saber Unity A", host = "169.254.185.161"
        discovery.setResolvedHostsForTesting([host], service: service)
        let usb = try XCTUnwrap(discovery.routeProbesForTesting["\(service)|\(host)%0"])
        XCTAssertEqual(logs.count, 1)
        XCTAssertTrue(logs[0].contains("no verified candidate"))
        XCTAssertTrue(logs[0].contains("alive=false stable=false replies=0 age=none"))
        XCTAssertEqual(selected, "")
        usb.recordReplyForTesting("PHONESABER_UNITY 1")
        discovery.routeTickForTesting()
        XCTAssertEqual(selected, host, "USBのみなら初回応答で使う")
        now = 29; usb.recordReplyForTesting("PHONESABER_UNITY 1"); discovery.routeTickForTesting()
        now = 29.9; usb.recordReplyForTesting("PHONESABER_UNITY 1"); discovery.routeTickForTesting()
        XCTAssertEqual(logs.count, 1, "候補ログは30秒に1回（1 MiBのログを早く回さない）")
        now = 30; usb.recordReplyForTesting("PHONESABER_UNITY 1"); discovery.routeTickForTesting()
        XCTAssertEqual(logs.count, 2)
        for field in ["selected=\(service)|\(host)%0", "reason=keep responding wired", "resolved=\(service)=[\(host)]",
                      "interface=default kind=wired alive=true stable=true replies=3 age=0.0", "pathWifi=", "state=", "requests="] {
            XCTAssertTrue(logs[1].contains(field), "候補ログに \(field) が必要: \(logs[1])")
        }
        now = 60; discovery.routeTickForTesting()
        XCTAssertEqual(logs.count, 3)
        XCTAssertTrue(logs[2].contains("reply expired"))
        XCTAssertTrue(logs[2].contains("alive=false stable=false"))
    }

    func testMacAndWindowsPreferWiredLoseItAndRecoverWithoutFlapping() {
        for (station, wiredHost, type, interfaceName) in [
            ("A", "169.254.111.127", NWInterface.InterfaceType.wiredEthernet, "en5"),
            ("B", "172.20.10.2", .wiredEthernet, "en2"),
            ("B", "172.20.10.14", .other, "usb0"),
            ("B", "172.20.10.3", .other, "en7")
        ] {
            var now = 0.0
            let wired = LANLivenessProbe(port: 1, expectedStation: station, clock: { now })
            let wifi = LANLivenessProbe(port: 1, expectedStation: station, clock: { now })
            var policy = LANRoutePreference()
            let service = "Phone Saber Unity " + station
            let wifiHost = station == "A" ? "192.168.100.48" : wiredHost
            func reply(_ probe: LANLivenessProbe) {
                probe.recordReplyForTesting("PHONESABER_UNITY 1 red=5005 blue=5006 station=\(station)")
            }
            func choose() -> (candidate: LANRoutePreference.Candidate, reason: String)? {
                let candidates = [
                    LANRoutePreference.Candidate(id: "wifi", service: service, host: wifiHost,
                        kind: LANRoutePreference.kind(host: wifiHost, type: .wifi, name: "en0", usesWiFi: true, usesWired: false),
                        evidence: wifi.replyEvidence),
                    LANRoutePreference.Candidate(id: "usb", service: service, host: wiredHost,
                        kind: LANRoutePreference.kind(host: wiredHost, type: type, name: interfaceName, usesWiFi: false, usesWired: type == .wiredEthernet),
                        evidence: wired.replyEvidence)
                ]
                return policy.select(candidates, service: service, at: now)
            }
            reply(wired); reply(wifi)
            XCTAssertEqual(choose()?.candidate.id, "usb", "両方応答なら有線。Windowsは同じIPでもインターフェースで区別")
            for time in [0.5, 1.0, 1.5, 2.0] {
                now = time; reply(wired); reply(wifi)
                XCTAssertNil(choose(), "Wi-Fiの再解決・応答でUSBを奪わない")
            }
            now = 3.5; reply(wifi)
            XCTAssertNil(choose(), "最終応答から1.5秒までは有線を保持")
            now = 3.51
            let fallback = choose()
            XCTAssertEqual(fallback?.candidate.id, "wifi")
            XCTAssertTrue(fallback?.reason.contains("expired") == true)
            now = 4; reply(wired); reply(wifi)
            XCTAssertNil(choose(), "単発の復帰応答では切り替えない")
            now = 4.5; reply(wired); reply(wifi)
            XCTAssertNil(choose(), "2回では復帰しない")
            now = 6.1; reply(wifi)
            XCTAssertNil(choose(), "一度途切れた復帰は持ち越さない")
            for time in [6.5, 7.0] {
                now = time; reply(wired); reply(wifi)
                XCTAssertNil(choose())
            }
            now = 7.5; reply(wired); reply(wifi)
            let recovery = choose()
            XCTAssertEqual(recovery?.candidate.id, "usb")
            XCTAssertTrue(recovery?.reason.contains("wired-recovered-stable") == true)
            now = 8; reply(wired); reply(wifi)
            XCTAssertNil(choose())
            now = 10
            XCTAssertFalse(wired.isAlive); XCTAssertFalse(wifi.isAlive)
            XCTAssertNil(choose(), "両方無応答なら現在のLANは未確認となり既存CoordinateDeliveryがP2Pへ退避")
        }
    }

    func testWiFiFirstWaitsForStableUSBAndNeverMixesServiceNames() {
        var policy = LANRoutePreference()
        XCTAssertNil(policy.select([.init(id: "pending", service: "PC A", host: "172.20.10.2", kind: .wired)],
                                   service: "PC A", at: 0), "初回は別台の同一IPを未確認で採用しない")
        let name = "PC A"
        var wifi = LANRoutePreference.Candidate(id: "wifi", service: name, host: "192.168.1.2", kind: .wifi,
            evidence: .init(first: 0, last: 0, count: 1))
        var usb = LANRoutePreference.Candidate(id: "usb", service: name, host: "169.254.1.2", kind: .wired)
        let other = LANRoutePreference.Candidate(id: "other", service: "Other PC A", host: "169.254.1.3", kind: .wired,
            evidence: .init(first: 0, last: 1, count: 3))
        XCTAssertEqual(policy.select([wifi, usb, other], service: name, at: 0)?.candidate.id, "wifi")
        usb.evidence = .init(first: 0.5, last: 0.5, count: 1)
        XCTAssertNil(policy.select([wifi, usb, other], service: name, at: 0.5))
        usb.evidence = .init(first: 0.5, last: 1.5, count: 3)
        wifi.evidence.last = 1.5
        XCTAssertEqual(policy.select([other, usb, wifi], service: name, at: 1.5)?.candidate.id, "usb")
        XCTAssertFalse(PhoneSaberStation.matches(service: "PC B", station: "A"))
        XCTAssertFalse(PhoneSaberStation.matches(service: "PC A", station: "B"))
        XCTAssertTrue(LANLivenessProbe.matchesStation("PHONESABER_UNITY 1 station=B", expected: "B"))
        XCTAssertFalse(LANLivenessProbe.matchesStation("PHONESABER_UNITY 1 station=A", expected: "B"))
        var now = 0.0
        let probe = LANLivenessProbe(port: 1, expectedStation: "B", clock: { now })
        probe.recordReplyForTesting("PHONESABER_UNITY 1 station=A")
        XCTAssertFalse(probe.isAlive)
        now = 1
        probe.recordReplyForTesting("PHONESABER_UNITY 1 station=B")
        XCTAssertTrue(probe.isAlive)
    }

    func testManualIPRemainsAuthoritativeForBothStations() {
        for (service, manualIP) in [("PC A", "169.254.111.127"), ("PC B", "172.20.10.2")] {
            var selection = DestinationHostSelection()
            selection.setManual(manualIP, resolvedHost: "", serviceName: "")
            for host in ["192.168.100.48", "172.20.10.14", "169.254.1.3"] {
                XCTAssertFalse(selection.applyBonjour(host: host, serviceName: service))
                XCTAssertEqual(selection.host, manualIP)
                XCTAssertEqual(selection.source, .manual)
            }
            selection.setManual("", resolvedHost: "192.168.100.48", serviceName: service)
            XCTAssertTrue(selection.applyBonjour(host: "169.254.1.2", serviceName: service))
        }
    }

    func testInterfaceEvidenceWinsOverBothTetheringAndLinkLocalAddresses() {
        for host in ["172.20.10.2", "172.20.10.14", "169.254.1.2"] {
            XCTAssertEqual(LANRoutePreference.kind(host: host, type: .wifi, name: "en0", usesWiFi: false, usesWired: false), .wifi)
            XCTAssertEqual(LANRoutePreference.kind(host: host, type: .other, name: "usb0", usesWiFi: true, usesWired: false), .wifi)
            XCTAssertEqual(LANRoutePreference.kind(host: host, type: .other, name: "utun1", usesWiFi: false, usesWired: false), .other)
        }
        XCTAssertEqual(LANRoutePreference.kind(host: "172.20.10.2", type: .other, name: "unknown", usesWiFi: false, usesWired: false), .other)
        XCTAssertEqual(LANRoutePreference.kind(host: "172.20.10.2", type: .other, name: "", usesWiFi: true, usesWired: false), .wifi,
                       "Windowsのテザリング範囲は固定なしでもIPだけで有線にしない")
        XCTAssertEqual(LANRoutePreference.kind(host: "172.20.10.2", type: .other, name: "bridge100", usesWiFi: true, usesWired: false), .wifi)
    }

    func testBothPortsAndProbePinAndClearTheSameInterface() throws {
        let monitor = NWPathMonitor()
        let seen = expectation(description: "interface inventory")
        let interfaces = PipelineLockedValue<[NWInterface]>([])
        monitor.pathUpdateHandler = { path in interfaces.mutate { $0 = path.availableInterfaces }; seen.fulfill() }
        monitor.start(queue: DispatchQueue(label: "test.lan-interface"))
        defer { monitor.cancel() }
        wait(for: [seen], timeout: 3)
        monitor.pathUpdateHandler = nil
        guard let interface = interfaces.value.first else { throw XCTSkip("sandboxにインターフェース一覧がない") }
        if interface.type != .loopback && interface.type != .cellular &&
            !["awdl", "llw", "utun", "pdp_ip"].contains(where: { interface.name.hasPrefix($0) }) {
            let discovery = BonjourDiscovery(monitorRoutes: false,
                probeFactory: { LANLivenessProbe(port: 1, expectedStation: $0) }, candidateLog: { _ in })
            let service = "Phone Saber Unity A", host = "169.254.185.161"
            discovery.setResolvedHostsForTesting([host], service: service)
            discovery.setInterfacesForTesting([interface], source: 1)
            discovery.setInterfacesForTesting([interface], source: 0)
            XCTAssertEqual(discovery.routeProbesForTesting.count, 2, "monitorを跨ぐ同じindexは重複させない")
            discovery.setInterfacesForTesting([], source: 0)
            let id = "\(service)|\(host)%\(interface.index)"
            XCTAssertNotNil(discovery.routeProbesForTesting[id], "既定経路から消えても専用monitor側のUSBを保持")
            discovery.setInterfacesForTesting([], source: 1)
            XCTAssertNil(discovery.routeProbesForTesting[id], "全monitorから消えて無応答なら固定候補を除去")
            XCTAssertEqual(discovery.routeProbesForTesting.count, 1, "固定なしの救済候補は残す")
        }
        let sender = UDPSender()
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1", requiredInterface: interface)
        for port in [5005, 5006] {
            XCTAssertEqual(sender.connectionsForTesting[port]?.parameters.requiredInterface?.index, interface.index)
        }
        let generation = sender.connectionGenerationForTesting(port: 5005)
        sender.configure(host: "127.0.0.1", requiredInterface: interface)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generation)
        sender.updateHost("127.0.0.1")
        XCTAssertNil(sender.connectionsForTesting[5005]?.parameters.requiredInterface)
        XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), generation)
        let probe = LANLivenessProbe(port: 1)
        defer { probe.stop() }
        probe.start(host: "127.0.0.1", requiredInterface: interface)
        let deadline = Date().addingTimeInterval(2)
        while probe.resourcesForTesting.connection == nil && Date() < deadline { Thread.sleep(forTimeInterval: 0.01) }
        XCTAssertEqual(probe.resourcesForTesting.connection?.parameters.requiredInterface?.index, interface.index)
        probe.start(host: "127.0.0.1")
        XCTAssertFalse(probe.isAlive)
    }
}

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

    func testVerifiedCandidateHandoffPrefersLANImmediatelyAndEvidenceExpiresNormally() {
        var now = 10.0
        let sent = expectation(description: "confirmed Wi-Fi fallback")
        let lan = UDPSender { _, _, done in done(.success(now)); sent.fulfill() }
        let p2p = P2PSender()
        p2p.overrideUsabilityForTesting(true)
        let probe = LANLivenessProbe(port: 1, clock: { now })
        defer { lan.stop(); p2p.stop(); probe.stop() }
        let delivery = CoordinateDelivery(sender: lan, p2pSender: p2p, lanProbe: probe)
        let config = settings()
        delivery.update(config)
        lan.configure(host: "127.0.0.1")
        probe.start(host: "127.0.0.1", verifiedEvidence: .init(first: 9, last: 10, count: 3))
        delivery.route("1,2,3,4", port: 5005, settings: config, onSendStarted: nil,
                       completion: { _ in }, noRoute: { XCTFail("verified LAN available") })
        wait(for: [sent], timeout: 2)
        XCTAssertEqual(delivery.currentRoute, .lanVerified)
        now = 11.51
        XCTAssertFalse(probe.isAlive, "候補からの引継ぎでも1.5秒の失効を延ばさない")
        probe.start(host: "127.0.0.2", verifiedEvidence: .init(first: 9, last: 10, count: 3))
        XCTAssertFalse(probe.isAlive, "古い候補の証拠は引き継がない")
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
    func testFullResolutionFilesTravelInExistingEnvelopeAndRejectWrongSizes() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let full: [String: Any] = ["state": "captured", "version": 1, "pixelsPath": "fullres.bgra",
                                  "metadataPath": "fullres.jsonl", "frameBytes": 16, "frameCount": 2]
        let summary: [String: Any] = ["sessionID": "fullres_test", "selectedImageCount": 0,
                                     "images": [], "fullResolution": full]
        try JSONSerialization.data(withJSONObject: summary).write(to: root.appendingPathComponent("summary.json"))
        try Data("test".utf8).write(to: root.appendingPathComponent("prompt.md"))
        let raw = Data((0..<32).map(UInt8.init))
        try raw.write(to: root.appendingPathComponent("fullres.bgra"))
        try Data("{}\n{}\n".utf8).write(to: root.appendingPathComponent("fullres.jsonl"))
        let package = try DebugBundleTransfer.makePackage(for: root)
        defer { try? FileManager.default.removeItem(at: package) }
        let bytes = try Data(contentsOf: package)
        let length = bytes[4..<8].reduce(0) { ($0 << 8) | Int($1) }
        let manifest = try XCTUnwrap(JSONSerialization.jsonObject(with: bytes[8..<8 + length]) as? [String: Any])
        let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
        XCTAssertEqual(Set(files.compactMap { $0["path"] as? String }), ["summary.json", "prompt.md", "fullres.bgra", "fullres.jsonl"])
        XCTAssertEqual(files.first { $0["path"] as? String == "fullres.bgra" }?["contentType"] as? String, "application/octet-stream")
        XCTAssertEqual(files.first { $0["path"] as? String == "fullres.jsonl" }?["contentType"] as? String, "application/x-ndjson")
        try raw.dropLast().write(to: root.appendingPathComponent("fullres.bgra"))
        XCTAssertThrowsError(try DebugBundleTransfer.makePackage(for: root))
        try FileManager.default.removeItem(at: root.appendingPathComponent("fullres.bgra"))
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("fullres.bgra"), withDestinationURL: root.appendingPathComponent("prompt.md"))
        XCTAssertThrowsError(try DebugBundleTransfer.makePackage(for: root))
    }

    func testUploadHeadersAndStartEndLogsShareIdentityWithoutChangingBody() throws {
        let url = try XCTUnwrap(URL(string: "http://127.0.0.1:8765/v1/bundle"))
        var ids = Set<String>()
        for (index, outcome) in ["failed--1", "uploaded-201", "uploaded-200"].enumerated() {
            let trace = DebugBundleUploadTrace(sessionID: "sample_session", attempt: index + 1,
                                              destination: url, packageBytes: 1234)
            var request = URLRequest(url: url)
            request.httpBody = Data("unchanged package".utf8)
            trace.apply(to: &request)
            XCTAssertEqual(request.httpBody, Data("unchanged package".utf8))
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-PhoneSaber-Session-ID"), "sample_session")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-PhoneSaber-Transfer-ID"), trace.transferID)
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-PhoneSaber-Attempt"), String(index + 1))
            XCTAssertNotNil(UUID(uuidString: trace.transferID))
            ids.insert(trace.transferID)
            for state in ["started", outcome] {
                let line = trace.message(state, now: Date(timeIntervalSince1970: 1_000))
                for field in ["time=1970-01-01T00:16:40.000Z", "sessionID=sample_session",
                              "transferID=\(trace.transferID)", "sender-attempt=\(index + 1)",
                              "destination=\(url.absoluteString)", "package-bytes=1234", "outcome=\(state)"] {
                    XCTAssertTrue(line.contains(field), line)
                }
                XCTAssertFalse(line.contains("received-bytes"))
            }
        }
        XCTAssertEqual(ids.count, 3)
    }

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

// 実 UDPSender のキューを止め、入口での集約と復帰時の送信を観測する。
final class UDPIngressTests: XCTestCase {
    private func barrier(_ sender: UDPSender) -> DispatchSemaphore {
        let entered = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        sender.enqueueControlForTesting {
            entered.signal()
            _ = release.wait(timeout: .now() + 10)
        }
        XCTAssertEqual(entered.wait(timeout: .now() + 2), .success)
        return release
    }

    private func report(_ row: [String: Any]) {
        let data = try! JSONSerialization.data(withJSONObject: row, options: [.sortedKeys])
        print("UDP_BENCH " + String(decoding: data, as: UTF8.self))
    }

    func testPausedQueueStartsWithLatestRedAndBlue() {
        let repetitions = ProcessInfo.processInfo.environment["PHONESABER_UDP_BENCHMARK"] == "1" ? 5 : 1
        for hz in [60, 120] {
            for pauseMs in [100, 250] {
                for repetition in 0..<repetitions {
                    let count = hz * pauseMs / 1000
                    let starts = PipelineLockedValue<[(String, Int, Double)]>([])
                    let requested = PipelineLockedValue<[String: Double]>([:])
                    let reportedWait = PipelineLockedValue<[String: (Double, Int)]>([:])
                    let latestStarted = DispatchGroup()
                    latestStarted.enter(); latestStarted.enter()
                    let sender = UDPSender { text, port, complete in
                        starts.mutate { $0.append((text, port, HostMonotonicClock.now())) }
                        if text == "\(count - 1),\(port),3,4" { latestStarted.leave() }
                        complete(.success(HostMonotonicClock.now()))
                    }
                    sender.configure(host: "127.0.0.1")
                    let release = barrier(sender)
                    let beganAt = HostMonotonicClock.now()
                    // 合成入力の周期だけ短時間spinで与え、OSのtimer coalescingを避ける。
                    // 停止/解除/送信完了の判定はbarrierと通知で同期する（本番コードでは使わない）。
                    for index in 0..<count {
                        while HostMonotonicClock.now() < beganAt + Double(index) / Double(hz) {}
                        for port in [5005, 5006] {
                            let text = "\(index),\(port),3,4"
                            requested.mutate { $0[text] = HostMonotonicClock.now() }
                            sender.send(text, to: port, onSendStarted: { wait, replaced in
                                reportedWait.mutate { $0[text] = (wait, replaced) }
                            }) { _ in }
                        }
                    }
                    while HostMonotonicClock.now() < beganAt + Double(pauseMs) / 1000 {}
                    let held = sender.ingressSnapshotForTesting
                    let releasedAt = HostMonotonicClock.now()
                    release.signal()
                    XCTAssertEqual(latestStarted.wait(timeout: .now() + 2), .success)
                    // getter をバリアとして、完了通知と後続drainまで処理する。
                    _ = sender.pendingCountForTesting
                    let drained = sender.ingressSnapshotForTesting
                    XCTAssertEqual(drained.drains, 1, "解除までの要求は1予約で処理する")
                    for port in [5005, 5006] {
                        let packets = starts.value.filter { $0.1 == port }
                        let latest = "\(count - 1),\(port),3,4"
                        XCTAssertEqual(packets.first?.0, latest, "復帰時に古い先頭要求を送らない")
                        let first = packets.first!
                        let last = packets.first { $0.0 == latest }!
                        let timing = reportedWait.value[latest]!
                        XCTAssertGreaterThanOrEqual(timing.0, releasedAt - requested.value[latest]! - 0.001,
                                                    "元の要求時刻からキュー停止中の待ち時間も測る")
                        XCTAssertEqual(timing.1, 2 * (count - 1))
                        let cadenceError = (0..<count).map {
                            abs(requested.value["\($0),\(port),3,4"]! - beganAt - Double($0) / Double(hz)) * 1000
                        }.max()!
                        report(["kind": "pause", "hz": hz, "pauseMs": pauseMs, "run": repetition,
                                "port": port, "first": first.0, "expected": latest,
                                "actualPauseMs": (releasedAt - beganAt) * 1000,
                                "cadenceMaxErrorMs": cadenceError, "reportedWaitMs": timing.0 * 1000,
                                "replaced": timing.1,
                                "inputSpanMs": (requested.value[latest]! - requested.value["0,\(port),3,4"]!) * 1000,
                                "firstAgeMs": (first.2 - requested.value[first.0]!) * 1000,
                                "latestStartAfterReleaseMs": (last.2 - releasedAt) * 1000,
                                "latestWaitMs": (last.2 - requested.value[latest]!) * 1000,
                                "held": held.pending, "scheduled": held.scheduled, "drains": drained.drains])
                    }
                    XCTAssertLessThanOrEqual(held.pending, 2)
                    XCTAssertLessThanOrEqual(held.scheduled, 1)
                    sender.stop()
                }
            }
        }
    }

    func testUnloadedPayloadPortsTimingAndInlineOrOtherQueueCompletion() {
        let repetitions = ProcessInfo.processInfo.environment["PHONESABER_UDP_BENCHMARK"] == "1" ? 5 : 1
        for otherQueue in [false, true] {
            for repetition in 0..<repetitions {
                let packets = PipelineLockedValue<[(String, Int)]>([])
                let waits = PipelineLockedValue<[Double]>([])
                let successes = PipelineLockedValue(0)
                let completionQueue = DispatchQueue(label: "UDPIngressTests.completion")
                let sender = UDPSender { text, port, complete in
                    packets.mutate { $0.append((text, port)) }
                    if otherQueue { completionQueue.async { complete(.success(1)) } }
                    else { complete(.success(1)) }
                }
                sender.configure(host: "127.0.0.1")
                for index in 0..<300 {
                    let done = DispatchSemaphore(value: 0)
                    sender.send("\(index),2,3,4", to: 5005 + index % 2, onSendStarted: { wait, replaced in
                        waits.mutate { $0.append(wait * 1000) }
                        XCTAssertEqual(replaced, 0)
                    }) { result in
                        if case .success = result { successes.mutate { $0 += 1 } }
                        else { XCTFail("通常送信が失敗") }
                        done.signal()
                    }
                    XCTAssertEqual(done.wait(timeout: .now() + 2), .success)
                }
                XCTAssertEqual(successes.value, 300)
                XCTAssertEqual(packets.value.map { $0.0 }, (0..<300).map { "\($0),2,3,4" })
                XCTAssertEqual(packets.value.map { $0.1 }, (0..<300).map { 5005 + $0 % 2 })
                let values = waits.value.sorted()
                let ingress = sender.ingressSnapshotForTesting
                report(["kind": "normal", "otherQueue": otherQueue, "run": repetition, "count": values.count,
                        "p50Ms": values[values.count / 2], "p95Ms": values[Int(Double(values.count) * 0.95)],
                        "maxMs": values.last!, "heldMax": ingress.maximumPending,
                        "scheduledMax": ingress.maximumScheduled, "drains": ingress.drains])
                sender.stop()
            }
        }
    }

    func testLargeBurstIsBoundedAndReplacementsAreNotSuccesses() {
        let packets = PipelineLockedValue<[(String, Int)]>([])
        let successes = PipelineLockedValue(0)
        let replacements = PipelineLockedValue<[Int]>([])
        let sender = UDPSender { text, port, complete in
            packets.mutate { $0.append((text, port)) }
            complete(.success(1))
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        let release = barrier(sender)
        for index in 0..<20_000 {
            sender.send("\(index),2,3,4", to: 5005 + index % 2, onSendStarted: { _, replaced in
                replacements.mutate { $0.append(replaced) }
            }) { _ in successes.mutate { $0 += 1 } }
        }
        let held = sender.ingressSnapshotForTesting
        release.signal()
        _ = sender.pendingCountForTesting
        _ = sender.pendingCountForTesting
        XCTAssertEqual(held.pending, 2)
        XCTAssertEqual(held.scheduled, 1)
        XCTAssertEqual(sender.ingressSnapshotForTesting.drains, 1)
        XCTAssertEqual(packets.value.map { $0.0 }.sorted(), ["19998,2,3,4", "19999,2,3,4"])
        XCTAssertEqual(successes.value, 2)
        XCTAssertEqual(sender.supersededPendingCountForTesting, 19_998)
        XCTAssertEqual(replacements.value, [19_998, 19_998])
    }
}

extension UDPIngressTests {
    func testContinuousRedInputLetsBlueAndControlWorkProceed() {
        let running = PipelineLockedValue(true)
        let inputCount = PipelineLockedValue(0)
        let blue = DispatchSemaphore(value: 0)
        let producerStarted = DispatchSemaphore(value: 0)
        let producerFinished = DispatchSemaphore(value: 0)
        let control = DispatchSemaphore(value: 0)
        let sender = UDPSender { _, port, complete in
            if port == 5006 { blue.signal() }
            complete(.success(1))
        }
        sender.configure(host: "127.0.0.1")
        let release = barrier(sender)
        DispatchQueue.global(qos: .userInteractive).async {
            sender.send("0,2,3,4", to: 5005) { _ in }
            producerStarted.signal()
            while running.value {
                inputCount.mutate { $0 += 1 }
                sender.send("\(inputCount.value),2,3,4", to: 5005) { _ in }
            }
            producerFinished.signal()
        }
        XCTAssertEqual(producerStarted.wait(timeout: .now() + 2), .success)
        sender.send("5,6,7,8", to: 5006) { _ in }
        sender.snapshot { _ in
            XCTAssertTrue(running.value)
            control.signal()
        }
        release.signal()
        XCTAssertEqual(blue.wait(timeout: .now() + 2), .success)
        XCTAssertEqual(control.wait(timeout: .now() + 2), .success)
        // 連続入力中でも停止処理がキューを取得できる。
        sender.stop()
        running.mutate { $0 = false }
        XCTAssertEqual(producerFinished.wait(timeout: .now() + 2), .success)
        let snapshot = sender.ingressSnapshotForTesting
        XCTAssertLessThanOrEqual(snapshot.maximumPending, 2)
        XCTAssertLessThanOrEqual(snapshot.maximumScheduled, 1)
    }

    func testQueuedCoordinatesRevalidateProcessorAndLifecycleGenerations() {
        for lifecycle in [false, true] {
            let sender = UDPSender { _, _, _ in XCTFail("旧世代の送信") }
            let p2p = P2PSender()
            let probe = LANLivenessProbe()
            defer { sender.stop(); p2p.stop(); probe.stop() }
            sender.configure(host: "127.0.0.1")
            let delivery = CoordinateDelivery(sender: sender, p2pSender: p2p, lanProbe: probe)
            var config = CoordinateDelivery.Settings()
            config.running = true; config.lanConfigured = true; config.p2pEnabled = false
            config.processorGeneration = 7; config.lifecycleGeneration = 3
            delivery.update(config)
            let release = barrier(sender)
            for port in [5005, 5006] {
                delivery.route("1,2,3,4", port: port, settings: config, onSendStarted: nil,
                               completion: { _ in XCTFail("旧世代の完了通知") }, noRoute: { XCTFail("経路あり") })
            }
            if lifecycle { config.lifecycleGeneration += 1 } else { config.processorGeneration += 1 }
            delivery.update(config)
            release.signal()
            XCTAssertEqual(sender.pendingCountForTesting, 0)
        }
    }

    func testWaitingStopAndDestinationChangeDropCoordinatesButSameConfigurationKeepsLatest() {
        for action in ["stop", "destination", "same"] {
            let packets = PipelineLockedValue<[String]>([])
            let sender = UDPSender { text, _, complete in
                packets.mutate { $0.append(text) }
                complete(.success(1))
            }
            defer { sender.stop() }
            sender.configure(host: "127.0.0.1")
            sender.simulateConnectionStateForTesting(port: 5005, state: .waiting)
            _ = sender.pendingCountForTesting
            let release = barrier(sender)
            sender.send("1,2,3,4", to: 5005) { _ in XCTFail("置換済み") }
            sender.send("5,6,7,8", to: 5005) { _ in }
            release.signal()
            XCTAssertEqual(sender.pendingCountForTesting, 1)
            let generation = sender.connectionGenerationForTesting(port: 5005)
            if action == "stop" { sender.stop() }
            sender.configure(host: action == "destination" ? "127.0.0.2" : "127.0.0.1")
            sender.simulateConnectionStateForTesting(port: 5005, state: .ready)
            XCTAssertEqual(sender.pendingCountForTesting, 0)
            if action == "same" {
                XCTAssertEqual(packets.value, ["5,6,7,8"])
                XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generation)
            } else {
                XCTAssertTrue(packets.value.isEmpty)
                XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), generation)
            }
        }
    }

    func testP2PSwitchDropsIngressBeforeReservedDrainAndPreservesNewFallback() {
        let packets = PipelineLockedValue<[String]>([])
        let sender = UDPSender { text, _, complete in
            packets.mutate { $0.append(text) }
            complete(.success(1))
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        let release = barrier(sender)
        sender.send("1,2,3,4", to: 5005) { _ in XCTFail("P2P切替前のLAN待機") }
        sender.discardPendingCoordinates()
        sender.send("5,6,7,8", to: 5005) { _ in }
        release.signal()
        _ = sender.pendingCountForTesting
        _ = sender.pendingCountForTesting
        XCTAssertEqual(packets.value, ["5,6,7,8"])
        XCTAssertEqual(sender.discardedForP2PCountForTesting, 1)
    }

    func testP2PSwitchAlsoDropsActiveMovedToRecoveryByEarlierQueuedFailure() {
        let packets = PipelineLockedValue<[String]>([])
        let callback = PipelineLockedValue<((Result<TimeInterval, Error>) -> Void)?>(nil)
        let sender = UDPSender(sendHandler: { text, _, complete in
            packets.mutate { $0.append(text) }
            callback.mutate { $0 = complete }
        }, timing: UDPSender.Timing(reconnectInitialDelay: 0.01, reconnectMaximumDelay: 0.01))
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        sender.send("1,2,3,4", to: 5005) { _ in }
        _ = sender.pendingCountForTesting
        let release = barrier(sender)
        // failed は discard の同期破棄より後に実行され、active を待機へ戻す。
        sender.simulateConnectionStateForTesting(port: 5005, state: .failed)
        sender.simulateConnectionStateForTesting(port: 5005, state: .ready)
        sender.discardPendingCoordinates()
        release.signal()
        XCTAssertEqual(sender.pendingCountForTesting, 0)
        let ready = expectation(description: "再接続がreadyになる")
        var readyNotifications = 0
        let beforeRecovery = sender.connectionGenerationForTesting(port: 5005)
        sender.configure(host: "127.0.0.1") { states, _, _ in
            if states[5005]?.hasPrefix("ready") == true {
                readyNotifications += 1
                if readyNotifications == 2 { ready.fulfill() }
            }
        }
        wait(for: [ready], timeout: 2)
        XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), beforeRecovery)
        callback.value?(.success(1))
        XCTAssertEqual(sender.rejectedCompletionCountForTesting, 1)
        XCTAssertEqual(packets.value, ["1,2,3,4"], "一度開始済みの座標を復帰後に再送しない")
    }

    func testCallbacksAndIsCurrentCanReadIngressWithoutHoldingItsLock() {
        let done = expectation(description: "再入可能なコールバック")
        done.expectedFulfillmentCount = 2
        var sender: UDPSender!
        sender = UDPSender { _, _, complete in
            _ = sender.ingressSnapshotForTesting
            complete(.success(1))
        }
        defer { sender.stop(); sender = nil }
        sender.configure(host: "127.0.0.1")
        sender.send("1,2,3,4", to: 5005, onSendStarted: { _, _ in
            _ = sender.ingressSnapshotForTesting
            sender.send("5,6,7,8", to: 5006) { _ in done.fulfill() }
        }, isCurrent: {
            _ = sender.ingressSnapshotForTesting
            return true
        }) { _ in
            _ = sender.ingressSnapshotForTesting
            done.fulfill()
        }
        wait(for: [done], timeout: 2)
    }
}

extension UDPIngressTests {
    func testP2PSwitchDuringIsCurrentDoesNotSendTakenOldRequest() {
        let checking = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        let latest = expectation(description: "P2P切替後の新しいLANフォールバック")
        let packets = PipelineLockedValue<[String]>([])
        let sender = UDPSender { text, _, complete in
            packets.mutate { $0.append(text) }
            complete(.success(1))
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        sender.send("1,2,3,4", to: 5005, isCurrent: {
            checking.signal()
            _ = release.wait(timeout: .now() + 2)
            return true
        }) { _ in XCTFail("取り出し済みでもP2P切替前の座標を送らない") }
        XCTAssertEqual(checking.wait(timeout: .now() + 2), .success)
        sender.discardPendingCoordinates()
        sender.send("5,6,7,8", to: 5005) { _ in latest.fulfill() }
        release.signal()
        wait(for: [latest], timeout: 2)
        XCTAssertEqual(packets.value, ["5,6,7,8"])
        XCTAssertEqual(sender.discardedForP2PCountForTesting, 1)
    }

    func testWatchdogRecoversLatestIngressAndRejectsOldCompletion() {
        let packets = PipelineLockedValue<[String]>([])
        let callbacks = PipelineLockedValue<[(Result<TimeInterval, Error>) -> Void]>([])
        let started = expectation(description: "最初の座標")
        let recovered = expectation(description: "watchdog復帰後の最新座標")
        let completed = expectation(description: "最新座標の成功通知")
        let timing = UDPSender.Timing(sendWatchdogTimeout: 0.05,
                                      reconnectInitialDelay: 0.01, reconnectMaximumDelay: 0.01)
        let sender = UDPSender(sendHandler: { text, _, complete in
            packets.mutate { $0.append(text) }
            callbacks.mutate { $0.append(complete) }
            if text == "1,2,3,4" { started.fulfill() } else { recovered.fulfill() }
        }, timing: timing)
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        let blueGeneration = sender.connectionGenerationForTesting(port: 5006)
        sender.send("1,2,3,4", to: 5005) { _ in XCTFail("旧送信の成功通知は無効") }
        wait(for: [started], timeout: 2)
        let release = barrier(sender)
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("置換済み") }
        sender.send("9,10,11,12", to: 5005, onSendStarted: { wait, replaced in
            XCTAssertGreaterThan(wait, 0.01, "要求元の時刻から、watchdog復帰までの待機を測る")
            XCTAssertEqual(replaced, 1)
        }) { _ in completed.fulfill() }
        release.signal()
        wait(for: [recovered], timeout: 2)
        XCTAssertEqual(packets.value, ["1,2,3,4", "9,10,11,12"])
        XCTAssertEqual(sender.watchdogTimeoutCountForTesting(port: 5005), 1)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5006), blueGeneration)
        callbacks.value[0](.success(1))
        XCTAssertEqual(sender.rejectedCompletionCountForTesting, 1)
        callbacks.value[1](.success(2))
        wait(for: [completed], timeout: 2)
    }
}

extension UDPIngressTests {
    func testNewDestinationDropsActiveAndPendingAndRejectsOldCompletion() {
        let callbacks = PipelineLockedValue<[(Result<TimeInterval, Error>) -> Void]>([])
        let packets = PipelineLockedValue<[String]>([])
        let started = expectation(description: "旧宛先の送信開始")
        let latest = expectation(description: "新宛先の送信開始")
        let completed = expectation(description: "新宛先の成功通知")
        let sender = UDPSender { text, _, complete in
            packets.mutate { $0.append(text) }
            callbacks.mutate { $0.append(complete) }
            if text == "1,2,3,4" { started.fulfill() } else { latest.fulfill() }
        }
        defer { sender.stop() }
        sender.configure(host: "127.0.0.1")
        sender.send("1,2,3,4", to: 5005) { _ in XCTFail("旧宛先の完了通知") }
        wait(for: [started], timeout: 2)
        let release = barrier(sender)
        sender.send("5,6,7,8", to: 5005) { _ in XCTFail("旧宛先の待機座標") }
        release.signal()
        XCTAssertEqual(sender.pendingCountForTesting, 1)
        sender.configure(host: "127.0.0.2")
        sender.send("9,10,11,12", to: 5005) { _ in completed.fulfill() }
        wait(for: [latest], timeout: 2)
        callbacks.value[0](.failure(NWError.posix(.ENETDOWN)))
        XCTAssertEqual(sender.rejectedCompletionCountForTesting, 1)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 0)
        callbacks.value[1](.success(1))
        wait(for: [completed], timeout: 2)
        XCTAssertEqual(packets.value, ["1,2,3,4", "9,10,11,12"])
    }
}
