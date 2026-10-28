import Combine
import CryptoKit
import Foundation
import Network

private struct DebugBundleManifestFile: Encodable {
    let path: String
    let size: Int64
    let sha256: String
    let contentType: String
}

private struct DebugBundleManifest: Encodable {
    let formatVersion = 1
    let sessionID: String
    let files: [DebugBundleManifestFile]
}

private enum DebugBundleTransferError: LocalizedError {
    case invalidSummary
    case unsafePath(String)
    case invalidFiles
    case bundleTooLarge
    case discoveryTimeout
    case invalidEndpoint
    case httpStatus(Int)
    case invalidReceipt

    var errorDescription: String? {
        switch self {
        case .invalidSummary: return "summary.json is invalid"
        case .unsafePath(let path): return "unsafe triage bundle path: \(path)"
        case .invalidFiles: return "triage bundle file list is invalid"
        case .bundleTooLarge: return "triage upload exceeds the 64 MiB limit"
        case .discoveryTimeout: return "PhoneSaber diagnostics receiver was not found"
        case .invalidEndpoint: return "receiver hostname or port is invalid"
        case .invalidReceipt: return "receiver acknowledgment does not match this session"
        case .httpStatus(let status): return "receiver returned HTTP \(status)"
        }
    }
}

// Persist relative paths: iOS may move the Documents container after relaunch.
struct DebugBundleOutbox: Codable {
    static let maximumAttempts = 6
    enum State: String, Codable { case pending, uploading, received, failed }
    struct Entry: Codable, Equatable {
        let sessionID: String
        let bundlePath: String
        let createdAt: Date
        var state: State = .pending
        var attempts = 0
        // 診断用の累計HTTP試行数。手動再送でも戻さず、旧outboxはattemptsから引き継ぐ。
        var uploadAttempts: Int? = nil
        var reason: String?
        var nextAttemptAt = Date.distantPast
        var receivedAt: Date?
        var automatic = true
        /// Auto clips (2026-10-19): not started while coordinates are being sent, so
        /// an upload never competes with gameplay traffic. nil in older outboxes.
        var deferDuringPlay: Bool? = nil

        // 旧outboxでも手動優先度を読み込めるようoptionalで追加する。
        var manualClip: Bool? = nil
        var isPending: Bool { state != .received }
        var canRetry: Bool { isPending && attempts < DebugBundleOutbox.maximumAttempts }
        func bundleURL(in directory: URL) -> URL {
            bundlePath.hasPrefix("/") ? URL(fileURLWithPath: bundlePath) : directory.appendingPathComponent(bundlePath)
        }
        var statusText: String {
            switch state {
            case .pending: return "転送待ち"
            case .uploading: return "転送中"
            case .received:
                let time = receivedAt.map {
                    DateFormatter.localizedString(from: $0, dateStyle: .short, timeStyle: .medium)
                } ?? ""
                return "Mac 受信済み（\(time)）"
            case .failed:
                return "転送失敗（\(reason ?? "不明")）— "
                    + (canRetry ? "自動で再試行します" : "再試行上限・端末に保存済み（手動再送可）")
            }
        }
    }

    private(set) var entries: [Entry] = []
    private(set) var prunedBefore = Date.distantPast
    var pendingCount: Int { entries.filter(\.isPending).count }
    var latest: Entry? { entries.max { $0.createdAt < $1.createdAt } }

    mutating func register(sessionID: String, bundlePath: String, now: Date, automatic: Bool = true,
                           deferDuringPlay: Bool = false, manualClip: Bool = false) {
        if let index = entries.firstIndex(where: { $0.sessionID == sessionID }) {
            // 再起動時は保存済みsummaryで旧エントリーの優先度・プレイ中抑止も復元する。
            if deferDuringPlay { entries[index].deferDuringPlay = true }
            if manualClip { entries[index].manualClip = true }
            return
        }
        guard now > prunedBefore else { return }
        entries.append(Entry(sessionID: sessionID, bundlePath: bundlePath, createdAt: now, automatic: automatic,
                             deferDuringPlay: deferDuringPlay ? true : nil, manualClip: manualClip ? true : nil))
    }

    mutating func recover(exists: (String) -> Bool) -> [String] {
        let missing = entries.filter { !exists($0.bundlePath) }.map(\.bundlePath)
        entries.removeAll { missing.contains($0.bundlePath) }
        for index in entries.indices where entries[index].state == .uploading {
            entries[index].state = .pending
        }
        prune()
        return missing
    }

    // 手動を先頭にし、各種別では保存順を守る。古い自動clipのbackoff中も追い越さない。
    private func candidates(automatic: Bool, manualIDs: Set<String>, liveTraffic: Bool) -> [Int] {
        entries.indices.filter {
            // 「今すぐ送る」で選んだclipは、座標送信中でも待たせない（10-29: 剣を持ったままだと送られず端末で消えた）。
            entries[$0].canRetry
                && !(liveTraffic && entries[$0].deferDuringPlay == true && !manualIDs.contains(entries[$0].sessionID))
                && ((automatic && entries[$0].automatic) || manualIDs.contains(entries[$0].sessionID))
        }.sorted {
            let left = entries[$0], right = entries[$1]
            if (left.manualClip == true) != (right.manualClip == true) { return left.manualClip == true }
            if left.createdAt != right.createdAt { return left.createdAt < right.createdAt }
            return left.sessionID < right.sessionID
        }
    }

    func nextAttemptDate(automatic: Bool, manualIDs: Set<String> = [], liveTraffic: Bool = false) -> Date? {
        let ordered = candidates(automatic: automatic, manualIDs: manualIDs, liveTraffic: liveTraffic)
        let manual = ordered.filter { entries[$0].manualClip == true }
        let automaticHead = ordered.first { entries[$0].manualClip != true }
        return (manual + (automaticHead.map { [$0] } ?? [])).map { entries[$0].nextAttemptAt }.min()
    }

    mutating func beginNext(now: Date, recording: Bool, automatic: Bool,
                            manualIDs: Set<String> = [], liveTraffic: Bool = false) -> Entry? {
        guard !recording, !entries.contains(where: { $0.state == .uploading }) else { return nil }
        let ordered = candidates(automatic: automatic, manualIDs: manualIDs, liveTraffic: liveTraffic)
        // backoff中の手動clipが、新しい手動指示を待たせない。
        let readyManual = ordered.first { entries[$0].manualClip == true && entries[$0].nextAttemptAt <= now }
        guard let index = readyManual ?? ordered.first(where: { entries[$0].manualClip != true }),
              entries[index].nextAttemptAt <= now else { return nil }
        entries[index].state = .uploading
        entries[index].reason = nil
        return entries[index]
    }

    mutating func beginUpload(sessionID: String) -> Int {
        guard let index = entries.firstIndex(where: { $0.sessionID == sessionID }) else { return 1 }
        let number = (entries[index].uploadAttempts ?? entries[index].attempts) + 1
        entries[index].uploadAttempts = number
        return number
    }

    mutating func finish(sessionID: String, now: Date, reason: String?, attempts: Int) {
        guard let index = entries.firstIndex(where: { $0.sessionID == sessionID }) else { return }
        entries[index].attempts += attempts
        entries[index].reason = reason
        entries[index].state = reason == nil ? .received : .failed
        entries[index].receivedAt = reason == nil ? now : nil
        // 1回失敗ごとにキューへ戻す。30/60/120/240/480秒、計6回で自動再試行を止める。
        let failures = max(0, entries[index].attempts - 1)
        entries[index].nextAttemptAt = now.addingTimeInterval(min(900, 30 * pow(2, Double(min(5, failures)))))
        prune()
    }

    mutating func pause(sessionID: String) {
        guard let index = entries.firstIndex(where: { $0.sessionID == sessionID }) else { return }
        entries[index].state = .pending
    }

    mutating func retryNow() -> Set<String> {
        let ids = Set(entries.filter(\.isPending).map(\.sessionID))
        for index in entries.indices where entries[index].isPending {
            if entries[index].uploadAttempts == nil { entries[index].uploadAttempts = entries[index].attempts }
            entries[index].attempts = 0
            entries[index].nextAttemptAt = .distantPast
            entries[index].automatic = true
        }
        return ids
    }

    private mutating func prune() {
        let received = entries.filter { $0.state == .received }.sorted {
            ($0.receivedAt ?? .distantPast) > ($1.receivedAt ?? .distantPast)
        }
        let keep = Set(received.prefix(20).map(\.sessionID))
        for entry in received where !keep.contains(entry.sessionID) {
            prunedBefore = max(prunedBefore, entry.createdAt)
        }
        entries.removeAll { $0.state == .received && !keep.contains($0.sessionID) }
    }

    static func load(from url: URL) throws -> Self {
        guard FileManager.default.fileExists(atPath: url.path) else { return Self() }
        return try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
    }

    func save(to url: URL) throws {
        try JSONEncoder().encode(self).write(to: url, options: .atomic)
    }
}

/// HTTPヘッダーと開始・終了ログで共有する。本文・受理判定には使わない。
struct DebugBundleUploadTrace {
    let sessionID: String
    let attempt: Int
    let destination: URL
    let packageBytes: Int
    let transferID = UUID().uuidString

    func apply(to request: inout URLRequest) {
        request.setValue(sessionID, forHTTPHeaderField: "X-PhoneSaber-Session-ID")
        request.setValue(transferID, forHTTPHeaderField: "X-PhoneSaber-Transfer-ID")
        request.setValue(String(attempt), forHTTPHeaderField: "X-PhoneSaber-Attempt")
    }

    func message(_ outcome: String, reason: String = "", now: Date = Date()) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return "[DebugTriageTransfer] time=\(formatter.string(from: now)) sessionID=\(sessionID) "
            + "transferID=\(transferID) sender-attempt=\(attempt) destination=\(destination.absoluteString) "
            + "package-bytes=\(packageBytes) outcome=\(outcome) reason=\(String(reflecting: reason))"
    }
}

/// 座標の台指定・手動IPとは独立した診断用の宛先。選択はネットワークI/Oを行わない。
struct DiagnosticsEndpoint: Equatable {
    enum Source: String { case manual = "手動", discovered = "Mac自動発見", coordinate = "座標の送り先" }
    let url: URL
    let source: Source

    static func receiverURL(host: String, port: Int = 8765) -> URL? {
        let host = host.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "."))
        guard !host.isEmpty, (1...65535).contains(port) else { return nil }
        var components = URLComponents()
        components.scheme = "http"
        components.host = host
        components.port = port
        components.path = "/v1/bundle"
        return components.url
    }

    static func choose(manualHost: String, discovered: URL?, coordinateHost: String) -> Self? {
        if !manualHost.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return receiverURL(host: manualHost).map { Self(url: $0, source: .manual) }
        }
        if let discovered { return Self(url: discovered, source: .discovered) }
        return receiverURL(host: coordinateHost).map { Self(url: $0, source: .coordinate) }
    }
}

/// 診断用Bonjourだけを非同期で探索する。カメラ・座標送信には接続しない。
final class DiagnosticsDestination: NSObject, ObservableObject, NetServiceBrowserDelegate, NetServiceDelegate {
    static let shared = DiagnosticsDestination()
    static let preferenceKey = "phoneSaberDiagnosticsHost"
    static let serviceType = "_phonesaber-diag._tcp."
    private let defaults: UserDefaults
    @Published var manualHost: String {
        didSet { defaults.set(manualHost.trimmingCharacters(in: .whitespacesAndNewlines), forKey: Self.preferenceKey) }
    }
    @Published private(set) var discoveredURL: URL?
    private var browser: NetServiceBrowser?
    private var services: [NetService] = []
    private var endpoints: [NetService: URL] = [:]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        manualHost = defaults.string(forKey: Self.preferenceKey) ?? ""
        super.init()
    }

    func selected(coordinateHost: String) -> DiagnosticsEndpoint? {
        DiagnosticsEndpoint.choose(manualHost: manualHost, discovered: discoveredURL, coordinateHost: coordinateHost)
    }

    func startDiscovery() {
        precondition(Thread.isMainThread)
        guard browser == nil, NSClassFromString("XCTestCase") == nil else { return }
        let browser = NetServiceBrowser()
        browser.delegate = self
        self.browser = browser
        browser.searchForServices(ofType: Self.serviceType, inDomain: "local.")
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        guard self.browser === browser, !services.contains(service) else { return }
        services.append(service)
        service.delegate = self
        service.resolve(withTimeout: 6)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        guard self.browser === browser else { return }
        services.first(where: { $0 == service })?.stop()
        services.removeAll { $0 == service }
        endpoints.removeValue(forKey: service)
        updateDiscoveredURL()
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard services.contains(sender), let host = sender.hostName,
              let url = DiagnosticsEndpoint.receiverURL(host: host, port: sender.port) else { return }
        endpoints[sender] = url
        updateDiscoveredURL()
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didNotSearch errorDict: [String: NSNumber]) {
        guard self.browser === browser else { return }
        browser.stop()
        self.browser = nil
        services.forEach { $0.stop() }
        services.removeAll()
        endpoints.removeAll()
        updateDiscoveredURL()
    }

    private func updateDiscoveredURL() {
        // 複数のMacがある場合も再描画や辞書の反復順で送り先を切り替えない。
        discoveredURL = endpoints.values.sorted { $0.absoluteString < $1.absoluteString }.first
    }
}

/// Sends only the files named in summary.json. Discovery, packaging, and retries
/// run after recording completion on queues independent from camera and UDP.
final class DebugBundleTransfer: NSObject {
    static let shared = DebugBundleTransfer()

    static let preferenceKey = "phoneSaberAutoTransferDebugBundles"
    private static let maximumImages = 20
    private static let maximumFiles = 43
    private static let maximumBytes: Int64 = 64 * 1024 * 1024

    private let workQueue = DispatchQueue(label: "PhoneSaberSender.debug-bundle-transfer",
                                          qos: .utility)
    static let didChange = Notification.Name("PhoneSaberDebugBundleOutboxDidChange")
    private let directory: URL
    private var storeURL: URL { directory.appendingPathComponent("debug-bundle-outbox.json") }
    private(set) var outbox = DebugBundleOutbox()
    private var recordingActive = false
    private var liveTrafficActive = false
    private var manualIDs: Set<String> = []
    private var retryWake: DispatchWorkItem?
    private let pathMonitor = NWPathMonitor()
    private var lanSession: URLSession?
    private var generation = 0
    private var activeSessionID: String?
    private var isBusy = false
    private var activeBundleURL: URL?
    private var activePackageURL: URL?
    private var destinationSubscription: AnyCancellable?
    private var discoveryTimeout: DispatchWorkItem?
    private var attempt = 0
    private var phase: AttemptPhase = .idle

    private enum AttemptPhase { case idle, searching, uploading, finished }

    private override convenience init() {
        self.init(directory: FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!,
                  monitorNetwork: true)
    }

#if DEBUG
    convenience init(directoryForTesting: URL) {
        self.init(directory: directoryForTesting, monitorNetwork: false)
    }
#endif

    private init(directory: URL, monitorNetwork: Bool) {
        self.directory = directory
        super.init()
        do { outbox = try DebugBundleOutbox.load(from: storeURL) }
        catch { print("[DebugTriageTransfer] outbox load failed: \(error.localizedDescription)") }
        recoverBundles(scan: true)
        pathMonitor.pathUpdateHandler = { [weak self] path in
            guard path.status == .satisfied else { return }
            DispatchQueue.main.async { self?.retryPending() }
        }
        if monitorNetwork {
            pathMonitor.start(queue: DispatchQueue(label: "PhoneSaberSender.debug-outbox-path"))
        }
    }

    private func persist() {
        do { try outbox.save(to: storeURL) }
        catch { print("[DebugTriageTransfer] outbox save failed: \(error.localizedDescription)") }
        NotificationCenter.default.post(name: Self.didChange, object: self)
    }

    private func recoverBundles(scan: Bool = false) {
        for path in outbox.recover(exists: { self.bundleFilesExist(at: $0.hasPrefix("/") ? URL(fileURLWithPath: $0) : self.directory.appendingPathComponent($0)) }) {
            print("[DebugTriageTransfer] dropping missing bundle: \(path)")
        }
        // Scan only on launch, when no builder can still be writing a bundle.
        // Recover files finalized before the app died between creation and registration.
        if scan {
            let roots = [directory, directory.appendingPathComponent("AutoClips")]
            let bundles = roots.flatMap {
                (try? FileManager.default.contentsOfDirectory(at: $0, includingPropertiesForKeys: nil)) ?? []
            }
            for url in bundles {
                guard url.lastPathComponent.hasPrefix("phone_saber_triage_"), bundleFilesExist(at: url),
                      let data = try? Data(contentsOf: url.appendingPathComponent("summary.json")),
                      let summary = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let id = summary["sessionID"] as? String else { continue }
                let created = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? Date()
                let clip = summary["autoClip"] as? [String: Any]
                outbox.register(sessionID: id, bundlePath: Self.storedPath(for: url, in: directory), now: created,
                                automatic: clip != nil || DebugRecordingLimits.shouldAutoTransfer(recordedFrames: summary["recordedFrameCount"] as? Int ?? 0),
                                deferDuringPlay: clip != nil, manualClip: Self.isManualClip(summary))
            }
        }
        persist()
    }

    private func bundleFilesExist(at url: URL) -> Bool {
        guard let data = try? Data(contentsOf: url.appendingPathComponent("summary.json")),
              let summary = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let images = summary["images"] as? [[String: Any]],
              FileManager.default.fileExists(atPath: url.appendingPathComponent("prompt.md").path) else { return false }
        return images.allSatisfy { image in
            guard let path = image["path"] as? String, let context = image["frameContextPath"] as? String else { return false }
            return FileManager.default.fileExists(atPath: url.appendingPathComponent(path).path)
                && FileManager.default.fileExists(atPath: url.appendingPathComponent(context).path)
        }
    }

    func setRecordingActive(_ active: Bool) {
        precondition(Thread.isMainThread)
        recordingActive = active
        if active, let id = activeSessionID, activePackageURL != nil {
            outbox.pause(sessionID: id)
            persist()
            finishCurrentBundle()
        } else if !active { startNextBundleIfNeeded() }
    }

    /// 座標送信中はclipを待機させ、開始済みの転送も中断して端末に保持する。
    func setLiveTrafficActive(_ active: Bool) {
        precondition(Thread.isMainThread)
        guard active != liveTrafficActive else { return }
        liveTrafficActive = active
        if active, let id = activeSessionID,
           outbox.entries.first(where: { $0.sessionID == id })?.deferDuringPlay == true {
            // 送信が再開したら手動指定も解き、次の静かな間まで待たせる。
            manualIDs.remove(id)
            outbox.pause(sessionID: id)
            persist()
            finishCurrentBundle()
        } else if !active { startNextBundleIfNeeded() }
    }

    func retryPending(immediately: Bool = false) {
        precondition(Thread.isMainThread)
        if !isBusy { recoverBundles() }
        if immediately { manualIDs.formUnion(outbox.retryNow()); persist() }
        startNextBundleIfNeeded()
    }

    static var automaticTransferEnabled: Bool {
        // Unit tests record real triage bundles on a simulator that shares the Mac's
        // network: they must never upload to the developer's receiver (2026-10-03 a
        // test run posted test bundles to the live receiver through the P2P relay).
        if NSClassFromString("XCTestCase") != nil { return false }
        return UserDefaults.standard.object(forKey: preferenceKey) as? Bool ?? true
    }

    static func isManualClip(_ summary: [String: Any]) -> Bool {
        let clip = summary["autoClip"] as? [String: Any]
        return (clip?["trigger"] as? [String: Any])?["kind"] as? String == "manual"
    }

    func enqueue(bundleURL: URL, automatic: Bool = true, deferDuringPlay: Bool = false) {
        DispatchQueue.main.async { [weak self] in
            guard let self,
                  let data = try? Data(contentsOf: bundleURL.appendingPathComponent("summary.json")),
                  let summary = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let id = summary["sessionID"] as? String else { return }
            self.outbox.register(sessionID: id, bundlePath: Self.storedPath(for: bundleURL, in: self.directory),
                                 now: Date(), automatic: automatic,
                                 deferDuringPlay: deferDuringPlay || summary["autoClip"] != nil,
                                 manualClip: Self.isManualClip(summary))
            self.persist()
            // 新しい手動clipは古い自動clipの通信タイムアウトを待たない。
            if Self.isManualClip(summary), let activeID = self.activeSessionID,
               let active = self.outbox.entries.first(where: { $0.sessionID == activeID }),
               active.deferDuringPlay == true, active.manualClip != true {
                self.outbox.pause(sessionID: activeID)
                self.persist()
                self.finishCurrentBundle()
            } else { self.startNextBundleIfNeeded() }
        }
    }

    private func startNextBundleIfNeeded() {
        precondition(Thread.isMainThread)
        retryWake?.cancel()
        retryWake = nil
        guard !isBusy, !recordingActive else { return }
        guard let entry = outbox.beginNext(now: Date(), recording: recordingActive,
                                          automatic: Self.automaticTransferEnabled, manualIDs: manualIDs,
                                          liveTraffic: liveTrafficActive) else {
            if let next = outbox.nextAttemptDate(automatic: Self.automaticTransferEnabled,
                                                  manualIDs: manualIDs, liveTraffic: liveTrafficActive) {
                let wake = DispatchWorkItem { [weak self] in self?.retryPending() }
                retryWake = wake
                DispatchQueue.main.asyncAfter(deadline: .now() + max(1, next.timeIntervalSinceNow), execute: wake)
            }
            return
        }
        // 座標送信中に「今すぐ送る」で始めた転送は、送信が続いていても止めない（persistの通知より前に記録）。
        let startedDuringLiveTraffic = liveTrafficActive
        persist()
        isBusy = true
        generation += 1
        let token = generation
        activeSessionID = entry.sessionID
        let activeBundleURL = entry.bundleURL(in: directory)
        self.activeBundleURL = activeBundleURL
        workQueue.async { [weak self] in
            do {
                let packageURL = try Self.makePackage(for: activeBundleURL)
                // 次のpackagingを始める前に旧世代の一時ファイルを片付ける。
                // workQueueは直列なので、同じclipの再開と固定package名が競合しない。
                DispatchQueue.main.sync {
                    guard let self, self.generation == token, self.activeBundleURL == activeBundleURL else {
                        try? FileManager.default.removeItem(at: packageURL)
                        return
                    }
                    self.activePackageURL = packageURL
                    let playResumed = self.liveTrafficActive && entry.deferDuringPlay == true && !startedDuringLiveTraffic
                    if self.recordingActive || playResumed {
                        if playResumed { self.manualIDs.remove(entry.sessionID) }
                        self.outbox.pause(sessionID: entry.sessionID)
                        self.persist()
                        self.finishCurrentBundle()
                    } else { self.startAttempt() }
                }
            } catch {
                print("[DebugTriageTransfer] package failed; local bundle preserved: \(error.localizedDescription)")
                DispatchQueue.main.async {
                    guard let self, self.generation == token else { return }
                    self.recordOutcome(reason: error.localizedDescription)
                    self.finishCurrentBundle()
                }
            }
        }
    }

    private func startAttempt() {
        precondition(Thread.isMainThread)
        guard activeBundleURL != nil, activePackageURL != nil else {
            finishCurrentBundle()
            return
        }
        attempt = 1
        let diagnostics = DiagnosticsDestination.shared
        diagnostics.startDiscovery()
        if !diagnostics.manualHost.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || diagnostics.discoveredURL != nil {
            uploadToSelectedDestination()
            return
        }
        phase = .searching
        destinationSubscription = diagnostics.$discoveredURL.dropFirst().sink { [weak self] url in
            guard let self, url != nil, self.phase == .searching else { return }
            // @Published は値を格納する前に通知するため、次のmainキューで選択する。
            DispatchQueue.main.async { [weak self] in
                guard let self, self.phase == .searching else { return }
                self.uploadToSelectedDestination()
            }
        }
        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .searching else { return }
            self.uploadToSelectedDestination()
        }
        discoveryTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: timeout)
    }

    private func uploadToSelectedDestination() {
        destinationSubscription = nil
        discoveryTimeout?.cancel()
        discoveryTimeout = nil
        guard let endpoint = DiagnosticsDestination.shared.selected(
            coordinateHost: P2PPreferredMac.coordinateDestination.lanHost) else {
            retryCurrentBundle(after: DebugBundleTransferError.invalidEndpoint)
            return
        }
        phase = .uploading
        upload(to: endpoint.url, attemptNumber: attempt)
    }

    private func upload(to url: URL, attemptNumber: Int) {
        guard let packageURL = activePackageURL, let sessionID = activeSessionID else { finishCurrentBundle(); return }
        let trace = DebugBundleUploadTrace(sessionID: sessionID, attempt: outbox.beginUpload(sessionID: sessionID),
                                          destination: url,
                                          packageBytes: (try? packageURL.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? -1)
        persist()
        var request = URLRequest(url: url, timeoutInterval: 120)
        request.httpMethod = "POST"
        request.setValue("application/vnd.phonesaber.triage-v1", forHTTPHeaderField: "Content-Type")
        request.setValue("close", forHTTPHeaderField: "Connection")
        trace.apply(to: &request)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 120
        configuration.timeoutIntervalForResource = 180
        let session = URLSession(configuration: configuration)
        lanSession = session
        let token = generation
        print(trace.message("started"))
        session.uploadTask(with: request, fromFile: packageURL) { [weak self] data, response, error in
            let status = (response as? HTTPURLResponse)?.statusCode ?? -1
            let accepted = error == nil && DebugBundleReceipt.accepts(status: status, body: data ?? Data(), sessionID: sessionID)
            // 中断後の旧世代でも、このHTTP転送の終了だけは同じIDで記録する。
            print(trace.message(accepted ? "uploaded-\(status)" : "failed-\(status)",
                                reason: error?.localizedDescription ?? (accepted ? "" : "invalid status or receipt")))
            DispatchQueue.main.async {
                guard let self, self.generation == token, self.attempt == attemptNumber, self.phase == .uploading else { return }
                if let error {
                    self.retryCurrentBundle(after: error)
                    return
                }
                guard let response = response as? HTTPURLResponse,
                      DebugBundleReceipt.accepts(status: response.statusCode, body: data ?? Data(), sessionID: self.activeSessionID) else {
                    let status = (response as? HTTPURLResponse)?.statusCode ?? -1
                    self.retryCurrentBundle(after: status == 200
                        ? DebugBundleTransferError.invalidReceipt : DebugBundleTransferError.httpStatus(status))
                    return
                }
                self.recordOutcome(reason: nil)
                self.finishCurrentBundle()
            }
        }.resume()
    }

    private func retryCurrentBundle(after error: Error) {
        precondition(Thread.isMainThread)
        stopDiscovery()
        // 再試行はoutboxが期限と総回数を管理し、待機中は手動clipへ送信権を譲る。
        if phase != .uploading {
            print("[DebugTriageTransfer] attempt failed; local bundle preserved: \(error.localizedDescription)")
        }
        recordOutcome(reason: error.localizedDescription)
        finishCurrentBundle()
    }

    private func stopDiscovery() {
        lanSession?.invalidateAndCancel()
        lanSession = nil
        destinationSubscription = nil
        discoveryTimeout?.cancel()
        discoveryTimeout = nil
    }

    private func recordOutcome(reason: String?) {
        guard let id = activeSessionID else { return }
        // 録画による一時停止では手動送信の要求を消費しない。
        if reason == nil { manualIDs.remove(id) }
        outbox.finish(sessionID: id, now: Date(), reason: reason, attempts: max(1, attempt))
        persist()
    }

    private func finishCurrentBundle() {
        precondition(Thread.isMainThread)
        generation += 1
        stopDiscovery()
        phase = .idle
        if let activePackageURL { try? FileManager.default.removeItem(at: activePackageURL) }
        activePackageURL = nil
        activeBundleURL = nil
        activeSessionID = nil
        attempt = 0
        isBusy = false
        startNextBundleIfNeeded()
    }

    /// Relative to Documents when inside it (also in a subfolder such as AutoClips/),
    /// so a container move after relaunch keeps the entry valid.
    static func storedPath(for bundleURL: URL, in directory: URL) -> String {
        let base = directory.standardizedFileURL.path
        let path = bundleURL.standardizedFileURL.path
        guard path.hasPrefix(base + "/") else { return bundleURL.path }
        return String(path.dropFirst(base.count + 1))
    }

    static func makePackage(for bundleURL: URL) throws -> URL {
        let root = bundleURL.standardizedFileURL
        var isDirectory: ObjCBool = false
        guard root.isFileURL,
              FileManager.default.fileExists(atPath: root.path, isDirectory: &isDirectory),
              isDirectory.boolValue,
              (try? root.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else {
            throw DebugBundleTransferError.invalidFiles
        }
        let summaryURL = root.appendingPathComponent("summary.json")
        let summaryData = try Data(contentsOf: summaryURL)
        guard let summary = try JSONSerialization.jsonObject(with: summaryData) as? [String: Any],
              let sessionID = summary["sessionID"] as? String,
              let imageCount = summary["selectedImageCount"] as? Int,
              (0...maximumImages).contains(imageCount),
              let images = summary["images"] as? [[String: Any]], images.count == imageCount else {
            throw DebugBundleTransferError.invalidSummary
        }

        var selectedPaths: Set<String> = ["summary.json", "prompt.md"]
        for image in images {
            guard let imagePath = image["path"] as? String,
                  let framePath = image["frameContextPath"] as? String else {
                throw DebugBundleTransferError.invalidSummary
            }
            selectedPaths.insert(try checkedRelativePath(imagePath, prefix: "images/", suffix: ".png"))
            selectedPaths.insert(try checkedRelativePath(framePath, prefix: "frames/", suffix: ".json"))
        }
        guard selectedPaths.count == 2 + imageCount * 2,
              selectedPaths.count <= maximumFiles else {
            throw DebugBundleTransferError.invalidFiles
        }

        let logURL = root.appendingPathComponent("connectivity.log")
        if FileManager.default.fileExists(atPath: logURL.path) {
            guard (try logURL.resourceValues(forKeys: [.fileSizeKey])).fileSize.map({ $0 <= 64 * 1024 }) == true else {
                throw DebugBundleTransferError.invalidFiles
            }
            selectedPaths.insert("connectivity.log")
        }
        let names = selectedPaths.sorted()
        var files: [DebugBundleManifestFile] = []
        var totalBytes: Int64 = 0
        for name in names {
            let fileURL = root.appendingPathComponent(name)
            guard let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]),
                  values.isRegularFile == true, values.isSymbolicLink != true,
                  let fileSize = values.fileSize, fileSize >= 0 else {
                throw DebugBundleTransferError.unsafePath(name)
            }
            totalBytes += Int64(fileSize)
            guard totalBytes <= maximumBytes else { throw DebugBundleTransferError.bundleTooLarge }
            let digest = try sha256(fileURL)
            let contentType: String
            if name.hasSuffix(".png") { contentType = "image/png" }
            else if name == "connectivity.log" { contentType = "text/plain; charset=utf-8" }
            else if name.hasSuffix(".md") { contentType = "text/markdown; charset=utf-8" }
            else { contentType = "application/json; charset=utf-8" }
            files.append(DebugBundleManifestFile(path: name, size: Int64(fileSize),
                                                 sha256: digest, contentType: contentType))
        }

        let manifest = DebugBundleManifest(sessionID: sessionID, files: files)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let manifestData = try encoder.encode(manifest)
        guard manifestData.count <= 128 * 1024 else { throw DebugBundleTransferError.invalidFiles }
        let envelopeSize = Int64(8 + manifestData.count) + totalBytes
        guard envelopeSize <= maximumBytes else { throw DebugBundleTransferError.bundleTooLarge }

        let packageURL = root.deletingLastPathComponent()
            .appendingPathComponent("\(sessionID)_triage_transfer.psbt")
        guard FileManager.default.createFile(atPath: packageURL.path, contents: nil) else {
            throw DebugBundleTransferError.invalidFiles
        }
        do {
            let output = try FileHandle(forWritingTo: packageURL)
            defer { try? output.close() }
            try output.write(contentsOf: Data("PSBT".utf8))
            var bigEndianLength = UInt32(manifestData.count).bigEndian
            try withUnsafeBytes(of: &bigEndianLength) { try output.write(contentsOf: Data($0)) }
            try output.write(contentsOf: manifestData)
            for name in names {
                let input = try FileHandle(forReadingFrom: root.appendingPathComponent(name))
                while let chunk = try input.read(upToCount: 1024 * 1024), !chunk.isEmpty {
                    try output.write(contentsOf: chunk)
                }
                try input.close()
            }
            guard DebugRecordingStorage.diskUsage(
                sessionID: sessionID, in: root.deletingLastPathComponent()
            ) <= DebugRecordingLimits.maximumDiskUsageBytes else {
                throw DebugBundleTransferError.bundleTooLarge
            }
            return packageURL
        } catch {
            try? FileManager.default.removeItem(at: packageURL)
            throw error
        }
    }

    private static func checkedRelativePath(_ path: String, prefix: String, suffix: String) throws -> String {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, path.hasPrefix(prefix), path.hasSuffix(suffix),
              !parts.contains("."), !parts.contains(".."), !path.contains("\\") else {
            throw DebugBundleTransferError.unsafePath(path)
        }
        return path
    }

    private static func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var digest = SHA256()
        while let chunk = try handle.read(upToCount: 1024 * 1024), !chunk.isEmpty {
            digest.update(data: chunk)
        }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
}


enum DebugBundleReceipt {
    static func accepts(status: Int, body: Data, sessionID: String?) -> Bool {
        // Old receivers reply 201. A bare 409 is always a failure.
        guard status == 200 else { return (200..<300).contains(status) }
        guard let sessionID,
              let receipt = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return false }
        return receipt["status"] as? String == "duplicate" && receipt["sessionID"] as? String == sessionID
    }
}

/// Uploads one triage package over Network.framework with peer-to-peer Wi-Fi
/// allowed (cellular prohibited) to the Mac P2P bridge's diagnostics relay
/// (`_phonesaber-dp2p._tcp`), which pipes it to the unchanged receiver. Same HTTP
/// request as the LAN path (POST /v1/bundle, Content-Length, Connection: close).
final class P2PBundleUploader {
    enum Outcome { case uploaded(String), notFound, failed(Error) }

    struct UploadError: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    private let queue = DispatchQueue(label: "PhoneSaberSender.diag-p2p", qos: .utility)
    private let serviceType: String
    private let endpointOverride: NWEndpoint?
    private let discoveryTimeout: TimeInterval
    private let transferTimeout: TimeInterval
    private var browser: NWBrowser?
    private var connection: NWConnection?
    private var completion: ((Outcome) -> Void)?
    private var file: FileHandle?
    private var response = Data()
    private var serviceName = ""
    private var sessionID: String?
    private let preferredService: () -> String?
    private let station: String
    /// Latest discovery results, used once more when discovery times out.
    private var discovered: [(endpoint: NWEndpoint, name: String)] = []
    /// Kept until `finish`, so a caller may drop its reference while the upload runs.
    private var keepAlive: P2PBundleUploader?

    init(serviceType: String = PhoneSaberP2P.diagnosticsServiceType, endpointOverride: NWEndpoint? = nil,
         discoveryTimeout: TimeInterval = 3, transferTimeout: TimeInterval = 180,
         preferredService: @escaping () -> String? = { P2PPreferredMac.current },
         station: String? = nil) {
        self.serviceType = serviceType
        self.preferredService = preferredService
        self.station = station ?? P2PPreferredMac.coordinateDestination.station
        self.endpointOverride = endpointOverride
        self.discoveryTimeout = discoveryTimeout
        self.transferTimeout = transferTimeout
    }

    func upload(fileURL: URL, sessionID: String? = nil, completion: @escaping (Outcome) -> Void) {
        queue.async { [self] in
            self.keepAlive = self
            self.sessionID = sessionID
            self.completion = completion
            guard let size = (try? FileManager.default.attributesOfItem(atPath: fileURL.path)[.size]) as? NSNumber,
                  let handle = try? FileHandle(forReadingFrom: fileURL) else {
                finish(.failed(UploadError(message: "package unreadable")))
                return
            }
            file = handle
            if let endpointOverride {
                connect(to: endpointOverride, name: "\(endpointOverride)", size: size.int64Value)
                return
            }
            let browser = NWBrowser(for: .bonjour(type: serviceType, domain: nil), using: parameters())
            self.browser = browser
            browser.browseResultsChangedHandler = { [weak self] results, _ in
                guard let self, self.connection == nil else { return }
                self.discovered = results.compactMap { result -> (endpoint: NWEndpoint, name: String)? in
                    if case .service(let name, _, _, _) = result.endpoint { return (result.endpoint, name) }
                    return nil
                }
                self.connectToDiscovered(final: false, size: size.int64Value)
            }
            browser.stateUpdateHandler = { [weak self] state in
                if case .failed = state { self?.finish(.notFound) }
            }
            browser.start(queue: queue)
            queue.asyncAfter(deadline: .now() + discoveryTimeout) { [weak self] in
                guard let self, self.connection == nil, self.completion != nil else { return }
                if !self.connectToDiscovered(final: true, size: size.int64Value) { self.finish(.notFound) }
            }
        }
    }

    /// Connects to the service `P2PDiagnosticsServicePicker` chooses; false when none yet.
    @discardableResult
    private func connectToDiscovered(final: Bool, size: Int64) -> Bool {
        guard let name = P2PDiagnosticsServicePicker.pick(from: discovered.map(\.name),
                                                          preferred: preferredService(), final: final, station: station),
              let chosen = discovered.first(where: { $0.name == name }) else { return false }
        browser?.cancel()
        browser = nil
        connect(to: chosen.endpoint, name: chosen.name, size: size)
        return true
    }

    func cancel() {
        queue.async { [weak self] in
            self?.completion = nil
            self?.finish(.notFound)
        }
    }

    private func parameters() -> NWParameters {
        let parameters = NWParameters.tcp
        parameters.includePeerToPeer = true
        parameters.prohibitedInterfaceTypes = [.cellular]
        return parameters
    }

    private func connect(to endpoint: NWEndpoint, name: String, size: Int64) {
        serviceName = name
        let connection = NWConnection(to: endpoint, using: parameters())
        self.connection = connection
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                let header = "POST /v1/bundle HTTP/1.1\r\nHost: phonesaber\r\n"
                    + "Content-Type: application/vnd.phonesaber.triage-v1\r\n"
                    + "Content-Length: \(size)\r\nConnection: close\r\n\r\n"
                connection.send(content: Data(header.utf8), completion: .contentProcessed { error in
                    if let error { self.finish(.failed(error)) } else { self.sendNextChunk() }
                })
                self.receiveResponse()
            case .failed(let error):
                self.finish(.failed(error))
            case .waiting(let error):
                self.finish(.failed(error))
            default:
                break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + transferTimeout) { [weak self] in
            self?.finish(.failed(UploadError(message: "P2P upload timed out")))
        }
    }

    private func sendNextChunk() {
        guard let connection, let file else { return }
        let chunk: Data
        do {
            chunk = try file.read(upToCount: 256 * 1024) ?? Data()
        } catch {
            // 読み取り失敗を EOF として送らず、元のエラーで接続とファイルを閉じる。
            finish(.failed(error))
            return
        }
        if chunk.isEmpty {
            // Request complete; the relay half-closes towards the receiver.
            connection.send(content: nil, contentContext: .finalMessage, isComplete: true,
                            completion: .contentProcessed { _ in })
            return
        }
        connection.send(content: chunk, completion: .contentProcessed { [weak self] error in
            if let error { self?.finish(.failed(error)) } else { self?.sendNextChunk() }
        })
    }

    private func receiveResponse() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data { self.response.append(data) }
            if let status = Self.statusCode(self.response),
               let end = self.response.range(of: Data("\r\n\r\n".utf8)) {
                let body = Data(self.response[end.upperBound...])
                let header = String(decoding: self.response[..<end.lowerBound], as: UTF8.self)
                let length = header.components(separatedBy: "\r\n").first {
                    $0.lowercased().hasPrefix("content-length:")
                }.flatMap { Int($0.split(separator: ":", maxSplits: 1).last?.trimmingCharacters(in: .whitespaces) ?? "") }
                // Duplicate receipts may arrive after the headers, in another TCP chunk.
                if status != 200 || (length.map { body.count >= $0 } ?? isComplete) {
                    if DebugBundleReceipt.accepts(status: status, body: body, sessionID: self.sessionID) {
                        self.finish(.uploaded(self.serviceName))
                    } else {
                        self.finish(.failed(UploadError(message: "receiver HTTP \(status) or invalid receipt")))
                    }
                    return
                }
            }
            if self.response.count > 64 * 1024 {
                self.finish(.failed(UploadError(message: "receiver response too large")))
                return
            }
            if isComplete || error != nil {
                self.finish(.failed(error ?? UploadError(message: "connection closed without a response")))
                return
            }
            self.receiveResponse()
        }
    }

    static func statusCode(_ response: Data) -> Int? {
        guard let line = String(data: response.prefix(64), encoding: .ascii)?
                .split(separator: "\r\n", maxSplits: 1).first,
              line.hasPrefix("HTTP/1.") else { return nil }
        let fields = line.split(separator: " ")
        return fields.count >= 2 ? Int(fields[1]) : nil
    }

    private func finish(_ outcome: Outcome) {
        browser?.cancel()
        browser = nil
        connection?.cancel()
        connection = nil
        try? file?.close()
        file = nil
        let completion = self.completion
        self.completion = nil
        completion?(outcome)
        keepAlive = nil
    }
}
