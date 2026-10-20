import Foundation

struct SigningExpiry {
    // CMS 全体を文字列にせず、バイナリ内の plist 部分だけを読む。通信は行わない。
    static let current: SigningExpiry = {
        let data = Bundle.main.url(forResource: "embedded", withExtension: "mobileprovision")
            .flatMap { try? Data(contentsOf: $0) }
        return SigningExpiry(expirationDate: data.flatMap(parseExpirationDate))
    }()

    let expirationDate: Date?

    static func parseExpirationDate(_ data: Data) -> Date? {
        guard let start = data.range(of: Data("<plist".utf8)),
              let end = data.range(of: Data("</plist>".utf8), in: start.lowerBound..<data.endIndex),
              let plist = try? PropertyListSerialization.propertyList(
                from: data.subdata(in: start.lowerBound..<end.upperBound), options: [], format: nil),
              let dictionary = plist as? [String: Any] else { return nil }
        return dictionary["ExpirationDate"] as? Date
    }

    func remainingHours(at now: Date) -> Int? {
        expirationDate.map { Int(floor(max(0, $0.timeIntervalSince(now)) / 3600)) }
    }

    func needsReinstall(at now: Date) -> Bool {
        expirationDate.map { $0.timeIntervalSince(now) < 2 * 24 * 3600 } ?? false
    }

    func displayText(at now: Date) -> String {
        guard let expirationDate else { return "アプリの期限: 確認できません" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "ja_JP")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = .current
        formatter.dateFormat = "M/d HH:mm"
        let days = Int(floor(max(0, expirationDate.timeIntervalSince(now)) / (24 * 3600)))
        return "アプリの期限: \(formatter.string(from: expirationDate))（あと\(days)日）"
    }

    func logMessage(at now: Date) -> String {
        let hours = remainingHours(at: now).map { "\($0)" } ?? "unknown"
        return "\(displayText(at: now)) remainingHours=\(hours)"
    }
}
