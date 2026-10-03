import Foundation

/// Wire format shared by the iPhone sender and the Mac P2P bridge
/// (Tools/p2p_bridge compiles this same file). Foundation only, no Network.
///
/// One UDP datagram = 20-byte header + body:
///   0..<4   magic "PSP2"
///   4       version (1)
///   5       kind (P2PMessageKind)
///   6       color (0 = RED, 1 = BLUE; 0 for ping/pong)
///   7       reserved (0)
///   8..<12  sender session ID (UInt32 BE, random per app launch / bridge start)
///   12..<20 sequence (UInt64 BE, increasing per session)
///   body    coordinates: the unchanged ASCII payload ("x1,y1,x2,y2" or
///           "ts=...;x1,y1,x2,y2"); ping/pong: empty
/// The bridge forwards the body bytes unchanged to 127.0.0.1:5005 (RED) or :5006 (BLUE).
enum PhoneSaberP2P {
    static let serviceType = "_phonesaber-p2p._udp"
    static let magic: [UInt8] = Array("PSP2".utf8)
    static let version: UInt8 = 1
    static let headerSize = 20
    /// Coordinates are a few dozen bytes; anything larger is malformed.
    static let maximumBodySize = 256
    static let redPort = 5005
    static let bluePort = 5006
}

enum P2PMessageKind: UInt8 {
    case coordinates = 1
    case ping = 2
    case pong = 3
}

enum P2PColor: UInt8 {
    case red = 0
    case blue = 1

    init?(port: Int) {
        switch port {
        case PhoneSaberP2P.redPort: self = .red
        case PhoneSaberP2P.bluePort: self = .blue
        default: return nil
        }
    }

    var port: Int { self == .red ? PhoneSaberP2P.redPort : PhoneSaberP2P.bluePort }
    var label: String { self == .red ? "RED" : "BLUE" }
}

struct P2PMessage: Equatable {
    var kind: P2PMessageKind
    var color: P2PColor
    var session: UInt32
    var sequence: UInt64
    var body: Data

    static func coordinates(_ text: String, color: P2PColor, session: UInt32,
                            sequence: UInt64) -> P2PMessage? {
        guard let body = text.data(using: .ascii), P2PPayload.isValid(body) else { return nil }
        return P2PMessage(kind: .coordinates, color: color, session: session, sequence: sequence, body: body)
    }

    static func ping(session: UInt32, sequence: UInt64) -> P2PMessage {
        P2PMessage(kind: .ping, color: .red, session: session, sequence: sequence, body: Data())
    }

    static func pong(session: UInt32, echoing sequence: UInt64) -> P2PMessage {
        P2PMessage(kind: .pong, color: .red, session: session, sequence: sequence, body: Data())
    }

    func encoded() -> Data {
        var data = Data(PhoneSaberP2P.magic)
        data.append(contentsOf: [PhoneSaberP2P.version, kind.rawValue, color.rawValue, 0])
        withUnsafeBytes(of: session.bigEndian) { data.append(contentsOf: $0) }
        withUnsafeBytes(of: sequence.bigEndian) { data.append(contentsOf: $0) }
        data.append(body)
        return data
    }

    enum DecodeError: Error, Equatable {
        case tooShort, badMagic, unsupportedVersion(UInt8), unknownKind(UInt8),
             unknownColor(UInt8), bodyTooLarge(Int), invalidPayload, unexpectedBody
    }

    static func decode(_ data: Data) -> Result<P2PMessage, DecodeError> {
        let bytes = [UInt8](data)
        guard bytes.count >= PhoneSaberP2P.headerSize else { return .failure(.tooShort) }
        guard Array(bytes[0..<4]) == PhoneSaberP2P.magic else { return .failure(.badMagic) }
        guard bytes[4] == PhoneSaberP2P.version else { return .failure(.unsupportedVersion(bytes[4])) }
        guard let kind = P2PMessageKind(rawValue: bytes[5]) else { return .failure(.unknownKind(bytes[5])) }
        guard let color = P2PColor(rawValue: bytes[6]) else { return .failure(.unknownColor(bytes[6])) }
        let session = bytes[8..<12].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
        let sequence = bytes[12..<20].reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
        let body = Data(bytes[PhoneSaberP2P.headerSize...])
        guard body.count <= PhoneSaberP2P.maximumBodySize else { return .failure(.bodyTooLarge(body.count)) }
        switch kind {
        case .coordinates:
            guard P2PPayload.isValid(body) else { return .failure(.invalidPayload) }
        case .ping, .pong:
            guard body.isEmpty else { return .failure(.unexpectedBody) }
        }
        return .success(P2PMessage(kind: kind, color: color, session: session, sequence: sequence, body: body))
    }
}

/// The existing coordinate payload: "x1,y1,x2,y2" with optional "ts=<number>;"
/// prefix (measurement mode). Validated, never rewritten.
enum P2PPayload {
    static func isValid(_ body: Data) -> Bool {
        guard !body.isEmpty, body.count <= PhoneSaberP2P.maximumBodySize,
              body.allSatisfy({ $0 >= 0x20 && $0 < 0x7F }),
              var text = String(data: body, encoding: .ascii) else { return false }
        if text.hasPrefix("ts=") {
            guard let separator = text.firstIndex(of: ";"),
                  Double(text[text.index(text.startIndex, offsetBy: 3)..<separator]) != nil else { return false }
            text = String(text[text.index(after: separator)...])
        }
        let fields = text.split(separator: ",", omittingEmptySubsequences: false)
        return fields.count == 4 && fields.allSatisfy { Double($0).map(\.isFinite) == true }
    }
}

/// Drops duplicated / reordered datagrams per (sender session, color). A new
/// session (app restart) starts over; old sessions are forgotten after a while.
struct P2PSequenceFilter {
    private struct Key: Hashable { let session: UInt32; let color: P2PColor }
    private var latest: [Key: (sequence: UInt64, seenAt: TimeInterval)] = [:]
    let forgetAfter: TimeInterval

    init(forgetAfter: TimeInterval = 30) { self.forgetAfter = forgetAfter }

    /// True when the datagram is newer than everything seen for its session and color.
    mutating func accept(session: UInt32, color: P2PColor, sequence: UInt64, now: TimeInterval) -> Bool {
        let key = Key(session: session, color: color)
        if let previous = latest[key], previous.sequence >= sequence,
           now - previous.seenAt < forgetAfter {
            return false
        }
        latest[key] = (sequence, now)
        if latest.count > 64 {
            latest = latest.filter { now - $0.value.seenAt < forgetAfter }
        }
        return true
    }
}

/// Liveness of the P2P link, decided only from pong arrival times so it can be
/// unit-tested with a manual clock. The link carries coordinates only while
/// `isUsable`; otherwise the sender falls back to the existing LAN UDP path.
struct P2PLivenessMonitor {
    struct Timing: Equatable {
        /// No pong for this long: stop using P2P (fall back to LAN).
        var staleAfter: TimeInterval = 1.5
        /// No pong for this long: drop the connection and resolve the service again.
        var reconnectAfter: TimeInterval = 4
        /// Time allowed for the first pong of a new connection.
        var connectTimeout: TimeInterval = 4
    }

    let timing: Timing
    private(set) var connectionStartedAt: TimeInterval?
    private(set) var lastPongAt: TimeInterval?

    init(timing: Timing = Timing()) { self.timing = timing }

    mutating func connectionStarted(at now: TimeInterval) {
        connectionStartedAt = now
        lastPongAt = nil
    }

    mutating func pongReceived(at now: TimeInterval) { lastPongAt = now }

    mutating func reset() {
        connectionStartedAt = nil
        lastPongAt = nil
    }

    func isUsable(at now: TimeInterval) -> Bool {
        guard let lastPongAt else { return false }
        return now - lastPongAt <= timing.staleAfter
    }

    /// True when the current connection should be torn down and rebuilt.
    func shouldReconnect(at now: TimeInterval) -> Bool {
        guard let started = connectionStartedAt else { return false }
        if let lastPongAt { return now - lastPongAt > timing.reconnectAfter }
        return now - started > timing.connectTimeout
    }
}
