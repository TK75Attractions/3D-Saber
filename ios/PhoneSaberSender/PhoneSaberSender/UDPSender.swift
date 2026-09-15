import Foundation
import Network

final class UDPSender {
    typealias SendHandler = (String, Int, @escaping (Result<TimeInterval, Error>) -> Void) -> Void
    private var connections: [Int: NWConnection] = [:]
    private let queue = DispatchQueue(label: "PhoneSaberSender.udp", qos: .userInteractive)
    private var states: [Int: String] = [:]
    private var connectionErrors: [Int: String] = [:]
    private var sendErrors: [Int: String] = [:]
    private var generation = 0
    private var configuredHost = ""
    private var updateHandler: (([Int: String], [Int: String], String?) -> Void)?
    private var updateHandlersForTesting: [(( [Int: String], [Int: String], String?) -> Void)?] = []
    private var rejectedCompletionCount = 0
    private struct PendingSend {
        let text: String
        let enqueuedAt: TimeInterval
        let onSendStarted: ((TimeInterval, Int) -> Void)?
        let completion: (Result<TimeInterval, Error>) -> Void
    }
    private var activePorts: Set<Int> = []
    private var pendingByPort: [Int: PendingSend] = [:]
    private var supersededPendingCount = 0
    private let sendHandler: SendHandler?

    init(sendHandler: SendHandler? = nil) {
        self.sendHandler = sendHandler
    }

    struct Snapshot { let states: [Int: String]; let errors: [Int: String]; let lastError: String? }

    enum SendError: LocalizedError {
        case notConfigured(host: String, port: Int)
        case invalidPayload(host: String, port: Int)

        var errorDescription: String? {
            switch self {
            case .notConfigured(let host, let port): return "UDP送信先未設定 (\(host.isEmpty ? "未設定" : host):\(port))"
            case .invalidPayload(let host, let port): return "UDP送信失敗 (\(host):\(port)): 座標をASCIIへ変換できません"
            }
        }
    }

    func configure(host: String, ports: [Int] = [5005, 5006], onUpdate: (([Int: String], [Int: String], String?) -> Void)? = nil) {
        queue.sync {
            generation += 1
            let currentGeneration = generation
            configuredHost = host
            updateHandler = onUpdate
            updateHandlersForTesting.append(onUpdate)
            states = Dictionary(uniqueKeysWithValues: ports.map { ($0, "waiting") })
            connectionErrors = [:]
            sendErrors = [:]
            activePorts = []
            pendingByPort = [:]
            supersededPendingCount = 0
            connections.values.forEach { $0.cancel() }
            if sendHandler != nil {
                connections = [:]
                states = Dictionary(uniqueKeysWithValues: ports.map { ($0, "ready (テスト送信)") })
                publish()
                return
            }
            connections = Dictionary(uniqueKeysWithValues: ports.map { port in
            let connection = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: UInt16(port))!, using: .udp)
            connection.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                self.queue.async {
                    guard self.generation == currentGeneration else { return }
                    switch state {
                    case .ready:
                        self.states[port] = "ready (送信可能)"
                        self.connectionErrors[port] = nil
                    case .waiting(let error): self.states[port] = "waiting"; self.connectionErrors[port] = "UDP接続待機 (\(host):\(port)): \(error.localizedDescription)"
                    case .failed(let error): self.states[port] = "failed"; self.connectionErrors[port] = "UDP接続失敗 (\(host):\(port)): \(error.localizedDescription)"
                    case .cancelled: self.states[port] = "停止"
                    default: self.states[port] = String(describing: state)
                    }
                    self.publish()
                }
            }
            connection.start(queue: queue)
            return (port, connection)
            })
            publish()
        }
    }

    func send(_ text: String, to port: Int, onSendStarted: ((TimeInterval, Int) -> Void)? = nil,
              completion: @escaping (Result<TimeInterval, Error>) -> Void) {
        queue.async { [weak self] in
            guard let self else { return }
            let request = PendingSend(text: text, enqueuedAt: HostMonotonicClock.now(),
                                      onSendStarted: onSendStarted, completion: completion)
            if self.activePorts.contains(port) {
                if self.pendingByPort.updateValue(request, forKey: port) != nil {
                    self.supersededPendingCount += 1
                }
            } else {
                self.activePorts.insert(port)
                self.start(request, port: port)
            }
        }
    }

    /// At most one datagram is in flight and one latest datagram is waiting per
    /// port. Repeated frames replace the waiting value instead of building FIFO lag.
    private func start(_ request: PendingSend, port: Int) {
        let sendGeneration = generation
        // This is the start of Network.framework work, not proof that the
        // datagram has reached the network interface or peer.
        request.onSendStarted?(max(0, HostMonotonicClock.now() - request.enqueuedAt), supersededPendingCount)
        if let sendHandler {
            sendHandler(request.text, port) { [weak self] result in
                self?.queue.async {
                    self?.finish(request, port: port, generation: sendGeneration,
                                 connection: nil, result: result)
                }
            }
            return
        }
        guard let connection = connections[port] else {
            finish(request, port: port, generation: sendGeneration, connection: nil,
                   result: .failure(SendError.notConfigured(host: configuredHost, port: port)))
            return
        }
        let host = configuredHost
        guard let data = request.text.data(using: .ascii) else {
            finish(request, port: port, generation: sendGeneration, connection: connection,
                   result: .failure(SendError.invalidPayload(host: host, port: port)))
            return
        }
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.queue.async {
                self.finish(request, port: port, generation: sendGeneration,
                            connection: connection,
                            result: error.map { .failure($0) }
                                ?? .success(ProcessInfo.processInfo.systemUptime))
            }
        })
    }

    private func finish(_ request: PendingSend, port: Int, generation sendGeneration: Int,
                        connection: NWConnection?, result: Result<TimeInterval, Error>) {
        guard generation == sendGeneration,
              connection == nil || connections[port] === connection else {
            rejectedCompletionCount += 1
            return
        }
        switch result {
        case .success(let completedAt):
            let clearedReportedError = sendErrors.removeValue(forKey: port) != nil
            request.completion(.success(completedAt))
            // Connection state did not change. Avoid a second MainActor UI
            // update for every successful frame; publish only when an error
            // was actually cleared.
            if clearedReportedError { publish() }
        case .failure(let error):
            sendErrors[port] = "UDP送信失敗 (\(configuredHost):\(port)): \(error.localizedDescription)"
            request.completion(.failure(error))
            publish()
        }
        if let latest = pendingByPort.removeValue(forKey: port) {
            start(latest, port: port)
        } else {
            activePorts.remove(port)
        }
    }

    func snapshot(completion: @escaping (Snapshot) -> Void) { queue.async { completion(Snapshot(states: self.states, errors: self.mergedErrors(), lastError: self.currentError())) } }

    func stop() {
        queue.sync { generation += 1; connections.values.forEach { $0.cancel() }; connections.removeAll(); activePorts = []; pendingByPort = [:]; supersededPendingCount = 0; states = [:]; connectionErrors = [:]; sendErrors = [:]; configuredHost = ""; publish() }
    }

    func setStateForTesting(port: Int, state: String, error: String?) {
        queue.sync {
            states[port] = state
            connectionErrors[port] = error
            publish()
        }
    }

    func sendStaleUpdateForTesting(index: Int, states: [Int: String], errors: [Int: String]) {
        queue.sync {
            guard updateHandlersForTesting.indices.contains(index) else { return }
            updateHandlersForTesting[index]?(states, errors, errors.values.first)
        }
    }

    var rejectedCompletionCountForTesting: Int {
        queue.sync { rejectedCompletionCount }
    }

    var supersededPendingCountForTesting: Int {
        queue.sync { supersededPendingCount }
    }

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
