import CryptoKit
import Foundation

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

    var errorDescription: String? {
        switch self {
        case .invalidSummary: return "summary.json is invalid"
        case .unsafePath(let path): return "unsafe triage bundle path: \(path)"
        case .invalidFiles: return "triage bundle file list is invalid"
        case .bundleTooLarge: return "triage upload exceeds the 512 MiB limit"
        case .discoveryTimeout: return "PhoneSaber diagnostics receiver was not found"
        case .invalidEndpoint: return "receiver hostname or port is invalid"
        case .httpStatus(let status): return "receiver returned HTTP \(status)"
        }
    }
}

/// Sends only the files named in summary.json. Discovery, packaging, and retries
/// run after recording completion on queues independent from camera and UDP.
final class DebugBundleTransfer: NSObject, NetServiceBrowserDelegate, NetServiceDelegate {
    static let shared = DebugBundleTransfer()

    static let preferenceKey = "phoneSaberAutoTransferDebugBundles"
    static let serviceType = "_phonesaber-diag._tcp."
    static let serviceName = "Phone Saber Diagnostics"
    private static let maximumImages = 20
    private static let maximumFiles = 42
    private static let maximumBytes: Int64 = 512 * 1024 * 1024
    private static let maximumAttempts = 3

    private let workQueue = DispatchQueue(label: "PhoneSaberSender.debug-bundle-transfer",
                                          qos: .utility)
    private var pendingBundles: [URL] = []
    private var isBusy = false
    private var activeBundleURL: URL?
    private var activePackageURL: URL?
    private var browser: NetServiceBrowser?
    private var resolvedService: NetService?
    private var discoveryTimeout: DispatchWorkItem?
    private var attempt = 0
    private var phase: AttemptPhase = .idle

    private enum AttemptPhase { case idle, searching, resolving, uploading, finished }

    private override init() { super.init() }

    static var automaticTransferEnabled: Bool {
        UserDefaults.standard.object(forKey: preferenceKey) as? Bool ?? true
    }

    func enqueue(bundleURL: URL) {
        DispatchQueue.main.async { [weak self] in
            guard Self.automaticTransferEnabled else {
                print("[DebugTriageTransfer] disabled; bundle kept at \(bundleURL.path)")
                return
            }
            self?.pendingBundles.append(bundleURL)
            self?.startNextBundleIfNeeded()
        }
    }

    private func startNextBundleIfNeeded() {
        precondition(Thread.isMainThread)
        guard !isBusy, !pendingBundles.isEmpty else { return }
        isBusy = true
        activeBundleURL = pendingBundles.removeFirst()
        guard let activeBundleURL else { finishCurrentBundle(); return }
        workQueue.async { [weak self] in
            do {
                let packageURL = try Self.makePackage(for: activeBundleURL)
                DispatchQueue.main.async {
                    guard let self, self.activeBundleURL == activeBundleURL else {
                        try? FileManager.default.removeItem(at: packageURL)
                        return
                    }
                    self.activePackageURL = packageURL
                    self.startAttempt(1)
                }
            } catch {
                print("[DebugTriageTransfer] package failed; local bundle preserved: \(error.localizedDescription)")
                DispatchQueue.main.async { self?.finishCurrentBundle() }
            }
        }
    }

    private func startAttempt(_ number: Int) {
        precondition(Thread.isMainThread)
        guard activeBundleURL != nil, activePackageURL != nil else {
            finishCurrentBundle()
            return
        }
        attempt = number
        phase = .searching
        resolvedService = nil
        let browser = NetServiceBrowser()
        browser.delegate = self
        self.browser = browser
        browser.searchForServices(ofType: Self.serviceType, inDomain: "local.")

        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.phase == .searching || self.phase == .resolving else { return }
            self.retryCurrentBundle(after: DebugBundleTransferError.discoveryTimeout)
        }
        discoveryTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: timeout)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService,
                           moreComing: Bool) {
        guard self.browser === browser, service.name == Self.serviceName,
              phase == .searching else { return }
        phase = .resolving
        resolvedService = service
        service.delegate = self
        service.resolve(withTimeout: 6)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didNotSearch errorDict: [String: NSNumber]) {
        guard self.browser === browser, phase == .searching else { return }
        retryCurrentBundle(after: NSError(domain: "PhoneSaberBonjour", code: 1,
                                          userInfo: [NSLocalizedDescriptionKey: "Bonjour browse failed"]))
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard sender === resolvedService, phase == .resolving else { return }
        discoveryTimeout?.cancel()
        guard let url = Self.receiverURL(hostName: sender.hostName, port: sender.port) else {
            retryCurrentBundle(after: DebugBundleTransferError.invalidEndpoint)
            return
        }
        phase = .uploading
        stopDiscovery()
        upload(to: url, attemptNumber: attempt)
    }

    func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) {
        guard sender === resolvedService, phase == .resolving else { return }
        retryCurrentBundle(after: NSError(domain: "PhoneSaberBonjour", code: 2,
                                          userInfo: [NSLocalizedDescriptionKey: "Bonjour resolve failed"]))
    }

    private func upload(to url: URL, attemptNumber: Int) {
        guard let packageURL = activePackageURL else { finishCurrentBundle(); return }
        var request = URLRequest(url: url, timeoutInterval: 120)
        request.httpMethod = "POST"
        request.setValue("application/vnd.phonesaber.triage-v1", forHTTPHeaderField: "Content-Type")
        request.setValue("close", forHTTPHeaderField: "Connection")
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 120
        configuration.timeoutIntervalForResource = 180
        let session = URLSession(configuration: configuration)
        session.uploadTask(with: request, fromFile: packageURL) { [weak self] _, response, error in
            DispatchQueue.main.async {
                guard let self, self.attempt == attemptNumber, self.phase == .uploading else { return }
                if let error {
                    self.retryCurrentBundle(after: error)
                    return
                }
                guard let response = response as? HTTPURLResponse,
                      (200..<300).contains(response.statusCode) else {
                    let status = (response as? HTTPURLResponse)?.statusCode ?? -1
                    self.retryCurrentBundle(after: DebugBundleTransferError.httpStatus(status))
                    return
                }
                print("[DebugTriageTransfer] uploaded selected bundle to \(url.host ?? "receiver")")
                self.finishCurrentBundle()
            }
        }.resume()
    }

    private func retryCurrentBundle(after error: Error) {
        precondition(Thread.isMainThread)
        stopDiscovery()
        guard attempt < Self.maximumAttempts else {
            print("[DebugTriageTransfer] stopped after \(attempt) attempts; local bundle preserved: \(error.localizedDescription)")
            finishCurrentBundle()
            return
        }
        let nextAttempt = attempt + 1
        phase = .finished
        let delay = Double(attempt)
        print("[DebugTriageTransfer] retry \(nextAttempt)/\(Self.maximumAttempts): \(error.localizedDescription)")
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, self.activeBundleURL != nil else { return }
            self.startAttempt(nextAttempt)
        }
    }

    private func stopDiscovery() {
        discoveryTimeout?.cancel()
        discoveryTimeout = nil
        browser?.stop()
        browser?.delegate = nil
        browser = nil
        resolvedService?.stop()
        resolvedService?.delegate = nil
        resolvedService = nil
    }

    private func finishCurrentBundle() {
        precondition(Thread.isMainThread)
        stopDiscovery()
        phase = .idle
        if let activePackageURL { try? FileManager.default.removeItem(at: activePackageURL) }
        activePackageURL = nil
        activeBundleURL = nil
        attempt = 0
        isBusy = false
        startNextBundleIfNeeded()
    }

    private static func receiverURL(hostName: String?, port: Int) -> URL? {
        guard let hostName, !hostName.isEmpty, (1...65535).contains(port) else { return nil }
        var components = URLComponents()
        components.scheme = "http"
        components.host = hostName.trimmingCharacters(in: CharacterSet(charactersIn: "."))
        components.port = port
        components.path = "/v1/bundle"
        return components.url
    }

    private static func makePackage(for bundleURL: URL) throws -> URL {
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

        let packageURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("phonesaber-triage-\(UUID().uuidString).psbt")
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
