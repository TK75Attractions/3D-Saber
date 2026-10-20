import Foundation
import XCTest
#if !SIGNING_EXPIRY_STANDALONE
@testable import PhoneSaberSender
#endif

final class SigningExpiryTests: XCTestCase {
    private let expiration = ISO8601DateFormatter().date(from: "2026-10-26T07:19:00Z")!

    private func profile(_ body: String) -> Data {
        // CMS の前後に UTF-8 でないバイトがあっても、XML 部分だけを読む。
        Data([0x30, 0x82, 0xff, 0x00]) + Data("<plist version=\"1.0\"><dict>\(body)</dict></plist>".utf8)
            + Data([0x00, 0xfe, 0x81])
    }

    func testExtractsExpirationDateFromSyntheticCMS() {
        let data = profile("<key>ExpirationDate</key><date>2026-10-26T07:19:00Z</date>")
        XCTAssertEqual(SigningExpiry.parseExpirationDate(data), expiration)
    }

    func testRejectsMissingMalformedAndWrongType() {
        for data in [Data(), Data("</plist><plist>".utf8),
                     Data("<plist><dict>".utf8), Data("<plist>broken</plist>".utf8),
                     profile("<key>Name</key><string>test</string>"),
                     profile("<key>ExpirationDate</key><string>2026-10-26</string>")] {
            XCTAssertNil(SigningExpiry.parseExpirationDate(data))
        }
    }

    func testWarningBoundaryHoursAndExpiry() {
        let expiry = SigningExpiry(expirationDate: expiration)
        let boundary = expiration.addingTimeInterval(-48 * 3600)
        XCTAssertFalse(expiry.needsReinstall(at: boundary))
        XCTAssertTrue(expiry.needsReinstall(at: boundary.addingTimeInterval(1)))
        XCTAssertEqual(expiry.remainingHours(at: boundary), 48)
        XCTAssertEqual(expiry.remainingHours(at: boundary.addingTimeInterval(1)), 47)
        XCTAssertTrue(expiry.displayText(at: boundary).hasSuffix("（あと2日）"))
        XCTAssertTrue(expiry.displayText(at: boundary.addingTimeInterval(1)).hasSuffix("（あと1日）"))
        for now in [expiration, expiration.addingTimeInterval(3600)] {
            XCTAssertTrue(expiry.needsReinstall(at: now))
            XCTAssertEqual(expiry.remainingHours(at: now), 0)
            XCTAssertTrue(expiry.displayText(at: now).hasSuffix("（あと0日）"))
        }
        XCTAssertTrue(expiry.logMessage(at: boundary).contains("remainingHours=48"))
    }

    func testFormatsDeviceLocalTimeAndHandlesUnavailableProfile() {
        let previous = NSTimeZone.default
        NSTimeZone.default = TimeZone(identifier: "Asia/Tokyo")!
        defer { NSTimeZone.default = previous }
        XCTAssertEqual(SigningExpiry(expirationDate: expiration).displayText(at: expiration.addingTimeInterval(-5 * 86400)),
                       "アプリの期限: 10/26 16:19（あと5日）")
        let unknown = SigningExpiry(expirationDate: nil)
        XCTAssertNil(unknown.remainingHours(at: expiration))
        XCTAssertFalse(unknown.needsReinstall(at: expiration))
        XCTAssertEqual(unknown.displayText(at: expiration), "アプリの期限: 確認できません")
        XCTAssertTrue(unknown.logMessage(at: expiration).contains("remainingHours=unknown"))
    }
}
