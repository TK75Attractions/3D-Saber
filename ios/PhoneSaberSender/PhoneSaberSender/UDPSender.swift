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

    func send(_ text: String, to port: Int, completion: @escaping (Result<TimeInterval, Error>) -> Void) {
        queue.async { [weak self] in
            guard let self else { return }
            if let sendHandler = self.sendHandler {
                let sendGeneration = self.generation
                sendHandler(text, port) { result in
                    self.queue.async {
                        guard self.generation == sendGeneration else { return }
                        switch result {
                        case .success(let completedAt): self.sendErrors[port] = nil; completion(.success(completedAt))
                        case .failure(let error): self.sendErrors[port] = "UDP送信失敗 (\(self.configuredHost):\(port)): \(error.localizedDescription)"; completion(.failure(error))
                        }
                        self.publish()
                    }
                }
                return
            }
            guard let connection = self.connections[port] else { completion(.failure(SendError.notConfigured(host: self.configuredHost, port: port))); return }
            let sendGeneration = self.generation
            let sendConnection = connection
            let host = self.configuredHost
            guard let data = text.data(using: .ascii) else { completion(.failure(SendError.invalidPayload(host: host, port: port))); return }
            connection.send(content: data, completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                self.queue.async {
                    guard self.generation == sendGeneration, self.connections[port] === sendConnection else { return }
                    if let error {
                        self.sendErrors[port] = "UDP送信失敗 (\(host):\(port)): \(error.localizedDescription)"
                        self.publish()
                        completion(.failure(error))
                    } else {
                        self.sendErrors[port] = nil
                        self.publish()
                        completion(.success(ProcessInfo.processInfo.systemUptime))
                    }
                }
            })
        }
    }

    func snapshot(completion: @escaping (Snapshot) -> Void) { queue.async { completion(Snapshot(states: self.states, errors: self.mergedErrors(), lastError: self.currentError())) } }

    func stop() {
        queue.sync { generation += 1; connections.values.forEach { $0.cancel() }; connections.removeAll(); states = [:]; connectionErrors = [:]; sendErrors = [:]; configuredHost = ""; publish() }
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
