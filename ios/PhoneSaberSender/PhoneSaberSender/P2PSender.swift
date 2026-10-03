import Foundation
import Network

/// State of the optional peer-to-peer link to the Mac P2P bridge.
enum P2PLinkState: Equatable {
    case disabled
    case searching
    case connecting(service: String)
    case connected(service: String, interface: String)
    case reconnecting(reason: String)
    case failed(reason: String)

    var label: String {
        switch self {
        case .disabled: return "P2P Off"
        case .searching: return "P2P Searching"
        case .connecting(let service): return "P2P Connecting (\(service))"
        case .connected(_, let interface): return "P2P Connected (\(interface))"
        case .reconnecting(let reason): return "P2P Reconnecting (\(reason))"
        case .failed(let reason): return "P2P Failed (\(reason))"
        }
    }

    var isConnected: Bool { if case .connected = self { return true } else { return false } }
}

/// Round-trip time of the liveness pings (ping -> bridge pong), so the UI can
/// show how fast the P2P hop really is (AWDL vs infrastructure Wi-Fi). Pure
/// value logic with explicit timestamps for unit tests.
struct P2PRoundTripStats: Equatable {
    struct Summary: Equatable {
        var lastMs: Double
        var medianMs: Double
        var p95Ms: Double
        var maxMs: Double
        var samples: Int
        /// Pings without a pong within `expireAfter`, over the recent outcomes.
        var lostPercent: Double
    }

    let window: Int
    let expireAfter: TimeInterval
    private var outstanding: [UInt64: TimeInterval] = [:]
    private var roundTrips: [Double] = []
    private var outcomes: [Bool] = []

    init(window: Int = 40, expireAfter: TimeInterval = 2) {
        self.window = window
        self.expireAfter = expireAfter
    }

    mutating func pingSent(_ sequence: UInt64, at now: TimeInterval) {
        outstanding[sequence] = now
    }

    /// Returns the round trip in seconds when the pong answers a known ping.
    @discardableResult
    mutating func pongReceived(_ sequence: UInt64, at now: TimeInterval) -> TimeInterval? {
        guard let sentAt = outstanding.removeValue(forKey: sequence) else { return nil }
        let roundTrip = max(0, now - sentAt)
        append(&roundTrips, roundTrip)
        append(&outcomes, true)
        return roundTrip
    }

    mutating func expire(at now: TimeInterval) {
        for (sequence, sentAt) in outstanding where now - sentAt > expireAfter {
            outstanding.removeValue(forKey: sequence)
            append(&outcomes, false)
        }
    }

    mutating func reset() {
        outstanding.removeAll()
        roundTrips.removeAll()
        outcomes.removeAll()
    }

    var summary: Summary? {
        guard let last = roundTrips.last else { return nil }
        let sorted = roundTrips.sorted()
        func percentile(_ q: Double) -> Double { sorted[min(sorted.count - 1, Int((Double(sorted.count - 1) * q).rounded()))] }
        let lost = outcomes.filter { !$0 }.count
        return Summary(lastMs: last * 1000, medianMs: percentile(0.5) * 1000, p95Ms: percentile(0.95) * 1000,
                       maxMs: (sorted.last ?? 0) * 1000, samples: sorted.count,
                       lostPercent: outcomes.isEmpty ? 0 : Double(lost) * 100 / Double(outcomes.count))
    }

    private func append<T>(_ values: inout [T], _ value: T) {
        values.append(value)
        if values.count > window { values.removeFirst(values.count - window) }
    }
}

/// Sends coordinates to the Mac bridge (`_phonesaber-p2p._udp`) with
/// Network.framework peer-to-peer Wi-Fi allowed (`includePeerToPeer`) and
/// cellular prohibited. It never blocks the caller: all work runs on its own
/// queue, only the newest coordinate per color waits while one is in flight,
/// and `isUsable` (a lock-protected flag) tells the caller whether to use this
/// link or the existing LAN UDPSender. Liveness comes from bridge pongs, never
/// from the UDP connection state alone (UDP "ready" proves nothing about a peer).
final class P2PSender {
    struct Timing {
        var pingInterval: TimeInterval = 0.25
        var liveness = P2PLivenessMonitor.Timing()
        var browserRestartDelay: TimeInterval = 2
    }

    enum SendError: LocalizedError {
        case notUsable, invalidPayload
        var errorDescription: String? {
            switch self {
            case .notUsable: return "P2P link is not connected"
            case .invalidPayload: return "P2P payload is not a coordinate string"
            }
        }
    }

    private struct PendingSend {
        let text: String
        let enqueuedAt: TimeInterval
        let onSendStarted: ((TimeInterval, Int) -> Void)?
        let completion: (Result<TimeInterval, Error>) -> Void
    }

    private let queue = DispatchQueue(label: "PhoneSaberSender.p2p", qos: .userInteractive)
    private let timing: Timing
    private let endpointOverride: NWEndpoint?
    private let clock: () -> TimeInterval
    private let session = UInt32.random(in: 1...UInt32.max)
    private var sequence: UInt64 = 0
    private var running = false
    private var browser: NWBrowser?
    private var browserRestart: DispatchWorkItem?
    private var candidate: (endpoint: NWEndpoint, name: String)?
    private var connection: NWConnection?
    private var connectionGeneration = 0
    private var monitor: P2PLivenessMonitor
    private var ticker: DispatchSourceTimer?
    private var inFlight: Set<P2PColor> = []
    private var pending: [P2PColor: PendingSend] = [:]
    private var supersededCount = 0
    private var state: P2PLinkState = .disabled
    private var interfaceName = "?"
    private var stateHandler: ((P2PLinkState) -> Void)?
    private var statsHandler: ((P2PRoundTripStats.Summary?) -> Void)?
    private var roundTrips = P2PRoundTripStats()
    private var lastStatsPublishedAt: TimeInterval = -.infinity

    private let usableLock = NSLock()
    private var usableFlag = false

    /// `endpointOverride` skips Bonjour browsing (tests, or a fixed bridge address).
    init(timing: Timing = Timing(), endpointOverride: NWEndpoint? = nil,
         clock: @escaping () -> TimeInterval = { HostMonotonicClock.now() }) {
        self.timing = timing
        self.endpointOverride = endpointOverride
        self.clock = clock
        self.monitor = P2PLivenessMonitor(timing: timing.liveness)
    }

    /// Cheap, thread-safe: whether coordinates should take the P2P link now.
    var isUsable: Bool {
        usableLock.lock(); defer { usableLock.unlock() }
        return usableFlag
    }

    /// `onStats` receives the ping round-trip summary about once per second
    /// (nil while there is no measurement, e.g. after a reconnect).
    func start(onState: ((P2PLinkState) -> Void)? = nil,
               onStats: ((P2PRoundTripStats.Summary?) -> Void)? = nil) {
        queue.async { [weak self] in
            guard let self, !self.running else { return }
            self.running = true
            self.stateHandler = onState
            self.statsHandler = onStats
            self.startTicker()
            if let endpointOverride = self.endpointOverride {
                self.candidate = (endpointOverride, "\(endpointOverride)")
                self.connect()
            } else {
                self.startBrowser()
            }
        }
    }

    func stop() {
        queue.sync {
            running = false
            browserRestart?.cancel(); browserRestart = nil
            browser?.cancel(); browser = nil
            tearDownConnection()
            ticker?.cancel(); ticker = nil
            candidate = nil
            pending.removeAll()
            setState(.disabled)
        }
    }

    /// `fallback` runs (on this sender's queue) when the link stopped being usable
    /// after the caller checked `isUsable`; it should hand the coordinate to LAN.
    func send(_ text: String, to port: Int,
              onSendStarted: ((TimeInterval, Int) -> Void)? = nil,
              completion: @escaping (Result<TimeInterval, Error>) -> Void,
              fallback: (() -> Void)? = nil) {
        let request = PendingSend(text: text, enqueuedAt: HostMonotonicClock.now(),
                                  onSendStarted: onSendStarted, completion: completion)
        queue.async { [weak self] in
            guard let self else { return }
            guard let color = P2PColor(port: port) else {
                completion(.failure(SendError.invalidPayload))
                return
            }
            guard self.running, self.connection != nil, self.monitor.isUsable(at: self.clock()) else {
                if let fallback { fallback() } else { completion(.failure(SendError.notUsable)) }
                return
            }
            if self.inFlight.contains(color) {
                // Newest coordinate wins; like UDPSender, a superseded one is dropped silently.
                if self.pending.updateValue(request, forKey: color) != nil { self.supersededCount += 1 }
            } else {
                self.transmit(request, color: color)
            }
        }
    }

    // MARK: Discovery

    private func parameters() -> NWParameters {
        let parameters = NWParameters.udp
        parameters.includePeerToPeer = true
        // The link must never use mobile data; Wi-Fi infrastructure and AWDL are fine.
        parameters.prohibitedInterfaceTypes = [.cellular]
        return parameters
    }

    private func startBrowser() {
        guard running, browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: PhoneSaberP2P.serviceType, domain: nil),
                                using: parameters())
        self.browser = browser
        if connection == nil { setState(.searching) }
        browser.stateUpdateHandler = { [weak self, weak browser] newState in
            guard let self else { return }
            self.queue.async {
                guard self.browser === browser else { return }
                switch newState {
                case .failed(let error):
                    self.log("browser failed: \(error)")
                    self.browser?.cancel(); self.browser = nil
                    if self.connection == nil { self.setState(.failed(reason: "browse: \(error)")) }
                    self.scheduleBrowserRestart()
                case .waiting(let error):
                    if self.connection == nil { self.setState(.failed(reason: "browse waiting: \(error)")) }
                default:
                    break
                }
            }
        }
        browser.browseResultsChangedHandler = { [weak self, weak browser] results, _ in
            guard let self else { return }
            self.queue.async {
                guard self.browser === browser else { return }
                self.handle(results: results)
            }
        }
        browser.start(queue: queue)
    }

    private func scheduleBrowserRestart() {
        guard running, browserRestart == nil else { return }
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.browserRestart = nil
            self.startBrowser()
        }
        browserRestart = item
        queue.asyncAfter(deadline: .now() + timing.browserRestartDelay, execute: item)
    }

    private func handle(results: Set<NWBrowser.Result>) {
        // Deterministic choice: the alphabetically first service name.
        let services = results.compactMap { result -> (NWEndpoint, String)? in
            if case .service(let name, _, _, _) = result.endpoint { return (result.endpoint, name) }
            return nil
        }.sorted { $0.1 < $1.1 }
        guard let first = services.first else {
            // Keep a live connection; liveness decides when it is gone.
            candidate = nil
            if connection == nil { setState(.searching) }
            return
        }
        let changed = candidate?.name != first.1
        candidate = (first.0, first.1)
        if connection == nil || (changed && !monitor.isUsable(at: clock())) {
            connect()
        }
    }

    // MARK: Connection

    private func connect() {
        guard running, let candidate else { return }
        tearDownConnection()
        connectionGeneration += 1
        let generation = connectionGeneration
        let connection = NWConnection(to: candidate.endpoint, using: parameters())
        self.connection = connection
        monitor.connectionStarted(at: clock())
        setState(.connecting(service: candidate.name))
        log("connecting to \(candidate.name)")
        connection.stateUpdateHandler = { [weak self] newState in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation else { return }
                switch newState {
                case .ready:
                    self.interfaceName = self.describeInterface(connection)
                    self.receive(on: connection, generation: generation)
                    self.sendPing()
                case .failed(let error):
                    self.log("connection failed: \(error)")
                    self.dropConnection(reason: "failed")
                case .waiting(let error):
                    self.log("connection waiting: \(error)")
                default:
                    break
                }
            }
        }
        connection.start(queue: queue)
    }

    private func describeInterface(_ connection: NWConnection) -> String {
        guard let path = connection.currentPath else { return "?" }
        if let interface = path.availableInterfaces.first(where: { path.usesInterfaceType($0.type) }) {
            return interface.name
        }
        return path.availableInterfaces.first?.name ?? "?"
    }

    private func receive(on connection: NWConnection, generation: Int) {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation else { return }
                if let data, case .success(let message) = P2PMessage.decode(data),
                   message.kind == .pong, message.session == self.session {
                    let now = self.clock()
                    self.roundTrips.pongReceived(message.sequence, at: now)
                    self.monitor.pongReceived(at: now)
                    self.refreshUsability()
                }
                if error == nil { self.receive(on: connection, generation: generation) }
            }
        }
    }

    private func tearDownConnection() {
        connectionGeneration += 1
        connection?.cancel()
        connection = nil
        monitor.reset()
        roundTrips.reset()
        statsHandler?(nil)
        inFlight.removeAll()
        pending.removeAll()  // stale coordinates are not worth delivering late
        refreshUsability()
    }

    private func dropConnection(reason: String) {
        tearDownConnection()
        setState(.reconnecting(reason: reason))
        if endpointOverride == nil && browser == nil { scheduleBrowserRestart() }
        // The next ticker pass reconnects to the current candidate.
    }

    // MARK: Liveness

    private func startTicker() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + timing.pingInterval, repeating: timing.pingInterval)
        timer.setEventHandler { [weak self] in self?.tick() }
        ticker = timer
        timer.resume()
    }

    private func tick() {
        guard running else { return }
        let now = clock()
        if connection == nil {
            if candidate != nil { connect() }
            return
        }
        if monitor.shouldReconnect(at: now) {
            log("no pong; reconnecting")
            dropConnection(reason: "no pong")
            return
        }
        sendPing()
        refreshUsability()
        roundTrips.expire(at: now)
        if now - lastStatsPublishedAt >= 1 {
            lastStatsPublishedAt = now
            statsHandler?(roundTrips.summary)
        }
    }

    private func sendPing() {
        guard let connection else { return }
        sequence &+= 1
        roundTrips.pingSent(sequence, at: clock())
        connection.send(content: P2PMessage.ping(session: session, sequence: sequence).encoded(),
                        completion: .idempotent)
    }

    private func refreshUsability() {
        let usable = connection != nil && monitor.isUsable(at: clock())
        usableLock.lock()
        let changed = usableFlag != usable
        usableFlag = usable
        usableLock.unlock()
        if usable {
            setState(.connected(service: candidate?.name ?? "bridge", interface: interfaceName))
        } else if changed, connection != nil {
            setState(.reconnecting(reason: "no pong"))
        }
        if changed { log(usable ? "connected via \(interfaceName)" : "not usable; LAN fallback") }
    }

    // MARK: Sending

    private func transmit(_ request: PendingSend, color: P2PColor) {
        guard let connection else {
            request.completion(.failure(SendError.notUsable))
            return
        }
        sequence &+= 1
        guard let message = P2PMessage.coordinates(request.text, color: color, session: session,
                                                   sequence: sequence) else {
            request.completion(.failure(SendError.invalidPayload))
            return
        }
        inFlight.insert(color)
        request.onSendStarted?(max(0, HostMonotonicClock.now() - request.enqueuedAt), supersededCount)
        let generation = connectionGeneration
        connection.send(content: message.encoded(), completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation else { return }
                self.inFlight.remove(color)
                request.completion(error.map { .failure($0) } ?? .success(ProcessInfo.processInfo.systemUptime))
                if let next = self.pending.removeValue(forKey: color) { self.transmit(next, color: color) }
            }
        })
    }

    private func setState(_ newState: P2PLinkState) {
        guard state != newState else { return }
        state = newState
        stateHandler?(newState)
    }

    private func log(_ message: String) {
#if DEBUG
        print("[P2P] \(message)")
#endif
    }

    // MARK: Testing

    var stateForTesting: P2PLinkState { queue.sync { state } }
    var roundTripSummaryForTesting: P2PRoundTripStats.Summary? { queue.sync { roundTrips.summary } }
    var supersededCountForTesting: Int { queue.sync { supersededCount } }
    var sessionForTesting: UInt32 { session }
}
