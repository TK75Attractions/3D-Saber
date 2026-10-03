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
    /// Exit when this process is gone (Unity launches the bridge and passes its pid,
    /// so a crashed or force-quit Editor never leaves an orphaned bridge behind).
    var exitWithParent: pid_t?
    /// Testing aid: make the listener fail once after this many seconds.
    var simulateListenerFailureAfter: TimeInterval?
    /// Diagnostics relay: iPhone triage uploads over peer-to-peer Wi-Fi are piped to
    /// the local receiver (phone_saber_triage_receiver.py, TCP 8765). 0 disables.
    var diagnosticsPort = UInt16(PhoneSaberP2P.diagnosticsReceiverPort)
    var diagnosticsListenPort: UInt16 = 0

    static let usage = """
    usage: PhoneSaberP2PBridge [--name NAME] [--no-bonjour] [--listen-port N] [--loopback-only]
                               [--forward-host HOST] [--red-port N] [--blue-port N]
                               [--stats-interval SECONDS] [--peer-idle-timeout SECONDS]
                               [--exit-with-parent PID] [--simulate-listener-failure-after SECONDS]
                               [--diag-port N (0 = no diagnostics relay)] [--diag-listen-port N]
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
            case "--exit-with-parent": guard let v = value().flatMap(Int32.init), v > 0 else { return nil }; options.exitWithParent = v
            case "--simulate-listener-failure-after": guard let v = value().flatMap(Double.init), v > 0 else { return nil }; options.simulateListenerFailureAfter = v
            case "--diag-port": guard let v = value().flatMap(UInt16.init) else { return nil }; options.diagnosticsPort = v
            case "--diag-listen-port": guard let v = value().flatMap(UInt16.init) else { return nil }; options.diagnosticsListenPort = v
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

/// Pipes each peer-to-peer TCP connection from the iPhone (triage bundle upload,
/// plain HTTP) to the local diagnostics receiver, byte for byte in both directions.
/// The receiver accepts loopback peers, so it needs no change.
final class DiagnosticsRelay {
    private let options: BridgeOptions
    private let queue = DispatchQueue(label: "PhoneSaberP2PBridge.diagnostics")
    private var listener: NWListener?
    private var restartAttempts = 0
    private var stopped = false

    init(options: BridgeOptions) { self.options = options }

    private func log(_ message: String) {
        print("[P2P] diag relay: \(message)")
        fflush(stdout)
    }

    func start() throws {
        let parameters = NWParameters.tcp
        parameters.includePeerToPeer = true
        if options.loopbackOnly { parameters.requiredInterfaceType = .loopback }
        let listener = try NWListener(using: parameters,
                                      on: NWEndpoint.Port(rawValue: options.diagnosticsListenPort) ?? .any)
        if options.bonjour {
            listener.service = NWListener.Service(name: options.serviceName,
                                                  type: PhoneSaberP2P.diagnosticsServiceType)
        }
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            guard let self, let listener, self.listener === listener else { return }
            switch state {
            case .ready:
                self.restartAttempts = 0
                self.log("listening on TCP \(listener.port?.rawValue ?? 0) → 127.0.0.1:\(self.options.diagnosticsPort)")
            case .failed(let error):
                listener.cancel()
                self.listener = nil
                self.restartAttempts += 1
                let delay = min(30, pow(2, Double(min(self.restartAttempts - 1, 5))))
                self.log("listener failed: \(error); restarting in \(Int(delay))s")
                self.queue.asyncAfter(deadline: .now() + delay) { [weak self] in
                    guard let self, !self.stopped, self.listener == nil else { return }
                    try? self.start()
                }
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] inbound in self?.relay(inbound) }
        self.listener = listener
        listener.start(queue: queue)
    }

    func stop() {
        queue.sync {
            stopped = true
            listener?.cancel()
        }
    }

    private func relay(_ inbound: NWConnection) {
        guard let port = NWEndpoint.Port(rawValue: options.diagnosticsPort) else { return }
        let outbound = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
        let label = "\(inbound.endpoint)"
        var bytes = 0
        var closed = false
        func close(_ reason: String) {
            guard !closed else { return }
            closed = true
            inbound.cancel()
            outbound.cancel()
            log("upload from \(label) closed after \(bytes) bytes (\(reason))")
        }
        func pipe(from source: NWConnection, to destination: NWConnection, counting: Bool) {
            source.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { data, _, isComplete, error in
                if let data, !data.isEmpty {
                    if counting { bytes += data.count }
                    destination.send(content: data, completion: .contentProcessed { sendError in
                        if let sendError { close("send: \(sendError)") }
                    })
                }
                if isComplete {
                    // Half-close: the HTTP request is complete; keep reading the response.
                    destination.send(content: nil, contentContext: .finalMessage, isComplete: true,
                                     completion: .contentProcessed { _ in
                                         if !counting { close("done") }
                                     })
                    return
                }
                if let error { close("\(error)"); return }
                pipe(from: source, to: destination, counting: counting)
            }
        }
        log("upload from \(label) → 127.0.0.1:\(options.diagnosticsPort)")
        inbound.stateUpdateHandler = { state in
            if case .failed(let error) = state { close("iPhone side: \(error)") }
        }
        var outboundReady = false
        outbound.stateUpdateHandler = { state in
            switch state {
            case .ready:
                outboundReady = true
                pipe(from: inbound, to: outbound, counting: true)
                pipe(from: outbound, to: inbound, counting: false)
            case .failed(let error), .waiting(let error):
                close(outboundReady ? "receiver closed: \(error)"
                                    : "receiver not reachable (Start PhoneSaber running?): \(error)")
            default:
                break
            }
        }
        inbound.start(queue: queue)
        outbound.start(queue: queue)
        // An abandoned upload must not keep sockets open forever.
        queue.asyncAfter(deadline: .now() + 300) { close("timeout") }
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
    /// Largest gap between consecutive forwarded coordinates per color in the
    /// current stats interval (gaps over 2 s are the saber being away, not stalls).
    private var lastArrival: [P2PColor: TimeInterval] = [:]
    private var maxGap: [P2PColor: TimeInterval] = [:]
    private var lastPingAt: TimeInterval?
    private var peerLostLogged = true
    private var timer: DispatchSourceTimer?
    private var boundPort: NWEndpoint.Port?
    private var restartAttempts = 0
    private var stopped = false

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
        try startListener(on: NWEndpoint.Port(rawValue: options.listenPort) ?? .any)
        startTimer()
        if let after = options.simulateListenerFailureAfter {
            queue.asyncAfter(deadline: .now() + after) { [weak self] in
                self?.log("simulating a listener failure (test option)")
                self?.listenerFailed("simulated")
            }
        }
    }

    private func startListener(on port: NWEndpoint.Port) throws {
        let parameters = NWParameters.udp
        parameters.includePeerToPeer = true
        if options.loopbackOnly {
            parameters.requiredInterfaceType = .loopback
        }
        let listener = try NWListener(using: parameters, on: port)
        if options.bonjour {
            listener.service = NWListener.Service(name: options.serviceName, type: PhoneSaberP2P.serviceType)
        }
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            guard let self, let listener, self.listener === listener else { return }
            switch state {
            case .ready:
                self.restartAttempts = 0
                if let bound = listener.port { self.boundPort = bound }
                let bound = listener.port.map { "\($0.rawValue)" } ?? "?"
                let service = self.options.bonjour
                    ? "service \"\(self.options.serviceName)\" \(PhoneSaberP2P.serviceType)" : "no Bonjour"
                self.log("listening on UDP \(bound) (\(service), peer-to-peer enabled); forwarding RED→\(self.options.forwardHost):\(self.options.redPort) BLUE→\(self.options.forwardHost):\(self.options.bluePort)")
            case .failed(let error):
                self.listenerFailed("\(error)")
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
    }

    /// A failed listener (e.g. mDNSResponder restarting after sleep/wake) is
    /// rebuilt with backoff instead of exiting: Unity starts the bridge only once
    /// per Play, so exiting would end P2P until the next Play. The previous port
    /// is tried first so the iPhone's resolved address stays valid.
    private func listenerFailed(_ reason: String) {
        listener?.cancel()
        listener = nil
        restartAttempts += 1
        let delay = min(30, pow(2, Double(min(restartAttempts - 1, 5))))
        log("listener failed: \(reason); restarting in \(Int(delay))s (attempt \(restartAttempts))")
        queue.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, !self.stopped, self.listener == nil else { return }
            let preferred = self.boundPort ?? NWEndpoint.Port(rawValue: self.options.listenPort) ?? .any
            do {
                try self.startListener(on: preferred)
            } catch {
                self.log("cannot reopen UDP \(preferred.rawValue): \(error); using any free port")
                do {
                    try self.startListener(on: .any)
                } catch {
                    self.listenerFailed("\(error)")
                }
            }
        }
    }

    func stop() {
        queue.sync {
            stopped = true
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
                if let previous = lastArrival[message.color], time - previous <= 2 {
                    maxGap[message.color] = max(maxGap[message.color] ?? 0, time - previous)
                }
                lastArrival[message.color] = time
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
                    var summary = self.counts.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }
                    for color in [P2PColor.red, .blue] {
                        if let gap = self.maxGap[color] {
                            summary.append("maxGapMs\(color.label)=\(Int((gap * 1000).rounded()))")
                        }
                    }
                    self.log("last \(Int(self.options.statsInterval))s: \(summary.joined(separator: " ")) peers=\(self.peers.count)")
                    self.counts.removeAll()
                    self.maxGap.removeAll()
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
        if options.diagnosticsPort != 0 {
            let relay = DiagnosticsRelay(options: options)
            do {
                try relay.start()
                diagnosticsRelay = relay
            } catch {
                // Coordinates keep working; only P2P triage uploads are unavailable.
                print("[P2P] diag relay: cannot start: \(error)")
            }
        }
        if let parent = options.exitWithParent {
            let watchdog = DispatchSource.makeTimerSource(queue: .main)
            watchdog.schedule(deadline: .now() + 1, repeating: 1)
            watchdog.setEventHandler {
                // kill(pid, 0) only probes; ESRCH means the parent has exited.
                if kill(parent, 0) != 0 && errno == ESRCH {
                    print("[P2P] parent process \(parent) exited; stopping")
                    bridge.stop()
                    exit(0)
                }
            }
            watchdog.resume()
            parentWatchdog = watchdog
            print("[P2P] will exit with parent process \(parent)")
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
    private static var parentWatchdog: DispatchSourceTimer?
    private static var diagnosticsRelay: DiagnosticsRelay?
}
