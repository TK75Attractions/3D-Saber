import QuickLook
import SwiftUI

struct ContentView: View {
    @StateObject private var model = CameraViewModel()
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("phoneSaberAutoTransferDebugBundles") private var autoTransferDebugBundles = true
    @State private var recordingPreviewURL: URL?
    @State private var showRecordingCleanupConfirmation = false
#if DEBUG
    @State private var showDebugPerformance = false
#endif

    var body: some View {
        let pathDetail = model.pathInterface.isEmpty ? "" : " (\(model.pathInterface))"
        NavigationStack {
            ScrollView {
                VStack(spacing: 12) {
                    GroupBox("接続") {
                        Picker("台", selection: Binding(
                            get: { model.station }, set: { model.setStation($0) }
                        )) {
                            Text("指定なし").tag("")
                            Text("A").tag("A")
                            Text("B").tag("B")
                        }
                        .disabled(model.running)
                        Button("Macを再検索") { model.retryDiscovery() }
                            .disabled(model.running)
                        Text(model.networkDiscoveryStatus)
                            .font(.caption).foregroundStyle(.secondary)
                        Text(model.networkStateLabel)
                            .font(.headline)
                        Text(model.cameraState.displayLabel)
                            .font(.headline)
                            .foregroundStyle(model.cameraState == .live ? .green : .orange)
                        if let cameraDetail = model.cameraState.detail {
                            Text(cameraDetail)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        if let frameAge = model.lastCameraFrameAge {
                            Text("Last camera frame: \(frameAge, specifier: "%.2f") s ago")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                        if model.running && model.cameraState.canRetry {
                            Button("カメラを再開") { model.retryCameraRecovery() }
                        }
                        Text("Mac: \(model.discoveredMacName.isEmpty ? "未発見" : model.discoveredMacName)　IP: \(model.discoveredMacIP.isEmpty ? "-" : model.discoveredMacIP)")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("Mode: \(model.connectionMode)　Red: 5005　Blue: 5006")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("経路: \(model.transportLabel)")
                            .font(.footnote.weight(.semibold))
                        Toggle("P2P優先 (peer-to-peer Wi-Fi・Mac側 P2P bridge)", isOn: Binding(
                            get: { model.p2pEnabled },
                            set: { model.setP2PEnabled($0) }
                        ))
                            .font(.footnote)
                        if model.p2pState.isConnected, let rtt = model.p2pRoundTrip {
                            Text(String(format: "P2P RTT 中央値 %.1f ms / p95 %.1f ms / 最大 %.1f ms / ping欠落 %.0f%%",
                                        rtt.medianMs, rtt.p95Ms, rtt.maxMs, rtt.lostPercent))
                                .font(.caption2.monospacedDigit()).foregroundStyle(.secondary)
                        }
                        if model.p2pEnabled && !model.p2pState.isConnected {
                            Text("\(model.p2pState.label)。届かない間は LAN(Bonjour / 手動IP)で送ります")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                        TextField("手動IP（自動発見できない場合のみ）", text: Binding(
                            get: { model.host },
                            set: { model.setManualHost($0) }
                        ))
                            .textFieldStyle(.roundedBorder)
                            .keyboardType(.URL)
                            .autocorrectionDisabled(true)
                            .textInputAutocapitalization(.never)
                            .disabled(model.running)
                        Toggle("起動時に送信を自動開始", isOn: $model.autoStartSending)
                            .font(.footnote)
                        if !model.automaticResumeMessage.isEmpty {
                            Text(model.automaticResumeMessage).font(.footnote).foregroundStyle(.green)
                        }
                        if !model.cameraRecoveryMessage.isEmpty {
                            Text(model.cameraRecoveryMessage).font(.footnote).foregroundStyle(.orange)
                        }
                        if let message = model.pcReconnectMessage {
                            Text(message).font(.footnote).foregroundStyle(.orange)
                        }
                        Button(model.sendingRequested ? "停止" : (model.measurementMode ? "遅延計測を開始" : "通常送信を開始")) {
                            model.sendingRequested ? model.stop() : model.start()
                        }
                        .buttonStyle(.borderedProminent)
                        Text("BonjourでMacを自動発見します。見つからない時だけ手動IPを入力します。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    ZStack {
                        CameraPreview(session: model.session).frame(height: 280)
                        GeometryReader { proxy in
                            Canvas { context, size in
                                draw(model.redEndpoints, color: .red, context: &context, size: size)
                                draw(model.blueEndpoints, color: .blue, context: &context, size: size)
                            }
                        }.frame(height: 280)
                    }.clipped()
                    Text(model.deviceHealthLine)
                        .font(.footnote.monospacedDigit())
                    if let warning = model.deviceHealthWarning {
                        Text(warning).font(.footnote).foregroundStyle(.orange)
                    }
                    GroupBox("Debug Recording") {
                        VStack(alignment: .leading, spacing: 8) {
                            Toggle("Debug Recording: \(model.debugRecordingEnabled ? "ON" : "OFF")",
                                   isOn: $model.debugRecordingEnabled)
                                .disabled(model.debugRecordingActive || model.debugRecordingFinalizing)
                            Picker("診断対象の色", selection: $model.debugDiagnosticColors) {
                                ForEach(DebugDiagnosticColors.allCases) { Text($0.rawValue).tag($0) }
                            }
                            .pickerStyle(.segmented)
                            .disabled(model.debugRecordingActive || model.debugRecordingFinalizing)
                            Text("選んだ色以外の未検出は診断の失敗として数えません（認識とUDP送信は常に両色）")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                            Text("露出実験（最大シャッター時間・ISOは自動）")
                                .font(.caption)
                            Picker("露出実験", selection: Binding(
                                get: { model.cameraExposureExperiment },
                                set: { model.setCameraExposureExperiment($0) }
                            )) {
                                ForEach(CameraExposureExperiment.allCases) { Text($0.title).tag($0) }
                            }
                            .pickerStyle(.segmented)
                            .disabled(!model.cameraExposureExperimentEditable)
                            Text("実験用。自動(既定)はカメラ露出を一切変更しません。速い振りのブレ確認用で、画面が暗くなる・ノイズが増えることがあります。録画中は変更できません。状態: \(model.cameraExposureExperimentState.displayText)")
                                .font(.caption2)
                                .foregroundStyle(model.cameraExposureExperimentState.capActive ? .orange : .secondary)
                            Toggle("Stop後にtriage bundleをMacへ自動転送",
                                   isOn: $autoTransferDebugBundles)
                            HStack {
                                Button("Start Recording") { model.startDebugRecording() }
                                    .disabled(!model.debugRecordingEnabled || !model.running ||
                                              model.debugRecordingActive || model.debugRecordingFinalizing)
                                Button("Stop Recording") { model.stopDebugRecording() }
                                    .disabled(!model.debugRecordingActive || model.debugRecordingFinalizing)
                                Button("Capture Lossless Frame") { model.captureNextLosslessFrame() }
                                    .disabled(!model.debugRecordingEnabled || !model.debugRecordingActive ||
                                              model.debugRecordingFinalizing || model.manualLosslessCapturePending ||
                                              model.debugManualLosslessCaptureCount >= DebugRecordingLimits.maximumManualLosslessCaptures)
                            }
                            Button { model.startGuidedRecording() } label: {
                                Label("ガイド付き録画を開始（約\(Int(model.guidedRecordingScript.totalSeconds.rounded(.up)) / 60)分\(Int(model.guidedRecordingScript.totalSeconds.rounded(.up)) % 60)秒）",
                                      systemImage: "speaker.wave.2")
                            }
                            .buttonStyle(.bordered)
                            .disabled(!model.debugRecordingEnabled || !model.running ||
                                      model.debugRecordingActive || model.debugRecordingFinalizing)
                            DisclosureGroup("ガイド付き録画の手順") {
                                VStack(alignment: .leading, spacing: 2) {
                                    ForEach(Array(model.guidedRecordingScript.steps.enumerated()), id: \.offset) { index, step in
                                        Text("\(index + 1). \(step.title) — \(Int(step.holdSeconds))秒・\(step.label.title)"
                                             + (step.losslessCaptures > 0 ? "・lossless \(step.losslessCaptures)枚" : ""))
                                    }
                                }
                                .font(.caption2)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .font(.caption)
                            Text("ガイド付き: iPhoneを置いたまま、音声とカウントダウンの指示どおりに動いてください。区間ラベルの切替、振りの区間のlossless保存（最大\(DebugRecordingLimits.maximumGuidedLosslessCaptures)枚）、最後の停止と転送は自動です。手動の録画は従来どおりです。")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                            Picker("区間ラベル", selection: $model.debugSegmentLabel) {
                                ForEach(DebugSegmentLabel.allCases) { Text($0.title).tag($0) }
                            }
                            .pickerStyle(.segmented)
                            .disabled(!model.debugRecordingActive || model.debugRecordingFinalizing)
                            Text("区間ラベル（正解情報・metadataのみ）: saberあり=点灯saberが画面内 / saberなし=消灯・背景のみ / 赤い物隠し=背景の赤い物を覆って背景のみ")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                            Text("上限: 1録画\(Int(DebugRecordingLimits.maximumDurationSeconds / 60))分・\(DebugRecordingLimits.maximumDiskUsageBytes / 1_048_576) MiB / lossless手動 \(model.debugManualLosslessCaptureCount)/3枚 / forensic自動8枚")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                            Text(model.debugRecordingEnabled ? model.debugRecordingStatus : "OFF（録画処理なし）")
                                .font(.caption)
                                .foregroundStyle(model.debugRecordingActive ? .red : .secondary)
                            if let recording = model.lastDebugRecordingResult {
                                Text("\(recording.sessionID)  — raw / overlayは同じセッションです")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                                if let triageBundleURL = recording.triageBundleURL {
                                    ShareLink(item: triageBundleURL) {
                                        Label("Triage bundleを共有", systemImage: "shippingbox")
                                    }
                                } else if let triageErrorMessage = recording.triageErrorMessage {
                                    Text("Triage生成失敗: \(triageErrorMessage)")
                                        .font(.caption2).foregroundStyle(.orange)
                                }
                                HStack {
                                    Button { recordingPreviewURL = recording.rawVideoURL } label: {
                                        Label("Rawを見る", systemImage: "video")
                                    }
                                    Button { recordingPreviewURL = recording.overlayVideoURL } label: {
                                        Label("Overlayを見る", systemImage: "scribble.variable")
                                    }
                                    Button { recordingPreviewURL = recording.metadataURL } label: {
                                        Label("Metadataを見る", systemImage: "doc.text")
                                    }
                                    if let forensicDirectoryURL = recording.forensicDirectoryURL {
                                        ShareLink(item: forensicDirectoryURL) {
                                            Label("Forensicを共有", systemImage: "photo.stack")
                                        }
                                    }
                                }
                            }
                            HStack {
                                Text("保存中の録画session: \(model.debugRecordingSessions.count)件")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                                Spacer()
                                Button("古いsessionを整理…") {
                                    showRecordingCleanupConfirmation = true
                                }
                                .font(.caption)
                                .disabled(model.debugRecordingActive || model.debugRecordingFinalizing
                                          || model.debugRecordingSessions.count < 2)
                            }
                            if !model.debugRecordingCleanupStatus.isEmpty {
                                Text(model.debugRecordingCleanupStatus)
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    Text("状態: \(model.status)  FPS: \(model.fps, specifier: "%.1f")")
                    Text("送信先: \(model.activeDestination)")
                        .font(.footnote).foregroundStyle(.secondary)
                    Text("検出数: 赤 \(model.redDetectionCount) / 青 \(model.blueDetectionCount)")
                        .font(.footnote)
                    Text("認識: 赤 \(model.redEndpoints == nil ? "未検出" : "認識中")　青 \(model.blueEndpoints == nil ? "未検出" : "認識中")")
                        .font(.footnote)
                    Text("送信試行: 赤 \(model.redAttemptCount) / 青 \(model.blueAttemptCount)　ローカル完了: 赤 \(model.redCompletedCount) / 青 \(model.blueCompletedCount)")
                        .font(.footnote)
                    Text("送信エラー: 赤 \(model.redErrorCount) / 青 \(model.blueErrorCount)")
                        .font(.footnote)
                    Text("経路: \(model.pathStatus)\(pathDetail)")
                        .font(.footnote).foregroundStyle(.secondary)
                    Text("赤 5005: \(model.senderStates[5005] ?? "未接続")　青 5006: \(model.senderStates[5006] ?? "未接続")")
                        .font(.footnote)
                    if let error = model.senderErrors[5005] { Text("赤 5005: \(error)").foregroundStyle(.red).font(.caption) }
                    if let error = model.senderErrors[5006] { Text("青 5006: \(error)").foregroundStyle(.red).font(.caption) }
                    if let localSendMs = model.lastLocalSendMs {
                        Text("ローカル処理開始→UDP送信完了: \(localSendMs, specifier: "%.2f") ms")
                            .font(.footnote).foregroundStyle(.secondary)
                    } else {
                        Text("ローカル処理→UDP送信完了: 未計測")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                    if let error = model.errorMessage {
                        Text(error).foregroundStyle(.red).font(.footnote)
                    }
                    #if DEBUG
                    Toggle("Freeze Diagnostics", isOn: $model.freezeDiagnosticsEnabled)
                        .tint(.orange)
                    DisclosureGroup(isExpanded: $showDebugPerformance) {
                        Text("DEBUG PROFILING")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.orange)
                        Text("表示更新は最大5Hz。認識とUDP送信の頻度は下げません。")
                            .font(.caption).foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Camera Configuration").font(.subheadline.weight(.semibold))
                            Picker("Capture rate", selection: Binding(
                                get: { model.cameraFPS },
                                set: { model.selectCameraFPS($0) }
                            )) {
                                Text("30 FPS").tag(30)
                                Text("60 FPS").tag(60)
                            }
                            .pickerStyle(.segmented)
                            Toggle("fps自動比較（30秒ごとに30⇄60・保存しない。PCのF9遅延テスト用）", isOn: $model.debugAlternateFPS)
                            DebugInfoRow(label: "60 FPS formats", value: model.debug60FPSFormats)
                            DebugInfoRow(label: "Device", value: model.debugCameraConfiguration.device)
                            DebugInfoRow(label: "Position", value: model.debugCameraConfiguration.position)
                            DebugInfoRow(label: "Active format", value: model.debugCameraConfiguration.format)
                            DebugInfoRow(label: "FPS range", value: model.debugCameraConfiguration.fpsRanges)
                            DebugInfoRow(label: "Min duration", value: model.debugCameraConfiguration.minimumDuration)
                            DebugInfoRow(label: "Max duration", value: model.debugCameraConfiguration.maximumDuration)
                            DebugInfoRow(label: "Session preset", value: model.debugCameraConfiguration.sessionPreset)
                            DebugInfoRow(label: "Pixel format", value: model.debugCameraConfiguration.pixelFormat)
                            DebugInfoRow(label: "Discard late", value: model.debugCameraConfiguration.discardsLateFrames ? "On" : "Off")
                            DebugInfoRow(label: "Auto frame rate", value: model.debugCameraConfiguration.autoFrameRate)
                            DebugInfoRow(label: "Exposure", value: model.debugCameraConfiguration.exposure)
                            DebugInfoRow(label: "Exposure budget", value: model.debugCameraConfiguration.exposureBudget)
                            DebugInfoRow(label: "Video HDR", value: model.debugCameraConfiguration.hdr)
                            DebugInfoRow(label: "Low-light boost", value: model.debugCameraConfiguration.lowLightBoost)
                            DebugInfoRow(label: "System pressure", value: model.debugCameraConfiguration.systemPressure)
                            DebugInfoRow(label: "Thermal state", value: model.debugCameraConfiguration.thermalState)
                            DebugInfoRow(label: "Stabilization", value: model.debugCameraConfiguration.stabilization)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Camera Frame Timing").font(.subheadline.weight(.semibold))
                            if let timing = model.debugFrameIntervalStatistics {
                                DebugInfoRow(label: "Measured camera FPS", value: String(format: "%.1f fps", timing.measuredFPS))
                                DebugInfoRow(label: "Interval latest", value: String(format: "%.1f ms", timing.latestMs))
                                DebugInfoRow(label: "Interval median", value: String(format: "%.1f ms", timing.medianMs))
                                DebugInfoRow(label: "Interval min / max", value: String(format: "%.1f / %.1f ms", timing.minimumMs, timing.maximumMs))
                                DebugInfoRow(label: "Samples", value: "\(timing.sampleCount) / 120")
                            } else {
                                DebugInfoRow(label: "Measured camera FPS", value: "Not available")
                                DebugInfoRow(label: "Frame intervals", value: "Not available")
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        ForEach(["Camera", "Processing", "Network"], id: \.self) { category in
                            let rows = model.debugPerformanceRows.filter { $0.category == category }
                            VStack(alignment: .leading, spacing: 4) {
                                Text(category).font(.subheadline.weight(.semibold))
                                ForEach(rows) { row in
                                    DebugPerformanceRowView(row: row)
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    } label: {
                        Label("Debug Performance", systemImage: "speedometer")
                            .font(.headline)
                    }
                    .onAppear { model.debugDetailedProfilingEnabled = showDebugPerformance }
                    .onChange(of: showDebugPerformance) { enabled in
                        model.debugDetailedProfilingEnabled = enabled
                    }
                    #endif
                    DisclosureGroup("詳細設定") {
                        GroupBox("カメラ") {
                            Picker("カメラ fps", selection: Binding(
                                get: { model.cameraFPS },
                                set: { model.selectCameraFPS($0) }
                            )) {
                                Text("60 fps（低遅延・既定）").tag(60)
                                Text("30 fps").tag(30)
                            }
                            .pickerStyle(.segmented)
                            Text(model.supports60FPS
                                 ? "60 fpsは撮影から送信までが約13ms短い（2026-10-07実測）。発熱や処理落ちが続くときだけ30 fpsにします。"
                                 : "この端末のカメラは60 fpsに対応していないため、30 fpsで動きます。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        }
                        GroupBox("接続・出力") {
                            Toggle("遅延計測モード", isOn: $model.measurementMode)
                                .disabled(model.running)
                            Text("送信先Mac: \(model.host.isEmpty ? "自動発見待ち" : model.host)")
                                .font(.headline)
                            Text("赤: UDP 5005　青: UDP 5006")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Text(model.measurementMode ? "遅延計測中は座標の前にiPhoneの時計で ts=... を付けます。Mac受信時刻との差には時計差が含まれます。" : "通常モードは座標だけを送ります。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            Text(model.running ? "送信中は送信先を変更できません。停止して編集後、再開してください。" : "送信先の変更は次回の開始時に反映されます。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            HStack {
                                Text("出力")
                                TextField("幅", value: $model.outputWidth, format: .number)
                                TextField("高さ", value: $model.outputHeight, format: .number)
                            }
                        }
                        GroupBox("検出") {
                            Stepper("明るさ閾値: \(model.threshold)", value: $model.threshold, in: 80...255)
                            Stepper("色差閾値: \(model.dominance)", value: $model.dominance, in: 0...120)
                            Toggle("左右反転", isOn: $model.mirrorX)
                            Toggle("上下反転", isOn: $model.mirrorY)
                            Button("認識前フレームを1枚保存") { model.saveNextRawFrame() }
                                .disabled(!model.running)
                            if !model.rawFrameSaveMessage.isEmpty {
                                Text(model.rawFrameSaveMessage).font(.caption).foregroundStyle(.secondary)
                            }
                            if let url = model.lastRawFrameURL {
                                ShareLink(item: url) { Label("保存画像を共有", systemImage: "square.and.arrow.up") }
                            }
                        }
                        Button(model.measurementMode ? "遅延計測を開始" : "通常送信を開始") {
                            model.start()
                        }
                        .disabled(model.running)
                        .buttonStyle(.bordered)
                    }
                    Text("カメラ映像と検出座標を使用します。192.168.x.x のMacへはiPhoneも同じWi-Fiに接続してください。セルラー経路では通常届きません。Wi-Fi経路ありでも同一LAN・到達可能性は保証されません。")
                        .font(.caption).foregroundStyle(.secondary)
                }.padding()
            }
            .navigationTitle("Phone Saber Sender")
            .quickLookPreview($recordingPreviewURL)
        }
        .overlay {
            if model.guidedRecordingRunning {
                GuidedRecordingOverlay(status: model.guidedRecordingStatus,
                                       losslessCount: model.debugManualLosslessCaptureCount,
                                       cancel: { model.cancelGuidedRecording() })
            }
        }
        .task { model.refreshDebugRecordingSessions() }
        .onAppear { model.sceneDidChange(isActive: scenePhase == .active) }
        .confirmationDialog(
            "古い録画sessionを整理しますか？",
            isPresented: $showRecordingCleanupConfirmation,
            titleVisibility: .visible
        ) {
            Button("古いsessionを整理（最新は保持）", role: .destructive) {
                model.cleanupOlderDebugRecordingSessions()
            }
            Button("キャンセル", role: .cancel) {}
        } message: {
            let older = Array(model.debugRecordingSessions.dropFirst())
            let reclaimable = older.reduce(Int64(0)) { $0 + $1.diskUsageBytes }
            let firstOlder = older.last?.sessionID ?? "なし"
            let latest = model.debugRecordingSessions.first?.sessionID ?? "なし"
            Text("対象: \(older.count)件（最古 \(firstOlder)、約\(ByteCountFormatter.string(fromByteCount: reclaimable, countStyle: .file))）。最新 \(latest) は保持します。")
        }
        .onChange(of: scenePhase) { phase in
            model.sceneDidChange(isActive: phase == .active)
        }
    }

    private func draw(_ endpoints: (PixelPoint, PixelPoint)?, color: Color, context: inout GraphicsContext, size: CGSize) {
        guard let endpoints else { return }
        let path = Path { path in
            path.move(to: aspectFillPoint(endpoints.0, source: model.sourceDimensions, view: (size.width, size.height)))
            path.addLine(to: aspectFillPoint(endpoints.1, source: model.sourceDimensions, view: (size.width, size.height)))
        }
        context.stroke(path, with: .color(color), lineWidth: 5)
    }
}

/// Full-screen guide readable from about 3 m while the iPhone sits on a tripod.
private struct GuidedRecordingOverlay: View {
    let status: GuidedRecordingStatus?
    let losslessCount: Int
    let cancel: () -> Void

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(spacing: 12) {
                if let status {
                    let inHold: Bool = { if case .hold = status.phase { return true }; return false }()
                    Text("ステップ \(status.stepIndex + 1) / \(status.stepCount)")
                        .font(.system(size: 28, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.75))
                    Text(inHold ? status.title : "次: \(status.title)")
                        .font(.system(size: 46, weight: .bold))
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                        .minimumScaleFactor(0.4)
                        .lineLimit(3)
                    Text("\(status.secondsRemaining)")
                        .font(.system(size: 170, weight: .heavy, design: .rounded).monospacedDigit())
                        .foregroundStyle(inHold ? Color.green : Color.yellow)
                        .minimumScaleFactor(0.5)
                        .lineLimit(1)
                    Text(inHold ? "記録中・\(status.label.title)" : "準備")
                        .font(.system(size: 38, weight: .bold))
                        .foregroundStyle(inHold ? Color.green : Color.yellow)
                    Text("全体の残り 約\(status.totalSecondsRemaining)秒　lossless \(losslessCount)/\(DebugRecordingLimits.maximumGuidedLosslessCaptures)")
                        .font(.system(size: 20).monospacedDigit())
                        .foregroundStyle(.white.opacity(0.75))
                } else {
                    Text("ガイド付き録画を開始しています…")
                        .font(.system(size: 40, weight: .bold))
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                }
                Spacer(minLength: 0)
                Button(role: .destructive, action: cancel) {
                    Text("キャンセル（ここで録画を止める）")
                        .font(.title2.bold())
                        .frame(maxWidth: .infinity, minHeight: 56)
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
            }
            .padding()
        }
    }
}

#if DEBUG
private struct DebugInfoRow: View {
    let label: String
    let value: String

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label)
            Spacer()
            Text(value).multilineTextAlignment(.trailing)
        }
        .font(.caption.monospacedDigit())
    }
}

private struct DebugPerformanceRowView: View {
    let row: DebugPerformanceRow

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(row.label).font(.caption)
            Spacer()
            VStack(alignment: .trailing, spacing: 1) {
                Text("Latest  \(formatted(row.latest))")
                Text("Median  \(formatted(row.median))")
                    .foregroundStyle(.secondary)
                Text("Max     \(formatted(row.maximum))")
                    .foregroundStyle(.secondary)
            }
            .font(.caption.monospacedDigit())
        }
    }

    private func formatted(_ value: Double?) -> String {
        guard let value else { return "Not available" }
        return row.unit == "count"
            ? String(format: "%.0f", value)
            : String(format: "%.1f %@", value, row.unit)
    }
}
#endif
