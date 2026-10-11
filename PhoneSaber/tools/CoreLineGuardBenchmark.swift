import Foundation

// PNG デコード・入出力・診断を測定区間から除いた host 検出時間。
@main
private enum CoreLineGuardBenchmark {
    static func main() throws {
        let args = CommandLine.arguments
        guard args.count == 4, let width = Int(args[1]), let height = Int(args[2]),
              let iterations = Int(args[3]), width > 0, height > 0, iterations > 0 else {
            fatalError("usage: swift-benchmark WIDTH HEIGHT ITERATIONS < BGRA")
        }
        let pixels = FileHandle.standardInput.readDataToEndOfFile()
        guard pixels.count == width * height * 4 else { fatalError("invalid BGRA length") }
        var count = 0
        var times: [Double] = []
        pixels.withUnsafeBytes { raw in
            let base = raw.bindMemory(to: UInt8.self).baseAddress!
            for i in 0..<(iterations + 10) {
                let start = ProcessInfo.processInfo.systemUptime
                let analysis = analyzeSabers(baseAddress: base, width: width, height: height,
                    bytesPerRow: width * 4, redThreshold: ColorThreshold(),
                    blueThreshold: ColorThreshold(), sampleStep: 2)
                let elapsed = (ProcessInfo.processInfo.systemUptime - start) * 1000
                count += analysis.selected.count
                if i >= 10 { times.append(elapsed) }
            }
        }
        times.sort()
        print(times[times.count / 2])
        FileHandle.standardError.write(Data("results=\(count)\n".utf8))
    }
}
