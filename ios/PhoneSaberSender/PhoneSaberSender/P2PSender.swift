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
        case .connected(let service, let interface): return "P2P Connected (\(interface) · \(service))"
        case .reconnecting(let reason): return "P2P Reconnecting (\(reason))"
        case .failed(let reason): return "P2P Failed (\(reason))"
        }
    }

    var isConnected: Bool { if case .connected = self { return true } else { return false } }
}

/// Which advertised bridge to use when several Macs publish
/// `_phonesaber-p2p._udp`. Locks onto the first chosen service name and keeps
/// it while it is still advertised, so a second Mac whose name sorts first
/// never steals a working link; switches only when the locked one disappears.
/// Plain names so it is unit-testable without Bonjour.
struct P2PServiceSelection: Equatable {
    private(set) var locked: String?

    /// Returns the service to use, or nil when nothing is advertised (the lock
    /// is kept, so the same Mac wins again when it comes back with others).
    mutating func choose(from available: [String]) -> String? {
        if let locked, available.contains(locked) { return locked }
        guard let first = available.min() else { return nil }
        locked = first
        return first
    }

    mutating func reset() { locked = nil }
}

/// Exponential reconnect delay (immediate connection failures used to retry
/// about four times per second). Reset by the first pong of a connection.
struct P2PReconnectBackoff: Equatable {
    let initial: TimeInterval
    let maximum: TimeInterval
    private(set) var nextDelay: TimeInterval
    private(set) var notBefore: TimeInterval = -.infinity

    init(initial: TimeInterval = 0.25, maximum: TimeInterval = 4) {
        self.initial = initial
        self.maximum = maximum
        self.nextDelay = initial
    }

    /// Records a lost / failed connection; returns the delay before the next attempt.
    @discardableResult
    mutating func connectionLost(at now: TimeInterval) -> TimeInterval {
        let delay = nextDelay
        notBefore = now + delay
        nextDelay = min(maximum, nextDelay * 2)
        return delay
    }

    func mayConnect(at now: TimeInterval) -> Bool { now >= notBefore }

    mutating func reset() {
        nextDelay = initial
        notBefore = -.infinity
    }
}

/// Name of the interface a P2P connection really uses, for the UI. AWDL and
/// infrastructure Wi-Fi share the `.wifi` type, so a link-local endpoint scope
/// ("fe80::...%awdl0") is preferred over the interface-type match.
enum P2PInterfaceLabel {
    static func pick(scoped: [String?], available: [(name: String, used: Bool)]) -> String {
        if let name = scoped.compactMap({ $0 }).first(where: { !$0.isEmpty }) { return name }
        if let used = available.first(where: { $0.used }) { return used.name }
        return available.first?.name ?? "?"
    }
}

/// UI text for NWBrowser problems. The Local Network permission denial
/// (kDNSServiceErr_PolicyDenied) gets an actionable Japanese message.
enum P2PBrowseErrorText {
    static let policyDeniedCode: Int32 = -65570  // kDNSServiceErr_PolicyDenied
    static let localNetworkDenied = "ローカルネットワークの許可が必要: 設定 > PhoneSaberSender"

    static func describe(dnsCode: Int32?, fallback: String, waiting: Bool) -> String {
        if dnsCode == policyDeniedCode { return localNetworkDenied }
        return (waiting ? "browse waiting: " : "browse: ") + fallback
    }

    static func describe(_ error: NWError, waiting: Bool) -> String {
        var code: Int32?
        if case .dns(let dnsCode) = error { code = dnsCode }
        return describe(dnsCode: code, fallback: "\(error)", waiting: waiting)
    }
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
        /// A browser stuck in `.waiting` (e.g. Local Network permission denied)
        /// is rebuilt this often, so granting the permission is picked up.
        var browserWaitingRetryDelay: TimeInterval = 10
        /// A coordinate whose send has not completed after this long means the
        /// link is stuck even if pings still get through: drop it, use LAN.
        /// Well above the AWDL stalls measured on 2026-10-03 (170-300 ms, up to
        /// ~1 s), which recover by themselves: reconnecting there would turn a
        /// short stall into a longer outage. This only catches a real hang.
        var sendWatchdogTimeout: TimeInterval = 3
        var reconnectInitialDelay: TimeInterval = 0.25
        var reconnectMaximumDelay: TimeInterval = 4
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

    /// Replaces `NWConnection.send` for coordinate datagrams (tests only);
    /// call the completion when the datagram is processed, or never to simulate
    /// a stuck link. Pings always use the real connection.
    typealias CoordinateSendHook = (_ datagram: Data, _ completion: @escaping (Error?) -> Void) -> Void

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
    private let coordinateSendHook: CoordinateSendHook?
    private let session = UInt32.random(in: 1...UInt32.max)
    private var sequence: UInt64 = 0
    private var running = false
    private var browser: NWBrowser?
    private var browserRestart: DispatchWorkItem?
    private var selection = P2PServiceSelection()
    private var candidate: (endpoint: NWEndpoint, name: String)?
    private var connection: NWConnection?
    /// Service name the current connection was opened to (the UI shows it).
    private var connectedService = ""
    private var connectionGeneration = 0
    private var connectCount = 0
    private var monitor: P2PLivenessMonitor
    private var backoff: P2PReconnectBackoff
    private var ticker: DispatchSourceTimer?
    private var nextSendID: UInt64 = 0
    private var inFlight: [P2PColor: UInt64] = [:]
    private var sendWatchdogs: [P2PColor: DispatchWorkItem] = [:]
    private var stalledSendCount = 0
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
    private var usableOverride: Bool?

    /// `endpointOverride` skips Bonjour browsing (tests, or a fixed bridge address).
    init(timing: Timing = Timing(), endpointOverride: NWEndpoint? = nil,
         clock: @escaping () -> TimeInterval = { HostMonotonicClock.now() },
         coordinateSendHook: CoordinateSendHook? = nil) {
        self.timing = timing
        self.endpointOverride = endpointOverride
        self.clock = clock
        self.coordinateSendHook = coordinateSendHook
        self.monitor = P2PLivenessMonitor(timing: timing.liveness)
        self.backoff = P2PReconnectBackoff(initial: timing.reconnectInitialDelay,
                                           maximum: timing.reconnectMaximumDelay)
    }

    /// Cheap, thread-safe: whether coordinates should take the P2P link now.
    var isUsable: Bool {
        usableLock.lock(); defer { usableLock.unlock() }
        return usableOverride ?? usableFlag
    }

    /// `onStats` receives the ping round-trip summary about once per second
    /// (nil while there is no measurement, e.g. after a reconnect).
    /// Both handlers run on this sender's private queue.
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

    /// Synchronous on the private queue: must never be called from this
    /// sender's own callbacks (`onState`, `onStats`, completions, fallbacks),
    /// which run on that queue and would deadlock in `queue.sync`.
    func stop() {
        queue.sync {
            running = false
            browserRestart?.cancel(); browserRestart = nil
            browser?.cancel(); browser = nil
            tearDownConnection()
            ticker?.cancel(); ticker = nil
            candidate = nil
            selection.reset()
            monitor.reset()
            backoff.reset()
            pending.removeAll()
            setState(.disabled)
        }
    }

    /// Foreground return: the app may have been suspended with the link dead.
    /// A link with a recent pong just gets an immediate ping; otherwise the
    /// connection is rebuilt now (no backoff) and must earn usability again
    /// with fresh pongs. Browsing resumes when no link is usable.
    func recoverIfNeeded() {
        queue.async { [weak self] in
            guard let self, self.running else { return }
            if self.connection != nil && self.monitor.isUsable(at: self.clock()) {
                self.sendPing()
                return
            }
            self.log("foreground: rebuilding the link")
            self.backoff.reset()
            if self.candidate != nil {
                self.connect()
            } else {
                self.tearDownConnection()
                self.setState(.searching)
            }
            self.resumeBrowser()
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
            if self.inFlight[color] != nil {
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
        // Real-time traffic class: asks the OS to treat this as latency-sensitive.
        // Field data (2026-10-03): over awdl0 while the Mac stayed on school Wi-Fi,
        // coordinates stalled 170-300 ms (up to ~1 s) every 10 s window as the Wi-Fi
        // radio alternated channels. This is a hint only; measure maxGapMs on device.
        parameters.serviceClass = .interactiveVoice
        return parameters
    }

    private func startBrowser() {
        guard running, browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: PhoneSaberP2P.serviceType, domain: nil),
                                using: parameters())
        self.browser = browser
        if connection == nil && candidate == nil { setState(.searching) }
        browser.stateUpdateHandler = { [weak self, weak browser] newState in
            guard let self else { return }
            self.queue.async {
                guard let browser, self.browser === browser else { return }
                switch newState {
                case .ready:
                    // Browsing works (again): clear a stale "waiting"/"failed" label.
                    self.browserRestart?.cancel(); self.browserRestart = nil
                    if self.connection == nil, case .failed = self.state {
                        let next: P2PLinkState = self.candidate.map { .connecting(service: $0.name) } ?? .searching
                        self.setState(next)
                    }
                case .failed(let error):
                    self.log("browser failed: \(error)")
                    self.browser?.cancel(); self.browser = nil
                    if self.connection == nil {
                        self.setState(.failed(reason: P2PBrowseErrorText.describe(error, waiting: false)))
                    }
                    self.scheduleBrowserRestart(after: self.timing.browserRestartDelay)
                case .waiting(let error):
                    self.log("browser waiting: \(error)")
                    if self.connection == nil {
                        self.setState(.failed(reason: P2PBrowseErrorText.describe(error, waiting: true)))
                    }
                    // Keep retrying slowly in case the OS does not resume this browser.
                    self.scheduleBrowserRestart(after: self.timing.browserWaitingRetryDelay, replacing: browser)
                default:
                    break
                }
            }
        }
        browser.browseResultsChangedHandler = { [weak self, weak browser] results, _ in
            guard let self else { return }
            self.queue.async {
                guard let browser, self.browser === browser else { return }
                self.handle(results: results)
            }
        }
        browser.start(queue: queue)
    }

    /// Rebuilds the browser after `delay`. With `replacing`, only if that
    /// browser is still the current one (it did not become ready meanwhile).
    private func scheduleBrowserRestart(after delay: TimeInterval, replacing waiting: NWBrowser? = nil) {
        guard running, browserRestart == nil else { return }
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.browserRestart = nil
            if let waiting {
                guard self.browser === waiting else { return }
                waiting.cancel(); self.browser = nil
            }
            self.startBrowser()
        }
        browserRestart = item
        queue.asyncAfter(deadline: .now() + delay, execute: item)
    }

    /// Browsing is paused while the link is usable (less radio / mDNS work on
    /// the AWDL channel) and resumed as soon as it is not.
    private func pauseBrowser() {
        guard endpointOverride == nil, browser != nil || browserRestart != nil else { return }
        browserRestart?.cancel(); browserRestart = nil
        browser?.cancel(); browser = nil
        log("browser paused (link usable)")
    }

    private func resumeBrowser() {
        guard running, endpointOverride == nil, browser == nil, browserRestart == nil else { return }
        log("browser resumed")
        startBrowser()
    }

    private func handle(results: Set<NWBrowser.Result>) {
        let services = results.compactMap { result -> (endpoint: NWEndpoint, name: String)? in
            if case .service(let name, _, _, _) = result.endpoint { return (result.endpoint, name) }
            return nil
        }
        guard let name = selection.choose(from: services.map(\.name)),
              let chosen = services.first(where: { $0.name == name }) else {
            // Keep a live connection; liveness decides when it is gone.
            candidate = nil
            if connection == nil { setState(.searching) }
            return
        }
        let changed = candidate?.name != chosen.name
        candidate = (chosen.endpoint, chosen.name)
        let now = clock()
        if connection == nil {
            if backoff.mayConnect(at: now) { connect() }  // otherwise the ticker connects
        } else if changed && !monitor.isUsable(at: now) {
            connect()
        }
    }

    // MARK: Connection

    private func connect() {
        guard running, let candidate else { return }
        tearDownConnection()
        connectionGeneration += 1
        connectCount += 1
        let generation = connectionGeneration
        let connection = NWConnection(to: candidate.endpoint, using: parameters())
        self.connection = connection
        connectedService = candidate.name
        interfaceName = "?"
        monitor.connectionStarted(at: clock())
        setState(.connecting(service: candidate.name))
        log("connecting to \(candidate.name)")
        connection.stateUpdateHandler = { [weak self] newState in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation else { return }
                switch newState {
                case .ready:
                    if let path = connection.currentPath { self.interfaceName = Self.describeInterface(path) }
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
        connection.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation else { return }
                let name = Self.describeInterface(path)
                guard name != self.interfaceName else { return }
                self.log("path changed: \(self.interfaceName) -> \(name)")
                self.interfaceName = name
                self.refreshUsability()
            }
        }
        connection.start(queue: queue)
    }

    private static func describeInterface(_ path: NWPath) -> String {
        func scope(_ endpoint: NWEndpoint?) -> String? {
            guard case .hostPort(let host, _)? = endpoint else { return nil }
            switch host {
            case .ipv6(let address): return address.interface?.name
            case .ipv4(let address): return address.interface?.name
            default: return nil
            }
        }
        return P2PInterfaceLabel.pick(
            scoped: [scope(path.localEndpoint), scope(path.remoteEndpoint)],
            available: path.availableInterfaces.map { ($0.name, path.usesInterfaceType($0.type)) })
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
                    self.backoff.reset()
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
        monitor.connectionEnded()
        roundTrips.reset()
        statsHandler?(nil)
        sendWatchdogs.values.forEach { $0.cancel() }
        sendWatchdogs.removeAll()
        inFlight.removeAll()
        pending.removeAll()  // stale coordinates are not worth delivering late
        refreshUsability()
    }

    private func dropConnection(reason: String) {
        tearDownConnection()
        let delay = backoff.connectionLost(at: clock())
        log("reconnect in \(delay)s (\(reason))")
        resumeBrowser()
        setState(.reconnecting(reason: reason))
        // The next ticker pass after the backoff reconnects to the current candidate.
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
            if candidate != nil && backoff.mayConnect(at: now) { connect() }
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
            setState(.connected(service: connectedService, interface: interfaceName))
        } else if changed, connection != nil {
            setState(.reconnecting(reason: "no pong"))
        }
        if changed {
            log(usable ? "connected via \(interfaceName)" : "not usable; LAN fallback")
            if usable { pauseBrowser() } else { resumeBrowser() }
        }
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
        nextSendID &+= 1
        let sendID = nextSendID
        inFlight[color] = sendID
        request.onSendStarted?(max(0, HostMonotonicClock.now() - request.enqueuedAt), supersededCount)
        let generation = connectionGeneration
        scheduleSendWatchdog(color: color, sendID: sendID, generation: generation)
        let finished: (Error?) -> Void = { [weak self] error in
            guard let self else { return }
            self.queue.async {
                guard self.connectionGeneration == generation, self.inFlight[color] == sendID else { return }
                self.inFlight[color] = nil
                self.sendWatchdogs.removeValue(forKey: color)?.cancel()
                request.completion(error.map { .failure($0) } ?? .success(ProcessInfo.processInfo.systemUptime))
                if let next = self.pending.removeValue(forKey: color) { self.transmit(next, color: color) }
            }
        }
        if let coordinateSendHook {
            coordinateSendHook(message.encoded(), finished)
        } else {
            connection.send(content: message.encoded(), completion: .contentProcessed { finished($0) })
        }
    }

    /// A send that never completes would hold its color forever while pings
    /// keep the link "usable": after `sendWatchdogTimeout` the connection is
    /// dropped (LAN takes over) and rebuilt. The stuck coordinate is dropped
    /// silently, like a superseded one.
    private func scheduleSendWatchdog(color: P2PColor, sendID: UInt64, generation: Int) {
        sendWatchdogs.removeValue(forKey: color)?.cancel()
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.running, self.connectionGeneration == generation,
                  self.inFlight[color] == sendID else { return }
            self.stalledSendCount += 1
            self.log("\(color.label) send stalled for \(self.timing.sendWatchdogTimeout)s; dropping the link")
            self.dropConnection(reason: "send stalled")
        }
        sendWatchdogs[color] = item
        queue.asyncAfter(deadline: .now() + timing.sendWatchdogTimeout, execute: item)
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
    var stalledSendCountForTesting: Int { queue.sync { stalledSendCount } }
    var connectCountForTesting: Int { queue.sync { connectCount } }
    var sessionForTesting: UInt32 { session }

    /// Forces what `isUsable` reports to callers (nil: real value), so a test can
    /// reproduce "usable when checked, gone when sent" without timing races.
    func overrideUsabilityForTesting(_ value: Bool?) {
        usableLock.lock(); usableOverride = value; usableLock.unlock()
    }
}
