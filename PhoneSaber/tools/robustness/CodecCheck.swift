import Foundation

struct SeededRandom {
    var state: UInt64
    mutating func next() -> UInt64 {
        state = state &+ 0x9e3779b97f4a7c15
        var value = state
        value = (value ^ (value >> 30)) &* 0xbf58476d1ce4e5b9
        value = (value ^ (value >> 27)) &* 0x94d049bb133111eb
        return value ^ (value >> 31)
    }
    mutating func int(_ limit: Int) -> Int { Int(next() % UInt64(limit)) }
}

@main struct CodecCheck {
    static func main() throws {
        var rng = SeededRandom(state: UInt64(CommandLine.arguments[1])!)
        let seconds = Double(CommandLine.arguments[2])!
        let started = ProcessInfo.processInfo.systemUptime
        // wire 境界と expiry 境界は random だけに任せず必ず通す。
        for session in [UInt32(0), .max] {
            for sequence in [UInt64(0), 1, .max] {
                let message = P2PMessage.coordinates("-0,1,2,3", color: .blue, session: session, sequence: sequence)!
                let decoded = try P2PMessage.decode(message.encoded()).get()
                precondition(decoded == message)
            }
        }
        var edge = P2PSequenceFilter()
        precondition(edge.accept(session: 0, color: .red, sequence: .max, now: 0))
        precondition(!edge.accept(session: 0, color: .red, sequence: .max, now: 29.999))
        precondition(!edge.accept(session: 0, color: .red, sequence: 0, now: 29.999))
        precondition(edge.accept(session: 0, color: .red, sequence: 0, now: 30))
        var iterations = 0
        var filter = P2PSequenceFilter()
        var expected: [String: (UInt64, Double)] = [:]
        var now = 0.0
        while ProcessInfo.processInfo.systemUptime - started < seconds {
            let session = UInt32(truncatingIfNeeded: rng.next())
            let sequence = rng.next()
            let color: P2PColor = rng.int(2) == 0 ? .red : .blue
            let width = rng.int(4096) + 1, height = rng.int(4096) + 1
            let a = PixelPoint(x: rng.int(width), y: rng.int(height))
            let b = PixelPoint(x: rng.int(width), y: rng.int(height))
            let coordinates = payload(for: (a, b), source: (width, height), output: (1920, 1080),
                                      mirrorX: rng.int(2) == 0, mirrorY: rng.int(2) == 0)
            precondition(coordinates.split(separator: ",").count == 4)
            let text = iterations % 2 == 0 ? coordinates : timestampedPayload(coordinates, timestamp: Double(rng.int(2000000000)))
            let message = P2PMessage.coordinates(text, color: color, session: session, sequence: sequence)!
            let roundTrip = try P2PMessage.decode(message.encoded()).get()
            precondition(roundTrip == message)
            for control in [P2PMessage.ping(session: session, sequence: sequence), .pong(session: session, echoing: sequence)] {
                let decodedControl = try P2PMessage.decode(control.encoded()).get()
                precondition(decodedControl == control)
            }
            var data = message.encoded()
            switch iterations % 4 {
            case 0: data = Data((0..<rng.int(1024)).map { _ in UInt8(truncatingIfNeeded: rng.next()) })
            case 1: data = data.prefix(rng.int(data.count))
            case 2: data.append(Data(repeating: 0, count: 300))
            default: data[rng.int(data.count)] = UInt8(truncatingIfNeeded: rng.next())
            }
            if case .success(let decoded) = P2PMessage.decode(data) {
                let normalized = try P2PMessage.decode(decoded.encoded()).get()
                precondition(normalized == decoded)
                precondition(decoded.body.count <= 256)
            }
            now += Double(rng.int(100)) / 1000
            let smallSession = UInt32(rng.int(128))
            let smallSequence = UInt64(rng.int(64))
            let key = "\(smallSession)-\(color.rawValue)"
            let previous = expected[key]
            let accept = previous == nil || previous!.0 < smallSequence || now - previous!.1 >= 30
            precondition(filter.accept(session: smallSession, color: color, sequence: smallSequence, now: now) == accept)
            if accept { expected[key] = (smallSequence, now) }
            iterations += 1
        }
        print("PASS seed=\(CommandLine.arguments[1]) iterations=\(iterations) seconds=\(ProcessInfo.processInfo.systemUptime-started)")
    }
}
