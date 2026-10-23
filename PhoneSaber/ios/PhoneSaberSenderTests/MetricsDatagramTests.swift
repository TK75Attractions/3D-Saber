import Foundation
import XCTest
#if !METRICS_STANDALONE
@testable import PhoneSaberSender
#endif

final class MetricsDatagramTests: XCTestCase {
    func testShutterISOAndEmitterCommandRules() {
        for value in [2000, 250, 200, 160, 125, 100, 80, 64] {
            let line = "PHONESABER_COMMAND 1 id=72 shutter-iso=\(value) future=x"
            XCTAssertEqual(LANLivenessProbe.parseCommand(line)?.shutterISO, value)
            XCTAssertEqual(LANLivenessProbe.formatAck(id: 72, station: "A", platform: "ios", result: "shutter-iso", detail: String(value)),
                           "PHONESABER_ACK 1 id=72 station=A platform=ios result=shutter-iso detail=\(value)")
            for other in ["shutter=2000", "shutter-emitter=1", "ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1",
                          "exposure-bias=-2", "clip=1", "auto-clip=0", "track-lock=1", "defaults=1"] {
                XCTAssertNil(LANLivenessProbe.parseCommand(line + " " + other))
            }
        }
        for value in ["0", "-1", "1000", "500", "63", "4000", "64.0", "x"] {
            XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=72 shutter-iso=\(value)"))
        }
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 shutter-iso=125"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 2 id=72 shutter-iso=125"))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=72 shutter-iso=125 shutter-iso=250")?.shutterISO, 250)
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=72 shutter-iso=125 shutter-iso=x"))
        XCTAssertNotNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=72 shutter-iso=x ignore=red"))
        for value in [0, 1] {
            let line = "PHONESABER_COMMAND 1 id=73 shutter-emitter=\(value)"
            XCTAssertEqual(LANLivenessProbe.parseCommand(line)?.shutterEmitter, value == 1)
            for other in ["shutter=2000", "shutter-iso=125", "defaults=1", "ignore=both", "clip=1"] {
                XCTAssertNil(LANLivenessProbe.parseCommand(line + " " + other))
            }
        }
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=73 shutter-emitter=on"))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=73 shutter-emitter=1 shutter-emitter=0")?.shutterEmitter, false)
    }

    func testIPhoneShutterParsingAndAck() {
        for value in [0, 500, 1000, 2000] {
            let line = "PHONESABER_COMMAND 1 id=71 shutter=\(value) future=x"
            XCTAssertEqual(LANLivenessProbe.parseCommand(line)?.shutter, value)
            let detail = value == 0 ? "off" : String(value)
            XCTAssertEqual(LANLivenessProbe.formatAck(id: 71, station: "B", platform: "ios", result: "shutter", detail: detail),
                "PHONESABER_ACK 1 id=71 station=B platform=ios result=shutter detail=\(detail)")
            for other in ["ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1", "exposure-bias=-2",
                          "clip=1", "auto-clip=0", "track-lock=1", "defaults=1"] {
                XCTAssertNil(LANLivenessProbe.parseCommand(line + " " + other))
            }
        }
        for value in ["-1", "1", "250", "4000", "1000.0", "x"] {
            XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=71 shutter=\(value)"))
        }
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 shutter=500"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 2 id=71 shutter=500"))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=71 shutter=500 shutter=2000")?.shutter, 2000)
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=71 shutter=500 shutter=x"))
        XCTAssertNotNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=71 shutter=x ignore=red"))
    }

    // Android の IgnoreZonesTest と同じ例。
    func testAckFormatMatchesAndroid() {
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 7, station: "A", platform: "ios", result: "ignored", detail: "red1-blue1"),
                       "PHONESABER_ACK 1 id=7 station=A platform=ios result=ignored detail=red1-blue1")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 8, station: "", platform: "android", result: "nothing", detail: "red0-blue0"),
                       "PHONESABER_ACK 1 id=8 station=none platform=android result=nothing detail=red0-blue0")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 9, station: "B", platform: "ios", result: "cleared", detail: "ignore"),
                       "PHONESABER_ACK 1 id=9 station=B platform=ios result=cleared detail=ignore")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 10, station: "A", platform: "android", result: "learning", detail: "10s"),
                       "PHONESABER_ACK 1 id=10 station=A platform=android result=learning detail=10s")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 10, station: "A", platform: "android", result: "learned", detail: "samples42"),
                       "PHONESABER_ACK 1 id=10 station=A platform=android result=learned detail=samples42")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 11, station: "A", platform: "ios", result: "failed", detail: "samples2"),
                       "PHONESABER_ACK 1 id=11 station=A platform=ios result=failed detail=samples2")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 12, station: "B", platform: "android", result: "cleared", detail: "area"),
                       "PHONESABER_ACK 1 id=12 station=B platform=android result=cleared detail=area")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 13, station: "A", platform: "ios", result: "exposure", detail: "ev-2"),
                       "PHONESABER_ACK 1 id=13 station=A platform=ios result=exposure detail=ev-2")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 14, station: "B", platform: "android", result: "tracklock", detail: "off"),
                       "PHONESABER_ACK 1 id=14 station=B platform=android result=tracklock detail=off")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 15, station: "A", platform: "ios", result: "clip-saved", detail: "saved"),
                       "PHONESABER_ACK 1 id=15 station=A platform=ios result=clip-saved detail=saved")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 16, station: "A", platform: "android", result: "clip-off", detail: "off"),
                       "PHONESABER_ACK 1 id=16 station=A platform=android result=clip-off detail=off")
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 17, station: "B", platform: "ios", result: "autoclip", detail: "on"),
                       "PHONESABER_ACK 1 id=17 station=B platform=ios result=autoclip detail=on")
        for atom in ["", "red blue", "赤", "x\n", "x=y", String(repeating: "x", count: 200)] {
            XCTAssertNil(LANLivenessProbe.formatAck(id: 1, station: "A", platform: "ios", result: "ignored", detail: atom))
        }
        XCTAssertNil(LANLivenessProbe.formatAck(id: -1, station: "A", platform: "ios", result: "cleared", detail: "ignore"))
        XCTAssertNil(LANLivenessProbe.formatAck(id: 1, station: "A", platform: "other", result: "cleared", detail: "ignore"))
    }

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
            m.controls = v["ctl"]
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
    func testCurrentControlsFitMetricsAndUseStableChoiceIndices() {
        for station in ["A", "B"] {
            var m = MetricsDatagram()
            m.station = station; m.app = String(repeating: "x", count: 24)
            m.fpsMode = 240; m.captureFPS = 1000; m.redFPS = 1000; m.blueFPS = 1000
            m.median = 2000; m.p95 = 2000; m.samples = 2400
            m.rtt = 1000; m.rttAge = 10; m.rttSamples = 2400
            m.thermal = "critical"; m.battery = 100
            m.controls = MetricsDatagram.controlState(trackLock: false, exposureBias: 0, shutter: 1000, shutterISO: 200)
            XCTAssertEqual(m.controls, "0022")
            XCTAssertTrue(m.line()?.hasSuffix(" ctl=0022") == true)
            XCTAssertLessThan(m.line()!.utf8.count, 400)
        }
        for (index, iso) in [2000, 250, 200, 160, 125, 100, 80, 64].enumerated() {
            XCTAssertEqual(MetricsDatagram.controlState(trackLock: true, exposureBias: -2, shutter: 2000, shutterISO: iso), "123\(index)")
        }
        for removed in [1000, 500] {
            XCTAssertNil(MetricsDatagram.controlState(trackLock: true, exposureBias: -2, shutter: 2000, shutterISO: removed))
        }
        XCTAssertNil(MetricsDatagram.controlState(trackLock: true, exposureBias: -3, shutter: 2000, shutterISO: 2000))
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

    // Mac の F8 から届く指示（2026-10-18）。Unity の PhoneSaberDiscoveryResponder.BuildCommand と同じ形式。
    func testPCCommandParsingDefaults() {
        typealias C = LANLivenessProbe.PCCommand
        func parse(_ text: String) -> C? { LANLivenessProbe.parseCommand(text) }
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=81 defaults=1 x=ignored"),
                       C(id: 81, ignore: nil, clearIgnore: false, defaults: true))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=81 defaults=0 defaults=1")?.defaults, true)
        for text in ["PHONESABER_COMMAND 2 id=1 defaults=1", "PHONESABER_COMMAND 1 defaults=1",
                     "PHONESABER_COMMAND 1 id=x defaults=1", "PHONESABER_COMMAND 1 id=1 defaults=0",
                     "PHONESABER_COMMAND 1 id=1 defaults=2", "PHONESABER_COMMAND 1 id=1 defaults=true",
                     "PHONESABER_COMMAND 1 id=1 defaults=01", "PHONESABER_COMMAND 1 id=1 defaults=1 defaults=bad"] {
            XCTAssertNil(parse(text), text)
        }
        for other in ["ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1", "exposure-bias=-1",
                      "track-lock=0", "clip=1", "auto-clip=0"] {
            XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 defaults=1 " + other), other)
        }
        XCTAssertEqual(LANLivenessProbe.formatAck(id: 81, station: "", platform: "ios", result: "defaults", detail: "tracklock-on-ev-2"),
                       "PHONESABER_ACK 1 id=81 station=none platform=ios result=defaults detail=tracklock-on-ev-2")
        for station in ["A", "B"] {
            XCTAssertEqual(LANLivenessProbe.formatAck(id: 81, station: station, platform: "ios", result: "defaults", detail: "tracklock-on-ev-2-shutter-2000"),
                           "PHONESABER_ACK 1 id=81 station=\(station) platform=ios result=defaults detail=tracklock-on-ev-2-shutter-2000")
        }
    }

    func testPCCommandParsingTrackLock() {
        typealias C = LANLivenessProbe.PCCommand
        func parse(_ text: String) -> C? { LANLivenessProbe.parseCommand(text) }
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=71 track-lock=1 x=ignored"),
                       C(id: 71, ignore: nil, clearIgnore: false, trackLock: true))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=72 track-lock=0")?.trackLock, false)
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=73 track-lock=1 track-lock=0")?.trackLock, false)
        for text in ["PHONESABER_COMMAND 2 id=1 track-lock=1", "PHONESABER_COMMAND 1 track-lock=1",
                     "PHONESABER_COMMAND 1 id=x track-lock=1", "PHONESABER_COMMAND 1 id=1 track-lock=2",
                     "PHONESABER_COMMAND 1 id=1 track-lock=true", "PHONESABER_COMMAND 1 id=1 track-lock=01",
                     "PHONESABER_COMMAND 1 id=1 track-lock=1 track-lock=bad"] {
            XCTAssertNil(parse(text), text)
        }
        for other in ["ignore=red", "clear-ignore=1", "learn-area=10", "clear-area=1", "exposure-bias=-1", "clip=1", "auto-clip=0"] {
            XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 track-lock=0 " + other), other)
        }
    }

    func testPCCommandParsing() {
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=7 ignore=red"),
                       LANLivenessProbe.PCCommand(id: 7, ignore: "red", clearIgnore: false))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=8 ignore=blue"),
                       LANLivenessProbe.PCCommand(id: 8, ignore: "blue", clearIgnore: false))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=9 clear-ignore=1"),
                       LANLivenessProbe.PCCommand(id: 9, ignore: nil, clearIgnore: true))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 ignore=red"))       // id なし
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=3 ignore=green")) // 知らない色
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=11 ignore=both")?.ignore, "both") // 赤と青をまとめて
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_UNITY 1 red=5005 blue=5006 name=Mac"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 2 id=1 ignore=red"))  // 別の版
        // 古い組み合わせ（ignore と clear-ignore）の受け付けは変えない。
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=6 clear-ignore=1 ignore=blue"),
                       LANLivenessProbe.PCCommand(id: 6, ignore: "blue", clearIgnore: true))
    }

    // プレイ範囲・露出補正（2026-10-18）。Android の PlayAreaTest と同じ例。
    func testPCCommandParsingPlayAreaAndExposure() {
        typealias C = LANLivenessProbe.PCCommand
        func parse(_ text: String) -> C? { LANLivenessProbe.parseCommand(text) }
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=11 learn-area=10"), C(id: 11, ignore: nil, clearIgnore: false, learnAreaSeconds: 10))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=12 clear-area=1"), C(id: 12, ignore: nil, clearIgnore: false, clearArea: true))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=13 exposure-bias=-1"), C(id: 13, ignore: nil, clearIgnore: false, exposureBias: -1))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=14 exposure-bias=-2"), C(id: 14, ignore: nil, clearIgnore: false, exposureBias: -2))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=15 exposure-bias=0"), C(id: 15, ignore: nil, clearIgnore: false, exposureBias: 0))
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=16 x=1 learn-area=5"), C(id: 16, ignore: nil, clearIgnore: false, learnAreaSeconds: 5))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 learn-area=10"))         // id なし
        XCTAssertNil(parse("PHONESABER_COMMAND 2 id=1 learn-area=10"))    // 別の版
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 learn-area=0"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 learn-area=61"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 learn-area=x"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 clear-area=0"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-3"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 exposure-bias=1"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1.5"))
        // 1つの指示は1つの操作だけ。
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 learn-area=10 clear-area=1"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1 ignore=red"))
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 clear-ignore=1 learn-area=10"))
        // 同じ項目は後のものが有効。
        XCTAssertEqual(parse("PHONESABER_COMMAND 1 id=1 exposure-bias=-1 exposure-bias=-2")?.exposureBias, -2)
        XCTAssertNil(parse("PHONESABER_COMMAND 1 id=1 learn-area=10 learn-area=x"))
    }
}
