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
    /// TCP relay to the Mac's diagnostics receiver (triage bundles), same peer-to-peer
    /// reach as the coordinate link. DNS-SD service names are limited to 15 characters.
    static let diagnosticsServiceType = "_phonesaber-dp2p._tcp"
    static let diagnosticsReceiverPort = 8765
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
        // header + body を一度だけ確保し、append による拡張・コピーを避ける。
        var data = Data(count: PhoneSaberP2P.headerSize + body.count)
        data.withUnsafeMutableBytes { (bytes: UnsafeMutableRawBufferPointer) in
            for index in PhoneSaberP2P.magic.indices { bytes[index] = PhoneSaberP2P.magic[index] }
            bytes[4] = PhoneSaberP2P.version
            bytes[5] = kind.rawValue
            bytes[6] = color.rawValue
            // reserved byte は Data(count:) のゼロ初期化を使う。
            withUnsafeBytes(of: session.bigEndian) {
                bytes.baseAddress!.advanced(by: 8).copyMemory(from: $0.baseAddress!, byteCount: $0.count)
            }
            withUnsafeBytes(of: sequence.bigEndian) {
                bytes.baseAddress!.advanced(by: 12).copyMemory(from: $0.baseAddress!, byteCount: $0.count)
            }
            body.copyBytes(to: bytes.baseAddress!.advanced(by: PhoneSaberP2P.headerSize)
                .assumingMemoryBound(to: UInt8.self), count: body.count)
        }
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
///
/// Hysteresis: the first link of a run is usable on its first pong. Once the
/// link has gone stale (or a later connection replaces it), it needs
/// `recoveryPongs` consecutive pongs, each within `recoveryMaxGap` of the
/// previous one, before it carries coordinates again. Without this, a link at
/// the stale edge flipped P2P / LAN on every single pong.
struct P2PLivenessMonitor {
    struct Timing: Equatable {
        /// No pong for this long: stop using P2P (fall back to LAN).
        var staleAfter: TimeInterval = 1.5
        /// No pong for this long: drop the connection and resolve the service again.
        var reconnectAfter: TimeInterval = 4
        /// Time allowed for the first pong of a new connection.
        var connectTimeout: TimeInterval = 4
        /// Consecutive pongs needed before a link that went stale is used again.
        var recoveryPongs: Int = 3
        /// Pongs further apart than this do not count as consecutive.
        var recoveryMaxGap: TimeInterval = 0.5
    }

    let timing: Timing
    private(set) var connectionStartedAt: TimeInterval?
    private(set) var lastPongAt: TimeInterval?
    /// True while a link that went stale has not yet proven itself again.
    private(set) var recovering = false
    private var everUsable = false
    private var streak = 0

    init(timing: Timing = Timing()) { self.timing = timing }

    /// A new connection starts. After the first usable link of this run it must
    /// earn usability again with `recoveryPongs` consecutive pongs.
    mutating func connectionStarted(at now: TimeInterval) {
        connectionStartedAt = now
        lastPongAt = nil
        streak = 0
        recovering = everUsable
    }

    /// The connection is gone (reconnect pending). Keeps the hysteresis memory.
    mutating func connectionEnded() {
        connectionStartedAt = nil
        lastPongAt = nil
        streak = 0
        recovering = everUsable
    }

    mutating func pongReceived(at now: TimeInterval) {
        if let lastPongAt, now - lastPongAt > timing.staleAfter {
            // The link was stale before this pong: it has to recover first.
            recovering = everUsable
        }
        if let lastPongAt, now - lastPongAt <= timing.recoveryMaxGap {
            streak += 1
        } else {
            streak = 1
        }
        lastPongAt = now
        if recovering && streak >= timing.recoveryPongs { recovering = false }
        if !recovering { everUsable = true }
    }

    /// Forget everything (P2P switched off / sender stopped).
    mutating func reset() {
        connectionStartedAt = nil
        lastPongAt = nil
        recovering = false
        everUsable = false
        streak = 0
    }

    func isUsable(at now: TimeInterval) -> Bool {
        guard let lastPongAt, !recovering else { return false }
        return now - lastPongAt <= timing.staleAfter
    }

    /// True when the current connection should be torn down and rebuilt.
    func shouldReconnect(at now: TimeInterval) -> Bool {
        guard let started = connectionStartedAt else { return false }
        if let lastPongAt { return now - lastPongAt > timing.reconnectAfter }
        return now - started > timing.connectTimeout
    }
}
