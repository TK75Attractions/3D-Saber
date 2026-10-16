import Foundation
import XCTest
#if !METRICS_STANDALONE
@testable import PhoneSaberSender
#endif

final class MetricsDatagramTests: XCTestCase {
    func testSharedFormatVectors() throws {
        var root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while !FileManager.default.fileExists(atPath: root.appendingPathComponent("PhoneSaber/tools/fixtures/protocol_metrics.tsv").path) && root.path != "/" {
            root.deleteLastPathComponent()
        }
        let fixture = Bundle(for: MetricsDatagramTests.self).url(forResource: "protocol_metrics", withExtension: "tsv")
            ?? root.appendingPathComponent("PhoneSaber/tools/fixtures/protocol_metrics.tsv")
        let text = try String(contentsOf: fixture, encoding: .utf8)
        for row in text.split(separator: "\n").dropFirst() {
            let f = row.split(separator: "\t").map(String.init)
            guard f[3] == "1" else { continue }
            let pairs = f[1].split(separator: " ").dropFirst(2).map { $0.split(separator: "=", maxSplits: 1).map(String.init) }
            let v = Dictionary(uniqueKeysWithValues: pairs.map { ($0[0], $0[1]) })
            var m = MetricsDatagram()
            m.station = v["station"]!; m.platform = v["platform"]!; m.app = v["app"]!
            m.fpsMode = Double(v["fps-mode"]!)!; m.captureFPS = Double(v["capture-fps"]!)
            m.redFPS = Double(v["send-red-fps"]!)!; m.blueFPS = Double(v["send-blue-fps"]!)!
            m.median = Double(v["c2s-median-ms"]!); m.p95 = Double(v["c2s-p95-ms"]!); m.samples = Int(v["c2s-n"]!)!
            m.rtt = Double(v["rtt-ms"]!); m.rttSamples = Int(v["rtt-n"]!)!; m.rttAge = Double(v["rtt-age-s"]!)
            m.thermal = v["thermal"]!; m.battery = Double(v["battery"]!); m.c2sEnd = v["c2s-end"]!
            XCTAssertEqual(m.line(), f[1], f[0])
        }
    }
    func testRttIgnoresLossDuplicateAndStaleSamples() {
        var meter = ProbeRoundTrip()
        meter.sent(at: 0); meter.reply(at: 1) // lost/late
        XCTAssertEqual(meter.snapshot(at: 1).count, 0)
        meter.sent(at: 2); meter.reply(at: 2.01); meter.reply(at: 2.02)
        meter.sent(at: 3); meter.reply(at: 3.03)
        XCTAssertEqual(meter.snapshot(at: 3.1).median!, 20, accuracy: 1e-9)
        XCTAssertEqual(meter.snapshot(at: 3.1).count, 2)
        XCTAssertEqual(meter.snapshot(at: 14).count, 0)
        for i in 0..<100 { meter.sent(at: 20 + Double(i) / 100); meter.reply(at: 20.001 + Double(i) / 100) }
        XCTAssertEqual(meter.snapshot(at: 21).count, 16)
    }
    func testRttSnapshotExcludesRepliesNewerThanTheRequestedTime() {
        var meter = ProbeRoundTrip()
        meter.sent(at: 10); meter.reply(at: 10.01)
        // 集計側が時刻を取得してから lock を取る間に、探索キューへ次の応答が届く。
        let snapshotTime = 10.02
        meter.sent(at: 10.02); meter.reply(at: 10.05)
        let snapshot = meter.snapshot(at: snapshotTime)
        XCTAssertEqual(snapshot.count, 1)
        XCTAssertEqual(snapshot.median!, 10, accuracy: 1e-9)
        XCTAssertEqual(snapshot.age!, 0.01, accuracy: 1e-9)
        var metrics = MetricsDatagram()
        metrics.rtt = snapshot.median; metrics.rttSamples = snapshot.count; metrics.rttAge = snapshot.age
        XCTAssertTrue(metrics.line()!.contains("rtt-ms=10.0 rtt-n=1 rtt-age-s=0.0"))

        let beforeFirstReply = meter.snapshot(at: 10)
        XCTAssertEqual(beforeFirstReply.count, 0)
        XCTAssertNil(beforeFirstReply.median)
        XCTAssertNil(beforeFirstReply.age)
        // 将来の集計では除外した応答も使え、10秒ちょうどの標本も残る。
        XCTAssertEqual(meter.snapshot(at: 10.05).count, 2)
        XCTAssertEqual(meter.snapshot(at: 20.05).count, 1)
        XCTAssertEqual(meter.snapshot(at: 20.051).count, 0)
    }
    func testCadenceAndUntrustedValues() {
        var cadence = MetricsCadence()
        XCTAssertFalse(cadence.due(at: 0, sending: true)); XCTAssertFalse(cadence.due(at: 4.99, sending: true))
        XCTAssertTrue(cadence.due(at: 5, sending: true)); XCTAssertFalse(cadence.due(at: 5, sending: true))
        XCTAssertFalse(cadence.due(at: 10, sending: false)); XCTAssertFalse(cadence.due(at: 11, sending: true))
        var m = MetricsDatagram(); m.station = "A\n台"; m.app = String(repeating: "x", count: 100)
        m.median = .nan; m.p95 = .infinity; m.samples = 20
        XCTAssertTrue(m.line()!.contains("c2s-n=0")); XCTAssertLessThan(m.line()!.utf8.count, 400)
        m.fpsMode = .nan; XCTAssertNil(m.line())
        m.fpsMode = 60; m.median = 2.25; m.p95 = 2.25
        XCTAssertTrue(m.line()!.contains("c2s-median-ms=2.3"))
    }
}
