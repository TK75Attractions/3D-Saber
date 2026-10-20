import SwiftUI

@main
struct PhoneSaberSenderApp: App {
    // View の再表示や scene の復帰では重複記録しない。
    private static let signingExpiryLaunchLog: Void = {
        ConnectivityLog.shared.record("SIGNING_EXPIRY", SigningExpiry.current.logMessage(at: Date()))
    }()

    init() {
        _ = Self.signingExpiryLaunchLog
    }

    var body: some Scene {
        WindowGroup { ContentView() }
    }
}
