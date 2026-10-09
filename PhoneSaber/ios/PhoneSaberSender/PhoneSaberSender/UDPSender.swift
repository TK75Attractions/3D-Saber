import Foundation
import Network

final class UDPSender {
    typealias SendHandler = (String, Int, @escaping (Result<TimeInterval, Error>) -> Void) -> Void

    struct Timing {
        var waitingTimeout: TimeInterval = 8
        var sendWatchdogTimeout: TimeInterval = 2
        var reconnectInitialDelay: TimeInterval = 0.5
        var reconnectMaximumDelay: TimeInterval = 8
    }

    struct Snapshot {
        let states: [Int: String]
        let errors: [Int: String]
        let lastError: String?
    }

    enum SendError: LocalizedError {
        case notConfigured(host: String, port: Int)
        case invalidPayload(host: String, port: Int)

        var errorDescription: String? {
            switch self {
            case .notConfigured(let host, let port):
                return "UDP送信先未設定 (\(host.isEmpty ? "未設定" : host):\(port))"
            case .invalidPayload(let host, let port):
                return "UDP送信失敗 (\(host):\(port)): 座標をASCIIへ変換できません"
            }
        }
    }

    enum ConnectionStateForTesting { case ready, waiting, failed }
    private enum PortPhase { case preparing, ready, waiting, failed, stopped }

    private struct PendingSend {
        let id: UInt64
        let text: String
        let enqueuedAt: TimeInterval
        let onSendStarted: ((TimeInterval, Int) -> Void)?
        let isCurrent: (() -> Bool)?
        let completion: (Result<TimeInterval, Error>) -> Void
    }

    private let queue = DispatchQueue(label: "PhoneSaberSender.udp", qos: .userInteractive)
    private let sendHandler: SendHandler?
    private let timing: Timing
    private var connections: [Int: NWConnection] = [:]
    private var states: [Int: String] = [:]
    private var phases: [Int: PortPhase] = [:]
    private var connectionErrors: [Int: String] = [:]
    private var sendErrors: [Int: String] = [:]
    private var configuredHost = ""
    private var configuredPorts: Set<Int> = []
    private var isRunning = false
    private var configurationGeneration = 0
    private var portGenerations: [Int: Int] = [:]
    private var reconnectAttempts: [Int: Int] = [:]
    private var reconnectCounts: [Int: Int] = [:]
    private var watchdogTimeoutCounts: [Int: Int] = [:]
    private var waitingSince: [Int: TimeInterval] = [:]
    private var connectionCreatedAt: [Int: TimeInterval] = [:]
    private var reconnectWork: [Int: DispatchWorkItem] = [:]
    private var waitingWork: [Int: DispatchWorkItem] = [:]
    private var watchdogWork: [Int: DispatchWorkItem] = [:]
    private var activeByPort: [Int: PendingSend] = [:]
    private var pendingByPort: [Int: PendingSend] = [:]
    private var nextSendID: UInt64 = 0
    private var updateHandler: (([Int: String], [Int: String], String?) -> Void)?
    private var updateHandlersForTesting: [(( [Int: String], [Int: String], String?) -> Void)?] = []
    private var rejectedCompletionCount = 0
    private var supersededPendingCount = 0
    private var discardedForP2PCount = 0
#if DEBUG
    private var freezeDiagnosticsEnabled = false
    private var lastSendStartByPort: [Int: TimeInterval] = [:]
#endif

    init(sendHandler: SendHandler? = nil, timing: Timing = Timing()) {
        self.sendHandler = sendHandler
        self.timing = timing
    }

    func configure(host: String, ports: [Int] = [5005, 5006],
                   onUpdate: (([Int: String], [Int: String], String?) -> Void)? = nil) {
        queue.sync {
            stopLocked(clearHandler: false)
            configurationGeneration += 1
            configuredHost = host
            configuredPorts = Set(ports)
            isRunning = true
            updateHandler = onUpdate
            updateHandlersForTesting.append(onUpdate)
            states = Dictionary(uniqueKeysWithValues: ports.map { ($0, "preparing") })
            phases = Dictionary(uniqueKeysWithValues: ports.map { ($0, .preparing) })
            connectionErrors = [:]
            sendErrors = [:]
            supersededPendingCount = 0
            for port in ports { createConnection(port: port, isReconnect: false) }
            publish()
        }
    }

    func updateHost(_ host: String) {
        queue.async { [weak self] in
            guard let self, self.isRunning, !host.isEmpty, host != self.configuredHost else { return }
            self.configurationGeneration += 1
            self.configuredHost = host
            for port in self.configuredPorts {
                self.prepareLatestForRecovery(port: port)
                self.cancelPortWork(port: port)
                self.connections.removeValue(forKey: port)?.cancel()
                self.createConnection(port: port, isReconnect: true)
            }
#if DEBUG
            print("[UDP] host update source=bonjour resolved=\(host) generation=\(self.configurationGeneration)")
#endif
            self.publish()
        }
    }

    func send(_ text: String, to port: Int,
              onSendStarted: ((TimeInterval, Int) -> Void)? = nil,
              isCurrent: (() -> Bool)? = nil,
              completion: @escaping (Result<TimeInterval, Error>) -> Void) {
        let enqueuedAt = HostMonotonicClock.now()
        queue.async { [weak self] in
            guard let self, isCurrent?() != false else { return }
            self.nextSendID &+= 1
            let request = PendingSend(id: self.nextSendID, text: text,
                                      enqueuedAt: enqueuedAt,
                                      onSendStarted: onSendStarted, isCurrent: isCurrent, completion: completion)
            guard self.isRunning, self.configuredPorts.contains(port) else {
                completion(.failure(SendError.notConfigured(host: self.configuredHost, port: port)))
                return
            }
            if self.activeByPort[port] != nil || self.phases[port] != .ready {
                if self.pendingByPort.updateValue(request, forKey: port) != nil {
                    self.supersededPendingCount += 1
                }
            } else {
                self.start(request, port: port)
            }
        }
    }

    private func createConnection(port: Int, isReconnect: Bool) {
        guard isRunning, configuredPorts.contains(port) else { return }
        portGenerations[port, default: 0] += 1
        let portGeneration = portGenerations[port, default: 0]
        let configureGeneration = configurationGeneration
        phases[port] = .preparing
        states[port] = "preparing"
        connectionCreatedAt[port] = HostMonotonicClock.now()
        connectionErrors[port] = nil
        waitingSince[port] = nil
        if isReconnect { reconnectCounts[port, default: 0] += 1 }
#if DEBUG
        print("[UDP][\(port)] connection generation=\(portGeneration) reconnect=\(reconnectCounts[port, default: 0]) host=\(configuredHost)")
#endif
        if sendHandler != nil {
            handleState(.ready, port: port, configurationGeneration: configureGeneration,
                        portGeneration: portGeneration, reason: nil)
            return
        }
        guard let networkPort = NWEndpoint.Port(rawValue: UInt16(port)) else { return }
        let connection = NWConnection(host: NWEndpoint.Host(configuredHost), port: networkPort, using: .udp)
        connections[port] = connection
        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self else { return }
            self.queue.async {
                guard self.connections[port] === connection else { return }
                switch state {
                case .ready:
                    self.handleState(.ready, port: port, configurationGeneration: configureGeneration,
                                     portGeneration: portGeneration, reason: nil)
                case .waiting(let error):
                    self.handleState(.waiting, port: port, configurationGeneration: configureGeneration,
                                     portGeneration: portGeneration, reason: error.localizedDescription)
                case .failed(let error):
                    self.handleState(.failed, port: port, configurationGeneration: configureGeneration,
                                     portGeneration: portGeneration, reason: error.localizedDescription)
                case .cancelled:
                    if self.isRunning && self.portGenerations[port] == portGeneration {
                        self.states[port] = "cancelled"
                        self.phases[port] = .stopped
                        self.scheduleReconnect(port: port, reason: "unexpected cancellation")
                    }
                default:
                    self.states[port] = String(describing: state)
                    self.publish()
                }
            }
        }
        connection.start(queue: queue)
    }

    private func handleState(_ phase: PortPhase, port: Int, configurationGeneration: Int,
                             portGeneration: Int, reason: String?) {
        guard isRunning, self.configurationGeneration == configurationGeneration,
              portGenerations[port] == portGeneration else { return }
        phases[port] = phase
        switch phase {
        case .ready:
            waitingWork.removeValue(forKey: port)?.cancel()
            waitingSince[port] = nil
            reconnectAttempts[port] = 0
            states[port] = sendHandler == nil ? "ready (送信可能)" : "ready (テスト送信)"
            connectionErrors[port] = nil
            if let latest = pendingByPort.removeValue(forKey: port), activeByPort[port] == nil {
                start(latest, port: port)
            }
        case .waiting:
            states[port] = "waiting"
            connectionErrors[port] = "UDP接続待機 (\(configuredHost):\(port)): \(reason ?? "reason unavailable")"
            if waitingSince[port] == nil {
                waitingSince[port] = HostMonotonicClock.now()
                scheduleWaitingTimeout(port: port, configurationGeneration: configurationGeneration,
                                       portGeneration: portGeneration)
            }
#if DEBUG
            print("[UDP][\(port)] waiting reason=\(reason ?? "unknown")")
#endif
        case .failed:
            states[port] = "failed"
            connectionErrors[port] = "UDP接続失敗 (\(configuredHost):\(port)): \(reason ?? "reason unavailable")"
#if DEBUG
            print("[UDP][\(port)] failed reason=\(reason ?? "unknown")")
#endif
            scheduleReconnect(port: port, reason: "failed")
        default:
            break
        }
        publish()
    }

    private func scheduleWaitingTimeout(port: Int, configurationGeneration: Int,
                                        portGeneration: Int) {
        waitingWork.removeValue(forKey: port)?.cancel()
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.isRunning,
                  self.configurationGeneration == configurationGeneration,
                  self.portGenerations[port] == portGeneration,
                  self.phases[port] == .waiting else { return }
            self.scheduleReconnect(port: port, reason: "waiting timeout")
        }
        waitingWork[port] = item
        queue.asyncAfter(deadline: .now() + timing.waitingTimeout, execute: item)
    }

    private func scheduleReconnect(port: Int, reason: String) {
        guard isRunning, configuredPorts.contains(port), reconnectWork[port] == nil else { return }
        prepareLatestForRecovery(port: port)
        waitingWork.removeValue(forKey: port)?.cancel()
        watchdogWork.removeValue(forKey: port)?.cancel()
        connections.removeValue(forKey: port)?.cancel()
        phases[port] = .failed
        let attempt = reconnectAttempts[port, default: 0]
        let delay = min(timing.reconnectMaximumDelay,
                        timing.reconnectInitialDelay * pow(2, Double(min(attempt, 8))))
        reconnectAttempts[port] = attempt + 1
        states[port] = "reconnecting"
#if DEBUG
        print("[UDP][\(port)] reconnect scheduled reason=\(reason) delay=\(delay)s attempt=\(attempt + 1)")
#endif
        let configureGeneration = configurationGeneration
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.isRunning, self.configurationGeneration == configureGeneration else { return }
            self.reconnectWork[port] = nil
            self.createConnection(port: port, isReconnect: true)
            self.publish()
        }
        reconnectWork[port] = item
        queue.asyncAfter(deadline: .now() + delay, execute: item)
        publish()
    }

    private func prepareLatestForRecovery(port: Int) {
        guard let active = activeByPort.removeValue(forKey: port) else { return }
        watchdogWork.removeValue(forKey: port)?.cancel()
        if pendingByPort[port] == nil { pendingByPort[port] = active }
    }

    private func start(_ request: PendingSend, port: Int) {
        guard request.isCurrent?() != false else { return }
        guard isRunning, phases[port] == .ready else {
            pendingByPort[port] = request
            return
        }
        let configureGeneration = configurationGeneration
        let portGeneration = portGenerations[port, default: 0]
        activeByPort[port] = request
        let sendStartedAt = HostMonotonicClock.now()
#if DEBUG
        if freezeDiagnosticsEnabled {
            if let previous = lastSendStartByPort[port] {
                let gapMs = (sendStartedAt - previous) * 1000
                if gapMs > 100 {
                    let color = port == 5005 ? "RED" : port == 5006 ? "BLUE" : "PORT\(port)"
                    let queueWaitMs = max(0, sendStartedAt - request.enqueuedAt) * 1000
                    print(String(format: "[FREEZE_DIAG][UDP] sendGap=%.1fms color=%@ queueWait=%.1fms replaced=%d",
                                 gapMs, color, queueWaitMs, supersededPendingCount))
                }
            }
            lastSendStartByPort[port] = sendStartedAt
        }
#endif
        request.onSendStarted?(max(0, sendStartedAt - request.enqueuedAt), supersededPendingCount)
        scheduleSendWatchdog(requestID: request.id, port: port,
                             configurationGeneration: configureGeneration,
                             portGeneration: portGeneration)
        if let sendHandler {
            sendHandler(request.text, port) { [weak self] result in
                self?.queue.async {
                    self?.finish(request, port: port, configurationGeneration: configureGeneration,
                                 portGeneration: portGeneration, connection: nil, result: result)
                }
            }
            return
        }
        guard let connection = connections[port] else {
            scheduleReconnect(port: port, reason: "missing connection")
            return
        }
        guard let data = request.text.data(using: .ascii) else {
            finish(request, port: port, configurationGeneration: configureGeneration,
                   portGeneration: portGeneration, connection: connection,
                   result: .failure(SendError.invalidPayload(host: configuredHost, port: port)))
            return
        }
        connection.send(content: data, completion: .contentProcessed { [weak self, weak connection] error in
            guard let self else { return }
            // Network completion は start(queue:) に指定した同じキューで呼ばれる。
            self.finish(request, port: port, configurationGeneration: configureGeneration,
                        portGeneration: portGeneration, connection: connection,
                        result: error.map { .failure($0) }
                            ?? .success(ProcessInfo.processInfo.systemUptime))
        })
    }

    private func scheduleSendWatchdog(requestID: UInt64, port: Int,
                                      configurationGeneration: Int, portGeneration: Int) {
        watchdogWork.removeValue(forKey: port)?.cancel()
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.isRunning,
                  self.configurationGeneration == configurationGeneration,
                  self.portGenerations[port] == portGeneration,
                  self.activeByPort[port]?.id == requestID else { return }
            self.watchdogTimeoutCounts[port, default: 0] += 1
#if DEBUG
            print("[UDP][\(port)] send watchdog timeout count=\(self.watchdogTimeoutCounts[port, default: 0]) generation=\(portGeneration)")
#endif
            self.scheduleReconnect(port: port, reason: "send watchdog timeout")
        }
        watchdogWork[port] = item
        queue.asyncAfter(deadline: .now() + timing.sendWatchdogTimeout, execute: item)
    }

    private func finish(_ request: PendingSend, port: Int, configurationGeneration: Int,
                        portGeneration: Int, connection: NWConnection?,
                        result: Result<TimeInterval, Error>) {
        dispatchPrecondition(condition: .onQueue(queue))
        guard isRunning, self.configurationGeneration == configurationGeneration,
              portGenerations[port] == portGeneration,
              activeByPort[port]?.id == request.id,
              connection == nil || connections[port] === connection else {
            rejectedCompletionCount += 1
            return
        }
        watchdogWork.removeValue(forKey: port)?.cancel()
        activeByPort[port] = nil
        switch result {
        case .success(let completedAt):
            let clearedReportedError = sendErrors.removeValue(forKey: port) != nil
            request.completion(.success(completedAt))
            if clearedReportedError { publish() }
        case .failure(let error):
            sendErrors[port] = "UDP送信失敗 (\(configuredHost):\(port)): \(error.localizedDescription)"
            request.completion(.failure(error))
            publish()
        }
        if let latest = pendingByPort.removeValue(forKey: port) { start(latest, port: port) }
    }

    func recoverIfNeeded() {
        queue.async { [weak self] in
            guard let self, self.isRunning else { return }
            for port in self.configuredPorts {
                switch self.phases[port] {
                case .ready:
                    continue
                case .preparing:
                    let elapsed = HostMonotonicClock.now() - (self.connectionCreatedAt[port] ?? 0)
                    if elapsed >= self.timing.waitingTimeout {
                        self.scheduleReconnect(port: port, reason: "foreground preparing timeout")
                    }
                case .waiting:
                    let elapsed = HostMonotonicClock.now() - (self.waitingSince[port] ?? 0)
                    if elapsed >= self.timing.waitingTimeout {
                        self.scheduleReconnect(port: port, reason: "foreground waiting timeout")
                    }
                default:
                    self.scheduleReconnect(port: port, reason: "foreground validation")
                }
            }
        }
    }

    /// Drops coordinates still waiting for a LAN port (and in-flight ones on
    /// ports that are not ready, which a reconnect would resend), so a LAN port
    /// that becomes ready later never delivers them after newer coordinates went
    /// over the P2P link. Called only by the P2P route; unused while P2P is off.
    func discardPendingCoordinates() {
        queue.async { [weak self] in
            guard let self, self.isRunning else { return }
            for port in self.configuredPorts {
                if self.pendingByPort.removeValue(forKey: port) != nil { self.discardedForP2PCount += 1 }
                if self.phases[port] != .ready, self.activeByPort.removeValue(forKey: port) != nil {
                    self.watchdogWork.removeValue(forKey: port)?.cancel()
                    self.discardedForP2PCount += 1
                }
            }
        }
    }

    func snapshot(completion: @escaping (Snapshot) -> Void) {
        queue.async { completion(Snapshot(states: self.states, errors: self.mergedErrors(),
                                          lastError: self.currentError())) }
    }

#if DEBUG
    func setFreezeDiagnosticsEnabled(_ enabled: Bool) {
        queue.async { [weak self] in
            self?.freezeDiagnosticsEnabled = enabled
            self?.lastSendStartByPort = [:]
        }
    }
#endif

    func stop() {
        queue.sync {
            configurationGeneration += 1
            stopLocked(clearHandler: true)
            publish()
        }
    }

    private func stopLocked(clearHandler: Bool) {
        isRunning = false
        connections.values.forEach { $0.cancel() }
        connections.removeAll()
        for item in reconnectWork.values { item.cancel() }
        for item in waitingWork.values { item.cancel() }
        for item in watchdogWork.values { item.cancel() }
        reconnectWork.removeAll(); waitingWork.removeAll(); watchdogWork.removeAll()
        activeByPort.removeAll(); pendingByPort.removeAll()
        configuredPorts.removeAll(); phases.removeAll(); waitingSince.removeAll(); connectionCreatedAt.removeAll()
        states.removeAll(); connectionErrors.removeAll(); sendErrors.removeAll()
        reconnectAttempts.removeAll()
        configuredHost = ""
        if clearHandler { updateHandler = nil }
#if DEBUG
        lastSendStartByPort = [:]
#endif
    }

    private func cancelPortWork(port: Int) {
        reconnectWork.removeValue(forKey: port)?.cancel()
        waitingWork.removeValue(forKey: port)?.cancel()
        watchdogWork.removeValue(forKey: port)?.cancel()
    }

    func setStateForTesting(port: Int, state: String, error: String?) {
        queue.sync { states[port] = state; connectionErrors[port] = error; publish() }
    }

    func simulateConnectionStateForTesting(port: Int, state: ConnectionStateForTesting,
                                           reason: String = "test") {
        queue.async { [weak self] in
            guard let self else { return }
            let generation = self.portGenerations[port, default: 0]
            let phase: PortPhase
            switch state { case .ready: phase = .ready; case .waiting: phase = .waiting; case .failed: phase = .failed }
            self.handleState(phase, port: port, configurationGeneration: self.configurationGeneration,
                             portGeneration: generation, reason: reason)
        }
    }

    func sendStaleUpdateForTesting(index: Int, states: [Int: String], errors: [Int: String]) {
        queue.sync {
            guard updateHandlersForTesting.indices.contains(index) else { return }
            updateHandlersForTesting[index]?(states, errors, errors.values.first)
        }
    }

    var rejectedCompletionCountForTesting: Int { queue.sync { rejectedCompletionCount } }
    var supersededPendingCountForTesting: Int { queue.sync { supersededPendingCount } }
    var discardedForP2PCountForTesting: Int { queue.sync { discardedForP2PCount } }
    var pendingCountForTesting: Int { queue.sync { pendingByPort.count } }
    func connectionGenerationForTesting(port: Int) -> Int { queue.sync { portGenerations[port, default: 0] } }
    func reconnectCountForTesting(port: Int) -> Int { queue.sync { reconnectCounts[port, default: 0] } }
    func watchdogTimeoutCountForTesting(port: Int) -> Int { queue.sync { watchdogTimeoutCounts[port, default: 0] } }

    private func mergedErrors() -> [Int: String] {
        var result = sendErrors
        connectionErrors.forEach { result[$0] = $1 }
        return result
    }

    private func currentError() -> String? {
        connectionErrors.values.compactMap { $0 }.first ?? sendErrors.values.compactMap { $0 }.first
    }

    private func publish() { updateHandler?(states, mergedErrors(), currentError()) }
}

/// LAN 経路の生存確認。Unity の探索応答（UDP 5007、Android の自動探索と同じ要求）へ 0.5 秒ごとに
/// unicast で問い合わせ、1.5 秒以内に応答があれば LAN が Unity まで届いているとみなす。
/// UDP の座標送信は届いたか分からないため、P2P より LAN を優先してよいかの判断にだけ使う。
/// 2026-10-07 の F9 実測では LAN が P2P より中央値で約45ms、p95で約2倍速かった。
final class LANLivenessProbe {
    static let port: UInt16 = 5007
    static let request = "PHONESABER_DISCOVER 1"
    static let replyPrefix = "PHONESABER_UNITY 1"
    static let aliveWindow: TimeInterval = 1.5

    private let queue = DispatchQueue(label: "PhoneSaberSender.lanProbe")
    private let lock = NSLock()
    private let clock: () -> TimeInterval
    private var host = ""
    private var connection: NWConnection?
    private var timer: DispatchSourceTimer?
    private var lastReply = -Double.infinity

    init(clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.clock = clock
    }

    /// 同じ host なら何もしない（応答履歴を保つ）。
    func start(host: String) {
        lock.lock()
        let same = host == self.host && timer != nil
        lock.unlock()
        if same { return }
        stop()
        guard !host.isEmpty else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: 0.5)
        timer.setEventHandler { [weak self] in self?.tick() }
        lock.lock(); self.host = host; self.timer = timer; lock.unlock()
        timer.resume()
    }

    func stop() {
        lock.lock()
        let timer = self.timer, connection = self.connection
        self.timer = nil; self.connection = nil; host = ""; lastReply = -Double.infinity
        lock.unlock()
        timer?.cancel()
        connection?.cancel()
    }

    var isAlive: Bool {
        lock.lock(); defer { lock.unlock() }
        return clock() - lastReply <= Self.aliveWindow
    }

    func recordReplyForTesting(_ text: String) { handle(Data(text.utf8)) }

    private func tick() {
        lock.lock()
        let host = self.host
        var connection = self.connection
        lock.unlock()
        guard !host.isEmpty, let port = NWEndpoint.Port(rawValue: Self.port) else { return }
        if let current = connection, case .failed = current.state { current.cancel(); connection = nil }
        if let current = connection, case .cancelled = current.state { connection = nil }
        if connection == nil {
            let created = NWConnection(host: NWEndpoint.Host(host), port: port, using: .udp)
            created.start(queue: queue)
            receive(on: created)
            lock.lock(); self.connection = created; lock.unlock()
            connection = created
        }
        connection?.send(content: Data(Self.request.utf8), completion: .idempotent)
    }

    private func receive(on connection: NWConnection) {
        connection.receiveMessage { [weak self, weak connection] data, _, _, error in
            guard let self, let connection else { return }
            if let data { self.handle(data) }
            if error == nil { self.receive(on: connection) }
        }
    }

    private func handle(_ data: Data) {
        guard let text = String(data: data, encoding: .ascii), text.hasPrefix(Self.replyPrefix) else { return }
        lock.lock(); lastReply = clock(); lock.unlock()
    }
}

/// 検出キューから直接送信するための設定スナップショットと経路選択。
/// UI は設定変更時だけ書き込み、フレームは MainActor を待たない。
final class CoordinateDelivery: @unchecked Sendable {
    struct Settings {
        var running = false
        var processorGeneration = 0
        var lifecycleGeneration = 0
        var outputWidth = 1920
        var outputHeight = 1080
        var mirrorX = false
        var mirrorY = false
        var measurementMode = false
        var manual = false
        var p2pEnabled = false
        var lanConfigured = false
    }

    private let lock = NSLock()
    private var settings = Settings()
    private var routedViaP2P = false
    private let sender: UDPSender
    private let p2pSender: P2PSender
    private let lanProbe: LANLivenessProbe

    init(sender: UDPSender, p2pSender: P2PSender, lanProbe: LANLivenessProbe) {
        self.sender = sender
        self.p2pSender = p2pSender
        self.lanProbe = lanProbe
    }

    func update(_ settings: Settings) {
        lock.lock(); defer { lock.unlock() }
        self.settings = settings
        if !settings.running { routedViaP2P = false }
    }

    func suspend() {
        lock.lock(); settings.running = false; routedViaP2P = false; lock.unlock()
    }

    func snapshot(generation: Int) -> Settings? {
        lock.lock(); defer { lock.unlock() }
        guard settings.running, settings.processorGeneration == generation else { return nil }
        return settings
    }

    private func isCurrent(_ request: Settings) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return matches(request)
    }

    private func matches(_ request: Settings) -> Bool {
        settings.running && settings.processorGeneration == request.processorGeneration
            && settings.lifecycleGeneration == request.lifecycleGeneration
    }

    func route(_ text: String, port: Int, settings request: Settings,
               onSendStarted: ((TimeInterval, Int) -> Void)?,
               completion: @escaping (Result<TimeInterval, Error>) -> Void,
               noRoute: @escaping () -> Void) {
        // 設定変更・停止と enqueue の順序を固定する。送信自体は非同期なので待たない。
        lock.lock(); defer { lock.unlock() }
        guard matches(request) else { return }
        let valid: () -> Bool = { [weak self] in self?.isCurrent(request) == true }
        let lanVerified = settings.lanConfigured && lanProbe.isAlive
        if !settings.manual && settings.p2pEnabled && p2pSender.isUsable && !lanVerified {
            if !routedViaP2P {
                routedViaP2P = true
                sender.discardPendingCoordinates()
            }
            p2pSender.send(text, to: port, onSendStarted: onSendStarted, completion: completion,
                           fallback: { [weak self] in
                               guard let self else { return }
                               self.lock.lock(); defer { self.lock.unlock() }
                               guard self.matches(request) else { return }
                               if self.settings.lanConfigured {
                                   self.sender.send(text, to: port, onSendStarted: onSendStarted,
                                                    isCurrent: valid, completion: completion)
                               } else { noRoute() }
                           }, isCurrent: valid)
        } else if settings.lanConfigured {
            routedViaP2P = false
            sender.send(text, to: port, onSendStarted: onSendStarted, isCurrent: valid, completion: completion)
        } else {
            routedViaP2P = false
            noRoute()
        }
    }
}
