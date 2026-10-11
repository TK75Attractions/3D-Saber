import Foundation
import Network

#if DEBUG
struct ConfigurationCallbackHistory<Value> {
    static var capacity: Int { 16 }
    private(set) var values: [Value] = []
    private(set) var firstIndex = 0

    mutating func append(_ value: Value) {
        values.append(value)
        if values.count > Self.capacity {
            values.removeFirst()
            firstIndex += 1
        }
    }

    subscript(index: Int) -> Value? {
        let offset = index - firstIndex
        return values.indices.contains(offset) ? values[offset] : nil
    }
}
#endif

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
#if DEBUG
    private var updateHandlersForTesting = ConfigurationCallbackHistory<(([Int: String], [Int: String], String?) -> Void)?>()
#endif
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
#if DEBUG
            // Debug の長時間運用でも直近16世代だけ保持する。古い絶対 index は無視する。
            updateHandlersForTesting.append(onUpdate)
#endif
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
                // 解放済みの旧接続（nil）と、再接続待ちで空の connections[port]（nil）を
                // 一致とみなさない。旧接続の cancelled/failed で表示や phase を上書きしない。
                guard let connection, self.connections[port] === connection else { return }
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
            if activeByPort[port] == nil, let latest = pendingByPort.removeValue(forKey: port) {
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
#if DEBUG
        // 本番では Network の契約（start(queue:) のキューで完了通知）に任せ、当日に落とさない。
        dispatchPrecondition(condition: .onQueue(queue))
#endif
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
#if DEBUG
        queue.sync {
            updateHandlersForTesting[index]??(states, errors, errors.values.first)
        }
#endif
    }

#if DEBUG
    var updateHandlerHistoryCountForTesting: Int { queue.sync { updateHandlersForTesting.values.count } }
#endif

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
    /// テスト中の view model が使う宛先 port（何も応答しない）。応答は recordReplyForTesting で与える。
    static let unusedPortForTests: UInt16 = 1
    static let request = "PHONESABER_DISCOVER 1"
    static let replyPrefix = "PHONESABER_UNITY 1"
    static let aliveWindow: TimeInterval = 1.5

    private let queue = DispatchQueue(label: "PhoneSaberSender.lanProbe")
    private let lock = NSLock()
    private let clock: () -> TimeInterval
    /// 本番は常に 5007。テストだけが空きポートの偽 Unity を指定する。
    private let probePort: UInt16
    private var host = ""
    private var connection: NWConnection?
    private var timer: DispatchSourceTimer?
    private var lastReply = -Double.infinity
    private var createdConnectionCount = 0

    init(port: UInt16 = LANLivenessProbe.port,
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.probePort = port
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

    func recordReplyForTesting(_ text: String) { handle(Data(text.utf8), from: nil) }

    var hostForTesting: String {
        lock.lock(); defer { lock.unlock() }
        return host
    }

    var createdConnectionCountForTesting: Int {
        lock.lock(); defer { lock.unlock() }
        return createdConnectionCount
    }

    /// 受信待ちが error で終わった場合と同じ処理（次の tick で接続を作り直す）。
    func simulateReceiveErrorForTesting() {
        queue.sync {
            lock.lock(); let current = connection; lock.unlock()
            if let current { discard(current) }
        }
    }

    private func tick() {
        lock.lock()
        let host = self.host
        var connection = self.connection
        lock.unlock()
        guard !host.isEmpty, let port = NWEndpoint.Port(rawValue: probePort) else { return }
        if let current = connection, case .failed = current.state { current.cancel(); connection = nil }
        if let current = connection, case .cancelled = current.state { connection = nil }
        if connection == nil {
            let created = NWConnection(host: NWEndpoint.Host(host), port: port, using: .udp)
            // この tick の途中で stop / 別 host への start があったら、旧 host の接続を残さない。
            lock.lock()
            let stillCurrent = self.host == host
            if stillCurrent { self.connection = created; createdConnectionCount += 1 }
            lock.unlock()
            guard stillCurrent else { return }
            created.start(queue: queue)
            receive(on: created)
            connection = created
        }
        connection?.send(content: Data(Self.request.utf8), completion: .idempotent)
    }

    private func receive(on connection: NWConnection) {
        connection.receiveMessage { [weak self, weak connection] data, _, _, error in
            guard let self, let connection else { return }
            if let data { self.handle(data, from: connection) }
            if error == nil {
                self.receive(on: connection)
            } else {
                // state が ready のままでも、受信待ちが無い接続では Unity の応答を二度と受け取れない
                // （LAN が確認済みにならず P2P へ回り続ける）。次の tick で作り直す。
                self.discard(connection)
            }
        }
    }

    private func discard(_ connection: NWConnection) {
        lock.lock()
        let current = self.connection === connection
        if current { self.connection = nil }
        lock.unlock()
        if current { connection.cancel() }
    }

    /// `connection` が nil のときはテストからの記録。停止・host 変更後に届いた旧接続の応答は数えない。
    /// この iPhone が送る先（5005 / 5006、版 1）で受けている Unity の応答だけを「LAN が生きている」証拠にする。
    /// 接頭辞だけの比較では "PHONESABER_UNITY 10 …" や別の port で待つ Unity も生存扱いになり、
    /// 届かない LAN を優先し続けて P2P へ退避できなかった。
    static func isCompatibleReply(_ text: String) -> Bool {
        guard text.hasPrefix(replyPrefix) else { return false }
        let rest = text.dropFirst(replyPrefix.count)
        guard rest.isEmpty || rest.first == " " else { return false }
        for field in rest.split(separator: " ") {
            if field.hasPrefix("red="), field.dropFirst(4) != "5005" { return false }
            if field.hasPrefix("blue="), field.dropFirst(5) != "5006" { return false }
        }
        return true
    }

    private func handle(_ data: Data, from connection: NWConnection?) {
        guard let text = String(data: data, encoding: .ascii), Self.isCompatibleReply(text) else { return }
        lock.lock()
        if connection == nil || self.connection === connection { lastReply = clock() }
        lock.unlock()
    }
}

/// 通信経路の出来事（経路の切替、P2P の探索・再接続、Unity 応答の途切れ）を時刻付きで残す。
/// 2026-10-10 に約100秒 PC へ届かなかったとき、iPhone 側の記録が何も無く原因を特定できなかった。
/// 直近を画面に出し、同じ内容を Library/Logs/connectivity.log へ追記する（256 KiB で1世代だけ回す）。
/// 記録は出来事の発生時だけ（フレームごとには呼ばない）。
final class ConnectivityLog: @unchecked Sendable {
    static let shared = ConnectivityLog(fileURL: defaultFileURL)
    static let capacity = 200
    static let maximumFileBytes = 256 * 1024

    struct Entry: Equatable {
        let time: Date
        let source: String
        let message: String
    }

    /// XCTest 中と、Mac 上で動かす解析用 harness ではファイルを作らない（iPhone のアプリ内だけ）。
    static var defaultFileURL: URL? {
#if os(iOS)
        guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil,
              let library = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first else { return nil }
        return library.appendingPathComponent("Logs/connectivity.log")
#else
        return nil
#endif
    }

    private let lock = NSLock()
    private var entries: [Entry] = []
    private var revisionValue = 0
    private let fileURL: URL?
    private let ioQueue = DispatchQueue(label: "PhoneSaberSender.connectivityLog", qos: .utility)
    private let fileFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    init(fileURL: URL? = nil) { self.fileURL = fileURL }

    /// 変化の検出用（画面はこれが変わったときだけ読み直す）。
    var revision: Int {
        lock.lock(); defer { lock.unlock() }
        return revisionValue
    }

    func record(_ source: String, _ message: String, at time: Date = Date()) {
        let entry = Entry(time: time, source: source, message: message)
        lock.lock()
        entries.append(entry)
        if entries.count > Self.capacity { entries.removeFirst(entries.count - Self.capacity) }
        revisionValue += 1
        lock.unlock()
        guard let fileURL else { return }
        ioQueue.async { [fileFormatter] in
            Self.append("\(fileFormatter.string(from: time)) [\(source)] \(message)\n", to: fileURL)
        }
    }

    var recent: [Entry] {
        lock.lock(); defer { lock.unlock() }
        return entries
    }

    /// 画面用の直近 `count` 行（端末の現地時刻 HH:mm:ss）。
    func displayLines(last count: Int) -> [String] {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "HH:mm:ss"
        return recent.suffix(count).map { "\(formatter.string(from: $0.time)) [\($0.source)] \($0.message)" }
    }

    /// ファイル書き込みの完了待ち（テスト用）。
    func flushForTesting() { ioQueue.sync {} }

    private static func append(_ line: String, to url: URL) {
        let manager = FileManager.default
        try? manager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let size = (try? manager.attributesOfItem(atPath: url.path))?[.size] as? Int, size >= maximumFileBytes {
            let previous = url.appendingPathExtension("1")
            // 回転先がディレクトリでも、その中の別の記録を再帰削除しない。現行へ追記を続ける。
            var isDirectory: ObjCBool = false
            if !manager.fileExists(atPath: previous.path, isDirectory: &isDirectory) || !isDirectory.boolValue {
                try? manager.removeItem(at: previous)
                try? manager.moveItem(at: url, to: previous)
            }
        }
        guard let data = line.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }
}

/// 送信中なのに PC へ届いている証拠が無い状態（Unity の探索応答なし、かつ P2P 未接続）が続いたら知らせる。
/// UDP は届かなくても「ready」のままなので、見出しは NETWORK READY のままになる（2026-10-10 の実機）。
/// `sending` は送信要求が続いているか（LAN 未設定で P2P だけの間も含む）。手動 IP は P2P を使わないので、
/// 呼び出し側は手動 IP のとき `p2pConnected` を false で渡す。
struct PCReplyWatch: Equatable {
    static let warnAfter: TimeInterval = 3
    static let automaticMessage = "PC（Unity）から応答がありません。iPhone と PC が同じ Wi-Fi か確認してください。"
    /// Unity 以外の受信先（計測ツールなど）は 5007 に応答しないので、届いていても確認できない。
    static let manualMessage = "手動 IP の宛先から Unity の応答がありません（Unity 以外の受信先なら正常です）。"
    private(set) var missingSince: TimeInterval?

    static func message(manual: Bool) -> String { manual ? manualMessage : automaticMessage }

    mutating func update(sending: Bool, lanVerified: Bool, p2pConnected: Bool, now: TimeInterval) -> Bool {
        guard sending, !lanVerified, !p2pConnected else {
            missingSince = nil
            return false
        }
        let since = missingSince ?? now
        missingSince = since
        return now - since >= Self.warnAfter
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

    /// 座標が実際に選んだ経路。変化したときだけ ConnectivityLog に残す。
    enum Route: String {
        case none = "経路なし（送信していません）"
        case p2p = "P2P"
        case lanVerified = "LAN（Unity 応答あり）"
        case lanUnverified = "LAN（Unity 応答なし。届いているか不明）"
    }

    private let lock = NSLock()
    private var settings = Settings()
    private var routedViaP2P = false
    private var lastRoute: Route?
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
        if !settings.running { routedViaP2P = false; lastRoute = nil }
    }

    func suspend() {
        lock.lock(); settings.running = false; routedViaP2P = false; lastRoute = nil; lock.unlock()
    }

    /// lock を持った状態で呼ぶ。
    private func note(_ route: Route) {
        guard route != lastRoute else { return }
        lastRoute = route
        ConnectivityLog.shared.record("経路", route.rawValue)
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
            note(.p2p)
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
            note(lanVerified ? .lanVerified : .lanUnverified)
            sender.send(text, to: port, onSendStarted: onSendStarted, isCurrent: valid, completion: completion)
        } else {
            routedViaP2P = false
            note(.none)
            noRoute()
        }
    }
}
