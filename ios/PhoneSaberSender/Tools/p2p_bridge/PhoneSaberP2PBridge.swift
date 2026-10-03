import Darwin
import Foundation
import Network

/// Mac-side PhoneSaber P2P bridge.
///
///   iPhone PhoneSaberSender --(Network.framework, includePeerToPeer, UDP)-->
///   this bridge (_phonesaber-p2p._udp) --(UDP)--> 127.0.0.1:5005 (RED) / :5006 (BLUE)
///   --> existing Unity InputPoint.cs (unchanged)
///
/// The coordinate body is forwarded byte for byte; the wire format lives in
/// ios/PhoneSaberSender/PhoneSaberSender/P2PProtocol.swift (compiled together).
/// Logs only state changes, the first RED/BLUE datagram per sender session and
/// a periodic summary — never one line per frame.
struct BridgeOptions {
    var serviceName = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    var bonjour = true
    var listenPort: UInt16 = 0
    var loopbackOnly = false
    var forwardHost = "127.0.0.1"
    var redPort = UInt16(PhoneSaberP2P.redPort)
    var bluePort = UInt16(PhoneSaberP2P.bluePort)
    var statsInterval: TimeInterval = 10
    var peerIdleTimeout: TimeInterval = 10

    static let usage = """
    usage: PhoneSaberP2PBridge [--name NAME] [--no-bonjour] [--listen-port N] [--loopback-only]
                               [--forward-host HOST] [--red-port N] [--blue-port N]
                               [--stats-interval SECONDS] [--peer-idle-timeout SECONDS]
    """

    static func parse(_ arguments: [String]) -> BridgeOptions? {
        var options = BridgeOptions()
        var iterator = arguments.makeIterator()
        func value() -> String? { iterator.next() }
        while let argument = iterator.next() {
            switch argument {
            case "--name": guard let v = value() else { return nil }; options.serviceName = v
            case "--no-bonjour": options.bonjour = false
            case "--listen-port": guard let v = value().flatMap(UInt16.init) else { return nil }; options.listenPort = v
            case "--loopback-only": options.loopbackOnly = true
            case "--forward-host": guard let v = value() else { return nil }; options.forwardHost = v
            case "--red-port": guard let v = value().flatMap(UInt16.init) else { return nil }; options.redPort = v
            case "--blue-port": guard let v = value().flatMap(UInt16.init) else { return nil }; options.bluePort = v
            case "--stats-interval": guard let v = value().flatMap(Double.init), v > 0 else { return nil }; options.statsInterval = v
            case "--peer-idle-timeout": guard let v = value().flatMap(Double.init), v > 0 else { return nil }; options.peerIdleTimeout = v
            case "-h", "--help": return nil
            default: return nil
            }
        }
        return options
    }
}

/// Forwards bodies to the local Unity UDP ports through one plain UDP socket.
final class LocalForwarder {
    private let socketFD: Int32
    private var addresses: [P2PColor: sockaddr_in] = [:]

    init?(host: String, redPort: UInt16, bluePort: UInt16) {
        socketFD = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard socketFD >= 0 else { return nil }
        let flags = fcntl(socketFD, F_GETFL)
        _ = fcntl(socketFD, F_SETFL, flags | O_NONBLOCK)
        for (color, port) in [(P2PColor.red, redPort), (.blue, bluePort)] {
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = port.bigEndian
            guard inet_pton(AF_INET, host, &address.sin_addr) == 1 else { return nil }
            addresses[color] = address
        }
    }

    deinit { close(socketFD) }

    @discardableResult
    func forward(_ body: Data, color: P2PColor) -> Bool {
        guard var address = addresses[color] else { return false }
        let sent = body.withUnsafeBytes { buffer in
            withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(socketFD, buffer.baseAddress, buffer.count, 0, $0,
                           socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        return sent == body.count
    }
}

final class P2PBridge {
    private let options: BridgeOptions
    private let queue = DispatchQueue(label: "PhoneSaberP2PBridge")
    private let forwarder: LocalForwarder
    private var listener: NWListener?
    private var peers: [ObjectIdentifier: (connection: NWConnection, label: String, lastSeen: TimeInterval)] = [:]
    private var filter = P2PSequenceFilter()
    private var announcedSessions: Set<String> = []
    private var counts: [String: Int] = [:]
    private var lastPingAt: TimeInterval?
    private var peerLostLogged = true
    private var timer: DispatchSourceTimer?

    init?(options: BridgeOptions) {
        guard let forwarder = LocalForwarder(host: options.forwardHost, redPort: options.redPort,
                                             bluePort: options.bluePort) else { return nil }
        self.options = options
        self.forwarder = forwarder
    }

    private func now() -> TimeInterval { ProcessInfo.processInfo.systemUptime }

    private func log(_ message: String) {
        print("[P2P] \(message)")
        fflush(stdout)
    }

    func start() throws {
        let parameters = NWParameters.udp
        parameters.includePeerToPeer = true
        if options.loopbackOnly {
            parameters.requiredInterfaceType = .loopback
        }
        let port = NWEndpoint.Port(rawValue: options.listenPort) ?? .any
        let listener = try NWListener(using: parameters, on: port)
        if options.bonjour {
            listener.service = NWListener.Service(name: options.serviceName, type: PhoneSaberP2P.serviceType)
        }
        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                let bound = listener.port.map { "\($0.rawValue)" } ?? "?"
                let service = self.options.bonjour
                    ? "service \"\(self.options.serviceName)\" \(PhoneSaberP2P.serviceType)" : "no Bonjour"
                self.log("listening on UDP \(bound) (\(service), peer-to-peer enabled); forwarding RED→\(self.options.forwardHost):\(self.options.redPort) BLUE→\(self.options.forwardHost):\(self.options.bluePort)")
            case .failed(let error):
                self.log("listener failed: \(error); exiting")
                exit(1)
            case .waiting(let error):
                self.log("listener waiting: \(error)")
            default:
                break
            }
        }
        listener.serviceRegistrationUpdateHandler = { [weak self] change in
            if case .add(let endpoint) = change { self?.log("Bonjour registered \(endpoint)") }
        }
        listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
        self.listener = listener
        listener.start(queue: queue)
        startTimer()
    }

    func stop() {
        queue.sync {
            timer?.cancel()
            listener?.cancel()
            peers.values.forEach { $0.connection.cancel() }
            peers.removeAll()
        }
    }

    private func accept(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        let label = "\(connection.endpoint)"
        peers[id] = (connection, label, now())
        log("peer connected \(label) (peers: \(peers.count))")
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed(let error):
                self.log("peer \(label) failed: \(error)")
                self.removePeer(id)
            case .cancelled:
                self.removePeer(id)
            default:
                break
            }
        }
        connection.start(queue: queue)
        receive(on: connection, id: id)
    }

    private func removePeer(_ id: ObjectIdentifier) {
        guard let removed = peers.removeValue(forKey: id) else { return }
        log("peer closed \(removed.label) (peers: \(peers.count))")
    }

    private func receive(on connection: NWConnection, id: ObjectIdentifier) {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data { self.handle(data, from: connection, id: id) }
            if error == nil, self.peers[id] != nil { self.receive(on: connection, id: id) }
        }
    }

    private func handle(_ data: Data, from connection: NWConnection, id: ObjectIdentifier) {
        let time = now()
        peers[id]?.lastSeen = time
        switch P2PMessage.decode(data) {
        case .failure(let error):
            counts["malformed", default: 0] += 1
            if counts["malformed"] == 1 { log("malformed datagram dropped (\(error)); further ones are counted") }
        case .success(let message):
            switch message.kind {
            case .ping:
                lastPingAt = time
                if peerLostLogged {
                    peerLostLogged = false
                    log("peer alive (session \(message.session)); iPhone can use P2P")
                }
                connection.send(content: P2PMessage.pong(session: message.session, echoing: message.sequence).encoded(),
                                completion: .idempotent)
            case .pong:
                counts["malformed", default: 0] += 1
            case .coordinates:
                guard filter.accept(session: message.session, color: message.color,
                                    sequence: message.sequence, now: time) else {
                    counts["stale", default: 0] += 1
                    return
                }
                let key = "\(message.session)-\(message.color.label)"
                if !announcedSessions.contains(key) {
                    announcedSessions.insert(key)
                    log("\(message.color.label) received (session \(message.session), from \(peers[id]?.label ?? "?"))")
                }
                if forwarder.forward(message.body, color: message.color) {
                    counts[message.color.label, default: 0] += 1
                } else {
                    counts["forwardFailed", default: 0] += 1
                }
            }
        }
    }

    private func startTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let tick = min(1.0, options.statsInterval)
        timer.schedule(deadline: .now() + tick, repeating: tick)
        var lastStats = now()
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let time = self.now()
            for (id, peer) in self.peers where time - peer.lastSeen > self.options.peerIdleTimeout {
                self.log("peer idle for \(Int(self.options.peerIdleTimeout))s; closing \(peer.label)")
                peer.connection.cancel()
                self.peers.removeValue(forKey: id)
            }
            if let lastPingAt = self.lastPingAt, !self.peerLostLogged, time - lastPingAt > 3 {
                self.peerLostLogged = true
                self.log("no ping for 3s; iPhone falls back to LAN (fallback to LAN)")
            }
            if time - lastStats >= self.options.statsInterval {
                lastStats = time
                if !self.counts.isEmpty {
                    let summary = self.counts.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }
                    self.log("last \(Int(self.options.statsInterval))s: \(summary.joined(separator: " ")) peers=\(self.peers.count)")
                    self.counts.removeAll()
                }
            }
        }
        self.timer = timer
        timer.resume()
    }
}

@main
enum PhoneSaberP2PBridgeMain {
    static func main() {
        setvbuf(stdout, nil, _IOLBF, 0)
        guard let options = BridgeOptions.parse(Array(CommandLine.arguments.dropFirst())) else {
            FileHandle.standardError.write(Data((BridgeOptions.usage + "\n").utf8))
            exit(2)
        }
        guard let bridge = P2PBridge(options: options) else {
            FileHandle.standardError.write(Data("[P2P] cannot open the local forwarding socket\n".utf8))
            exit(1)
        }
        do {
            try bridge.start()
        } catch {
            FileHandle.standardError.write(Data("[P2P] cannot start listener: \(error)\n".utf8))
            exit(1)
        }
        for signalNumber in [SIGINT, SIGTERM] {
            signal(signalNumber, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: signalNumber, queue: .main)
            source.setEventHandler {
                print("[P2P] stopping")
                bridge.stop()
                exit(0)
            }
            source.resume()
            signalSources.append(source)
        }
        dispatchMain()
    }

    private static var signalSources: [DispatchSourceSignal] = []
}
