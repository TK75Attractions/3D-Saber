// 保存済みeligibleの幾何と得点を本番SaberTrackLockへ渡す。画像認識の代用にはしない。
import Foundation

var selectionReplayPath = "acquisition"
var selectionReplayExcluded: [SaberCandidate] = []

func replayCandidate(_ row: [Double], sources: [String] = []) -> SaberCandidate {
    let a = PixelPoint(x: Int(row[1]), y: Int(row[2]))
    let b = PixelPoint(x: Int(row[3]), y: Int(row[4]))
    let source = sources.indices.contains(Int(row[11])) ? sources[Int(row[11])] : "color-mask"
    return SaberCandidate(source: source, comparisonEndpoints: (a, b), endpoints: (a, b),
        boundingBox: SaberBoundingBox(minX: Int(row[5]), minY: Int(row[6]), maxX: Int(row[7]), maxY: Int(row[8])),
        score: row[9], scoreBreakdown: SaberScoreBreakdown(length: 0, aspect: 0, extent: 0,
            widthConsistency: 0, area: 0, peakBrightness: 0, meanBrightness: 0, highBrightnessRatio: 0,
            colorPurity: 0, localContrast: 0, emitterTexture: 0, clippedWhite: 0,
            longitudinalHighCoverage: 0, coreSupport: 0, longitudinalCoreCoverage: 0),
        isEmitterEligible: Int(row[10]) & 1 != 0, isCompactRed: false, peakValue: Int(row[12]), meanValue: row[15],
        highValueRatio: row[14], meanColorPurity: row[17], clippedWhiteRatio: 0, brightnessVariation: 0,
        localContrast: 0, longitudinalHighCoverage: 0, widthVariation: 0, coreSupportRatio: 0,
        longitudinalCoreCoverage: 0, longitudinalContinuity: 0, largestLongitudinalGap: 0,
        retainedBodyRatio: 0, rawPCASpan: hypot(Double(b.x - a.x), Double(b.y - a.y)),
        robustMainIntervalEndpoints: nil, robustMainIntervalLength: 0, axialDensity: 0,
        componentArea: Int(row[18]), pointCount: 0, usedPointLEDFallback: false)
}

@main enum SelectionReplayHost {
    static func main() throws {
        if CommandLine.arguments.contains("--self-test") {
            testStationarySelection()
            testEndOnSelection()
            return
        }
        let input = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [[String: Any]]
        var output: [[String: Any]] = []
        for clip in input {
            let lock = SaberTrackLock()
            let sources = clip["sources"] as? [String] ?? []
            let width = clip["width"] as! Int, height = clip["height"] as! Int
            let area = (clip["area"] as? [Double]).map {
                SaberPlayArea(minX: $0[0], minY: $0[1], maxX: $0[2], maxY: $0[3])
            }
            var frames: [[String: Any]] = []
            for frame in clip["frames"] as! [[String: Any]] {
                if frame["reset"] as? Bool == true { lock.reset() }
                var result: [String: Any] = ["f": frame["f"]!]
                for (name, color) in [("red", SaberColor.red), ("blue", .blue)] {
                    var rows = frame[name] as! [[Double]]
                    let observedRows = (frame["observed"] as? [String: [[Double]]])?[name] ?? rows
                    let observed = observedRows.map { replayCandidate($0, sources: sources) }
                    // 原画像の色支持gateは保存候補だけでは再現できない。候補replayは上限評価。
                    let continuity = observedRows.filter { Int($0[10]) & 1 == 0 }.map { replayCandidate($0, sources: sources) }
                    if let seed = frame["seed"] as? [String: Any], let value = seed[name] {
                        guard let selected = value as? [Double] else {
                            _ = lock.select([], color: color, width: width, height: height,
                                            now: frame["t"] as! Double, observedCandidates: observed)
                            result[name] = ["sel": NSNull(), "path": "unseeded"]
                            continue
                        }
                        if let index = rows.firstIndex(where: { Array($0[1...4]) == selected }) {
                            rows.insert(rows.remove(at: index), at: 0)
                        }
                    }
                    let candidates = rows.map { replayCandidate($0, sources: sources) }
                    selectionReplayPath = "acquisition"
                    selectionReplayExcluded = []
                    let winner = lock.select(candidates, color: color, width: width, height: height,
                                             now: frame["t"] as! Double, playArea: area, observedCandidates: observed, continuityCandidates: continuity)
                    let selected = winner.map { [$0.endpoints.0.x, $0.endpoints.0.y, $0.endpoints.1.x, $0.endpoints.1.y] }
                    result[name] = ["sel": selected as Any? ?? NSNull(), "path": selectionReplayPath,
                                    "excluded": selectionReplayExcluded.map {
                                        [$0.endpoints.0.x, $0.endpoints.0.y, $0.endpoints.1.x, $0.endpoints.1.y]
                                    }]
                }
                frames.append(result)
            }
            output.append(["name": clip["name"]!, "frames": frames])
        }
        FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys]))
    }
}

// 画像生成/eligibilityと独立に、実運用の色別選択履歴を連続frameで検証する。
private func testStationarySelection() {
    func candidate(_ x: Int, _ y: Int = 200, score: Double = 80) -> SaberCandidate {
        replayCandidate([0, Double(x), Double(y - 35), Double(x), Double(y + 35),
                         Double(x - 4), Double(y - 35), Double(x + 4), Double(y + 35),
                         score, 3, 0, 254, 0.8, 0.8, 230, 0, 0.7, 600, 8])
    }
    for color in [SaberColor.red, .blue] {
        let lock = SaberTrackLock(), fixed = candidate(80)
        func select(_ candidates: [SaberCandidate], _ time: Double) -> SaberCandidate? {
            lock.select(candidates, color: color, width: 640, height: 480, now: time)
        }
        // 対抗する移動の証拠がない静止刃/静止物だけなら、長時間でも従来通り送る。
        for frame in 0..<200 {
            precondition(select([fixed], Double(frame) / 60)?.endpoints.0.x == 80)
        }
        lock.reset()
        _ = select([fixed], 0)
        var movingX = 0
        for frame in 1...240 {
            movingX = 400 + Int(70 * sin(Double(frame) / 15))
            let winner = select([fixed, candidate(movingX, 300, score: 70)], Double(frame) / 60)
            if Double(frame) / 60 > SaberTrackLock.stationarySeconds + 0.1 {
                precondition(winner?.endpoints.0.x == movingX, "固定物へのtrackを解除して遠い実刃を取得")
            }
        }
        // 動いていた実刃の一時停止と、背景だけ残った時の抑制を固定する。
        for frame in 241...420 {
            precondition(select([fixed, candidate(movingX, 300, score: 70)], Double(frame) / 60)?.endpoints.0.x == movingX)
        }
        precondition(select([fixed], 421.0 / 60) == nil)
        let otherColor: SaberColor = color == .red ? .blue : .red
        precondition(lock.select([fixed], color: otherColor, width: 640, height: 480,
                                 now: 421.0 / 60)?.endpoints.0.x == 80)
        lock.reset()
        precondition(select([fixed], 422.0 / 60)?.endpoints.0.x == 80)
        // reset後は過去の静止時間・降格・移動証拠を引き継がない。
        precondition(select([fixed, candidate(400, 300, score: 70)], 423.0 / 60)?.endpoints.0.x == 80)
        lock.reset()
        // 同色2本の交差。両方の中点が移動し続けるため、既存の速度予測による継続を保つ。
        for frame in 0...240 {
            let x = frame <= 120 ? 100 + 2 * frame : 580 - 2 * frame
            let otherX = 600 - x
            let winner = select([candidate(x), candidate(otherX)], Double(frame) / 60)
            precondition(winner?.endpoints.0.x == x, "交差で継続中の刃を失わない")
        }
        lock.reset()
        // ineligible期間も静止を観測するが、移動の証拠としては採用しない。
        for frame in 0..<120 {
            let noise = candidate(400 + Int(70 * sin(Double(frame) / 15)), 300)
            precondition(lock.select([fixed], color: color, width: 640, height: 480,
                                     now: Double(frame) / 60, observedCandidates: [fixed, noise])?.endpoints.0.x == 80)
        }
        lock.reset()
        for frame in 0..<120 {
            precondition(lock.select([], color: color, width: 640, height: 480,
                                     now: Double(frame) / 60, observedCandidates: [fixed]) == nil)
        }
        for frame in 120...150 {
            let blade = candidate(400 + 2 * (frame - 120), 300, score: 70)
            let result = select([fixed, blade], Double(frame) / 60)
            if frame >= 132 { precondition(result?.endpoints.0.x == blade.endpoints.0.x) }
        }
        // 解像度変更も静止時間・降格を消す。
        precondition(lock.select([fixed], color: color, width: 639, height: 480, now: 3)?.endpoints.0.x == 80)
        lock.reset()
        let partial = replayCandidate([0, 80, 200, 80, 235, 76, 200, 84, 235,
                                       80, 3, 0, 254, 0.8, 0.8, 230, 0, 0.7, 300, 4])
        for frame in 0...240 {
            let variant = frame / 60 % 2 == 0 ? fixed : partial
            let blade = candidate(400 + 2 * min(frame, 36), 300, score: 70)
            let winner = select([variant, blade], Double(frame) / 60)
            if Double(frame) / 60 > SaberTrackLock.stationarySeconds + 0.1 {
                precondition(winner?.endpoints.0.x == blade.endpoints.0.x, "全体/部分bodyの切替で固定物へ戻らない")
            }
        }
        lock.reset()
        for frame in 0..<120 {
            let blade = candidate(400 + 2 * max(0, frame - 107), 300, score: 70)
            _ = lock.select([fixed], color: color, width: 640, height: 480,
                            now: Double(frame) / 60, observedCandidates: [fixed, blade])
        }
        // eligibility復帰時に、直前まで見えていた成分の実測移動を使える。
        precondition(select([fixed, candidate(426, 300, score: 70)], 2)?.endpoints.0.x == 426)
    }
    print("Stationary selection: both colors, moving rival, stationary-only, pause, crossing and reset passed")
}

// 短縮履歴を持つ色だけに限定し、現候補の端点・非延長の期限・通常選択の独立性を固定する。
private func testEndOnSelection() {
    func candidate(_ length: Int, _ x: Int = 100, eligible: Bool = true,
                   peak: Double = 240, purity: Double = 0.8) -> SaberCandidate {
        replayCandidate([0, Double(x), 200, Double(x + length), 200,
                         Double(x), 196, Double(x + length), 204, 80,
                         eligible ? 3 : 0, 0, peak, 0.5, 0.1, 180, 0, purity, 100, 2])
    }
    for color in [SaberColor.red, .blue] {
        let lock = SaberTrackLock()
        func select(_ eligible: [SaberCandidate], _ observed: [SaberCandidate], _ now: Double) -> SaberCandidate? {
            lock.select(eligible, color: color, width: 480, height: 640, now: now,
                        observedCandidates: eligible + observed, continuityCandidates: observed)
        }
        func seed(_ base: Double = 0) {
            lock.reset()
            for (i, length) in [48, 36, 26, 18].enumerated() {
                precondition(select([candidate(length)], [], base + Double(i) / 60) != nil)
            }
        }
        let short = candidate(12, 104, eligible: false)
        precondition(select([], [short], 0) == nil, "静止した短い物だけでは取得しない")
        seed()
        precondition(select([], [], 0.06) == nil, "n0では過去端点を出さない")
        precondition(select([], [short], 0.07)?.endpoints.0 == short.endpoints.0)
        let moved = candidate(14, 108, eligible: false)
        precondition(select([], [moved], 0.08)?.endpoints.1 == moved.endpoints.1, "現在frameの端点")
        precondition(!lock.suppressed.contains(color), "実測救済は送信抑制しない")
        // 別色には履歴を貸さない。
        let other: SaberColor = color == .red ? .blue : .red
        precondition(lock.select([], color: other, width: 480, height: 640, now: 0.08,
                                 continuityCandidates: [short]) == nil)
        for (index, bad) in [candidate(12, 300, eligible: false), candidate(12, eligible: false, peak: 209),
                            candidate(12, eligible: false, purity: 0.649), candidate(22, eligible: false)].enumerated() {
            seed()
            precondition(select([], [bad], 0.07) == nil, "近傍/輝度/純度/長さ \(index)")
        }
        seed()
        var warm = short
        warm.warmNoDeepRed = SaberWarmNoDeepRedVerdict(deepCount: 0, warmCount: 10, pixelCount: 10)
        precondition(select([], [warm], 0.07) == nil, "赤コーンgateを迂回しない")
        var teal = short
        teal.blueNoDeepSupport = SaberBlueNoDeepSupportVerdict(deepCount: 0, pixelCount: 10)
        precondition(select([], [teal], 0.08) == nil, "青緑gateを迂回しない")
        seed()
        // 通常eligibleの結果は救済候補の有無で変えない。
        let normal = candidate(30, 110)
        precondition(select([normal], [short], 0.07)?.endpoints.0 == normal.endpoints.0)
        seed()
        for frame in 4...31 {
            let time = Double(frame) / 60
            if frame == 20 {
                precondition(select([candidate(18)], [], time) != nil)
            } else {
                precondition(select([], [short], time) != nil)
            }
        }
        precondition(select([], [short], 0.534) == nil, "短いeligible復帰でも0.5秒期限を延ばさない")
        precondition(lock.suppressed.contains(color))
        seed()
        precondition(select([], [short], 0.151) == nil, "6コマを超えた不在は継がない")
        seed(); lock.reset()
        precondition(select([], [short], 0.07) == nil)
        seed()
        precondition(lock.select([], color: color, width: 640, height: 480, now: 0.07,
                                 continuityCandidates: [short]) == nil, "解像度変更でreset")
        seed()
        for frame in 4...6 { precondition(select([], [], Double(frame) / 60) == nil) }
        precondition(select([candidate(18, 300)], [], 7.0 / 60) != nil)
        precondition(select([], [candidate(12, 300, eligible: false)], 8.0 / 60) == nil,
                     "別物体へ通常選択が移ったら短縮履歴を貸さない")
        seed()
        precondition(select([], [short], -0.1) == nil, "時計の逆行では使わない")
    }
    print("End-on selection: current endpoints, recency, deadline, safety, normal priority, color and reset passed")
}
