import AVFoundation
import QuickLook
import SwiftUI

struct ContentView: View {
    @StateObject private var model = CameraViewModel()
    @ObservedObject private var diagnostics = DiagnosticsDestination.shared
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("phoneSaberAutoTransferDebugBundles") private var autoTransferDebugBundles = true
    @State private var recordingPreviewURL: URL?
    @State private var showRecordingCleanupConfirmation = false
    @State private var showIgnoreInspection = false
#if DEBUG
    @State private var showDebugPerformance = false
#endif

    var body: some View {
        // 低発熱表示の間は、操作画面と preview を表示から外して黒い画面だけにする。
        // scenePhase・onAppear などの modifier は外側に置き、切り替えで再実行させない。
        ZStack {
            if model.lowHeatDisplayActive {
                LowHeatScreen(line: model.lowHeatStatusLine, wake: { model.lowHeatTouch() })
            } else {
                mainScreen
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            Text(model.phoneIdentity.displayText)
                .font(.headline.monospacedDigit()).bold()
                .foregroundStyle(model.manualHostActive || model.station.isEmpty ? Color.yellow : Color.white)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10).background(Color.black)
                .accessibilityIdentifier("phoneIdentity")
        }
        .background(TouchActivityObserver { model.lowHeatTouch() })
        .statusBarHidden(model.lowHeatDisplayActive)
        .persistentSystemOverlays(model.lowHeatDisplayActive ? .hidden : .automatic)
        .task {
            model.refreshDebugRecordingSessions()
            diagnostics.startDiscovery()
        }
        // .inactive は前面のまま（システムの確認画面・通知センター・USB の「信頼しますか」など）。
        // 背面になるまでは自動ロックを止めたまま送り続ける。2026-10-29 台B: USB を挿した約20秒後に
        // 確認画面で非アクティブになり、自動ロックが戻って座標が止まった。
        .onAppear { model.sceneDidChange(isActive: scenePhase != .background) }
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
            model.sceneDidChange(isActive: phase != .background)
        }
        .onChange(of: recordingPreviewURL) { url in
            model.setLowHeatInhibitedByUI(url != nil || showIgnoreInspection)
        }
        .onChange(of: showIgnoreInspection) { shown in
            model.setLowHeatInhibitedByUI(shown || recordingPreviewURL != nil)
        }
        .sheet(isPresented: $showIgnoreInspection) {
            IgnoreZoneInspectionView(model: model)
        }
    }

    @ViewBuilder private var mainScreen: some View {
        let pathDetail = model.pathInterface.isEmpty ? "" : " (\(model.pathInterface))"
        NavigationStack {
            ScrollView {
                VStack(spacing: 12) {
                    TimelineView(.periodic(from: .now, by: 60)) { context in
                        let expiry = SigningExpiry.current
                        VStack(spacing: 2) {
                            Text(expiry.displayText(at: context.date))
                            if expiry.needsReinstall(at: context.date) {
                                Text("Mac で入れ直してください")
                            }
                        }
                        .font(.footnote.monospacedDigit())
                        .foregroundStyle(expiry.needsReinstall(at: context.date) ? Color.red : Color.secondary)
                    }
#if DEBUG
                    if model.debugAlternateFPS {
                        Text("fps 自動比較が ON です（F9 計測用）。通常の運用では OFF にしてください")
                            .font(.headline).foregroundStyle(.orange)
                    }
#endif
                    if model.running && !model.thermalFPSMessage.isEmpty {
                        Text(model.thermalFPSMessage).font(.footnote).foregroundStyle(.orange)
                    }
                    GroupBox("接続") {
                        Picker("台", selection: Binding(
                            get: { model.station }, set: { model.setStation($0) }
                        )) {
                            Text("指定なし").tag("")
                            Text("A").tag("A")
                            Text("B").tag("B")
                        }
                        .disabled(model.running)
                        if model.manualHostActive {
                            Text("手動 IP 指定中: 台の選択は使われません")
                                .font(.footnote).foregroundStyle(.orange)
                            if !model.sendingRequested {
                                Button("手動 IP を解除して自動探索に戻す") { model.clearManualHost() }
                            }
                        }
                        Button("Macを再検索") { model.retryDiscovery() }
                            .disabled(model.running)
                        Text(model.networkDiscoveryStatus)
                            .font(.caption).foregroundStyle(.secondary)
                        Text(model.networkStateLabel)
                            .font(.headline)
                        if model.pcReplyMissing {
                            Text(model.pcReplyMissingMessage)
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                        if !model.connectivityLogTail.isEmpty {
                            DisclosureGroup("通信ログ") {
                                VStack(alignment: .leading, spacing: 2) {
                                    ForEach(Array(model.connectivityLogTail.enumerated()), id: \.offset) { _, line in
                                        Text(line)
                                    }
                                }
                                .font(.caption2.monospaced())
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .font(.caption)
                        }
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
                        Text("LAN 宛先 Mac: \(model.discoveredMacName.isEmpty ? "未発見" : model.discoveredMacName)　IP: \(model.discoveredMacIP.isEmpty ? "-" : model.discoveredMacIP)")
                            .font(.footnote).foregroundStyle(.secondary)
                        if !model.otherDiscoveredPC.isEmpty {
                            Text("ほかに見つかった PC: \(model.otherDiscoveredPC)")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Text("Mode: \(model.connectionMode)　Red: 5005　Blue: 5006")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("経路: \(model.transportLabel)")
                            .font(.footnote.weight(.semibold))
                        Toggle("P2P予備（LANでUnityに届かないときだけ peer-to-peer Wi-Fi・Mac側 P2P bridge で送る）", isOn: Binding(
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
                        Text("診断データの送り先（Mac）")
                            .font(.footnote)
                        TextField("手動IP（空欄でMacを自動発見）", text: $diagnostics.manualHost)
                            .textFieldStyle(.roundedBorder)
                            .keyboardType(.URL)
                            .autocorrectionDisabled(true)
                            .textInputAutocapitalization(.never)
                        if let endpoint = diagnostics.selected(coordinateHost: model.host) {
                            Text("診断送信先: \(endpoint.url.host ?? ""):\(endpoint.url.port ?? 8765)（\(endpoint.source.rawValue)）")
                                .font(.caption).foregroundStyle(.secondary)
                        } else {
                            Text("診断送信先: 未設定（Macを探索中・手動IPも指定できます）")
                                .font(.caption).foregroundStyle(.secondary)
                        }
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
                    CameraDisplay(session: model.session, previewView: model.lowHeatDisplay.previewView,
                                  red: model.redEndpoints, blue: model.blueEndpoints,
                                  width: model.sourceDimensions.width, height: model.sourceDimensions.height,
                                  area: model.playArea)
                        .equatable()
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
                            .disabled(!model.cameraExposureExperimentEditable || model.shutter != 0)
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
                            Button { model.startSwingOnlyRecording() } label: {
                                Label("振りだけ録画を開始（約\(Int(model.swingOnlyRecordingScript.totalSeconds.rounded(.up)))秒・自動で終了）",
                                      systemImage: "figure.wave")
                            }
                            .buttonStyle(.bordered)
                            .disabled(!model.debugRecordingEnabled || !model.running ||
                                      model.debugRecordingActive || model.debugRecordingFinalizing)
                            Text("振りだけ: 押したら振る位置へ。消灯で5秒→点灯して振る→止める→速く振る、の順に短い音声が出ます。振りの画像は自動で残り、最後は自動で止まります。")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
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
                            if let entry = model.debugTransferEntry {
                                Text("\(entry.sessionID): \(entry.statusText)")
                                    .font(.caption)
                                    .foregroundStyle(entry.state == .received ? .green : .orange)
                            }
                            Text("未転送の録画: \(model.debugTransferPendingCount)件")
                                .font(.caption)
                            if model.debugTransferPendingCount > 0 {
                                if !autoTransferDebugBundles {
                                    Text("自動転送OFFの間は、自動再試行も停止します。")
                                        .font(.caption2).foregroundStyle(.secondary)
                                }
                                Button("未転送の録画を今すぐ送る") { model.sendPendingDebugRecordings() }
                                    .disabled(model.debugRecordingActive)
                            }
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
                                if model.supports120FPS { Text("120 FPS").tag(120) }
                            }
                            .pickerStyle(.segmented)
                            if model.supports120FPS {
                                Picker("F9 比較条件", selection: $model.debugComparisonHighFPS) {
                                    Text("30⇄60").tag(60)
                                    Text("60⇄120").tag(120)
                                }.pickerStyle(.segmented)
                            }
                            Toggle("fps自動比較（30秒ごと・保存しない。PCのF9遅延テスト用）", isOn: $model.debugAlternateFPS)
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
                            DebugInfoRow(label: "Active format", value: model.debugCameraConfiguration.format)
                            DebugInfoRow(label: "Pixel format", value: model.debugCameraConfiguration.pixelFormat)
                            DebugInfoRow(label: "Frame duration", value: model.debugCameraConfiguration.maximumDuration)
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
                                if model.supports120FPS { Text("120 fps（実験）").tag(120) }
                            }
                            .pickerStyle(.segmented)
                            Toggle("低遅延カメラ設定（試験）", isOn: Binding(
                                get: { model.lowLatencyCameraSettings },
                                set: { model.setLowLatencyCameraSettings($0) }
                            ))
                            .disabled(!model.cameraExposureExperimentEditable)
                            Text("既定OFF。HDR・歪み補正を無効化するため画像が変わる場合があります。アプリ終了でOFFに戻ります。")
                                .font(.caption).foregroundStyle(.secondary)
                            Toggle("剣を追いかける（通行人対策・試験）", isOn: Binding(
                                get: { model.trackLockEnabled },
                                set: { model.setTrackLockEnabled($0) }
                            ))
                            Toggle("赤が暗い時に自動で強める", isOn: Binding(
                                get: { model.redBoostEnabled },
                                set: { model.setRedBoostEnabled($0) }
                            ))
                            Text(model.redBoostStatus).font(.caption).foregroundStyle(.secondary)
                            Text("無視する赤・青の物: \(model.ignoreZoneSummary)")
                            Button("登録ゾーンを確認・個別解除") { showIgnoreInspection = true }
                            Text(model.ignoreLearning ? "学習中：剣を消して3秒待つ" : "剣を消して3秒待つ")
                            HStack {
                                Button("今の赤を無視") { model.ignoreCurrentDetection(.red) }
                                    .disabled(!model.running || model.ignoreLearning)
                                Button("今の青を無視") { model.ignoreCurrentDetection(.blue) }
                                    .disabled(!model.running || model.ignoreLearning)
                                Button("無視をすべて消す", role: .destructive) { model.clearIgnoreZones() }
                            }
                            .buttonStyle(.bordered)
                            Text("会場のコーンや蛍光灯などを3秒間覚えます。剣の電源を切るかカメラの外に出してから押してください。学習中に映った剣も無視されます。カメラや物を動かしたら押し直してください。")
                                .font(.caption).foregroundStyle(.secondary)
                            Text("プレイ範囲: \(model.playAreaSummary)")
                            HStack {
                                Button("プレイ範囲を覚える（\(CameraViewModel.playAreaLearnSeconds)秒振る）") { model.learnPlayArea() }
                                    .disabled(!model.running || model.playAreaLearning)
                                Button("プレイ範囲を消す", role: .destructive) { model.clearPlayArea() }
                                    .disabled(model.playArea == nil && !model.playAreaLearning)
                            }
                            .buttonStyle(.bordered)
                            Text("赤い服の人など、動く赤・青の物の対策。送信中に押し、\(CameraViewModel.playAreaLearnSeconds)秒間、遊ぶ範囲いっぱいに剣（赤・青）を振ります。以後、中心がその範囲（緑の枠）の外にある候補は選びません。カメラを動かしたら覚え直してください。Mac の F8 からも操作できます。")
                                .font(.caption).foregroundStyle(.secondary)
                            Text(model.shutterStatus)
                            Picker("固定シャッター（iPhone）", selection: Binding(
                                get: { model.shutter }, set: { model.setShutter($0) }
                            )) {
                                ForEach(CameraViewModel.shutterChoices, id: \.self) { value in
                                    Text(CameraViewModel.shutterLabel(value)).tag(value)
                                }
                            }
                            Picker("固定シャッターの明るさ", selection: Binding(
                                get: { model.shutterISO }, set: { model.setShutterISO($0) }
                            )) {
                                ForEach(CameraViewModel.shutterISOChoices, id: \.self) { value in
                                    Text(CameraViewModel.shutterISOLabel(value)).tag(value)
                                }
                            }
                            Toggle("低ISOの色付き発光体（試験）", isOn: Binding(
                                get: { model.shutterEmitter }, set: { model.setShutterEmitter($0) }
                            ))
                            Toggle("認識状況でISOを自動調整（低ISO発光体のとき）", isOn: Binding(
                                get: { model.autoExposureEnabled }, set: { model.setAutoExposureEnabled($0) }
                            ))
                            Text("1/2000秒でISOを半分にするごとに−1段。端末の範囲に制限されるため実測値も確認。既定ISO 250、色付き発光体は既定ON、固定シャッターかつISO2000未満の時だけ有効。自動調整ONでは選択ISOから100〜400の範囲で調整します（背景が暗い台で刃が弱い時だけ640まで）。固定シャッターOFFで自動露出＋補正に戻ります。")
                                .font(.caption)
                            Text(model.exposureBiasStatus)
                            Picker("露出補正", selection: Binding(
                                get: { model.exposureBias },
                                set: { model.setExposureBias($0) }
                            )) {
                                ForEach(CameraViewModel.exposureBiasChoices, id: \.self) { ev in
                                    Text(ev == 0 ? "露出 0（OFF）" : "露出 −\(-ev)").tag(ev)
                                }
                            }
                            .pickerStyle(.segmented)
                            Text("既定 −2 EV。固定シャッターOFF時に使う自動露出の補正です。固定シャッター中の変更は保存し、OFFに戻したとき適用します。")
                                .font(.caption).foregroundStyle(.secondary)
                            Toggle("低発熱表示（送信中に\(Int(LowHeatDisplayPolicy.idleSeconds))秒触れなければ画面を暗くする）", isOn: Binding(
                                get: { model.lowHeatDisplayEnabled },
                                set: { model.setLowHeatDisplayEnabled($0) }
                            ))
                            Text("既定ON。カメラ映像の表示を止めて画面を暗くし、発熱を抑えて60 fpsを保ちやすくします。認識と送信は変わりません。画面に触れると元に戻ります。")
                                .font(.caption).foregroundStyle(.secondary)
                            Text(model.supports60FPS
                                 ? "60 fpsは撮影から送信までが約13ms短い（2026-10-07実測）。発熱や処理落ちが続くときだけ30 fpsにします。"
                                 : "この端末のカメラは60 fpsに対応していないため、30 fpsで動きます。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        }
                        GroupBox("自動クリップ") {
                            Toggle("問題らしい瞬間だけ前後5秒を保存してMacへ送る", isOn: Binding(
                                get: { model.autoClipEnabled },
                                set: { model.setAutoClipEnabled($0) }
                            ))
                            Toggle("原寸・全frame記録（実験用）", isOn: Binding(
                                get: { model.fullResolutionEnabled },
                                set: { model.setFullResolutionEnabled($0) }
                            ))
                            Text(model.fullResolutionStatusLine).font(.caption)
                            Text("既定OFF。問題の前2秒〜後2秒を原寸BGRA・全候補で記録。480×640 / 60fpsで約296 MB/件、最大4件/時。保存合計1 GiBで古い順に削除。熱・電池20%以下・空き2 GiB未満で停止。処理・コピーが追いつかないframeは欠番として記録します。転送は既存AutoClipと同じです。")
                                .font(.caption).foregroundStyle(.secondary)
                            Text(model.autoClipStatusLine)
                                .font(.caption)
                            Text("既定ON。送信中、同じ色の候補が2つ以上残る（コーン・服・反射との取り違え）か、剣の位置が1コマで大きく飛んだときだけ、前3秒・後2秒の画像と認識データを保存します（20秒に1回まで、1回の送信で6件まで）。転送は座標を送っていない間（曲の合間・停止後）に行います。発熱中・fps を下げている間・Debug Recording 中は止まります。認識と送信は変わりません。PC（Unity）からの指示でも ON/OFF・今すぐ保存ができます。")
                                .font(.caption).foregroundStyle(.secondary)
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
    }
}

// カウンタ・接続状態の更新では preview の再設定や同じ overlay の描画を繰り返さない。
private struct CameraDisplay: View, Equatable {
    let session: AVCaptureSession
    let previewView: PreviewView
    let red: (PixelPoint, PixelPoint)?
    let blue: (PixelPoint, PixelPoint)?
    let width: Int
    let height: Int
    /// プレイ範囲（緑の枠）。nil なら描かない。
    var area: SaberPlayArea? = nil

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.session === rhs.session && lhs.previewView === rhs.previewView && lhs.width == rhs.width && lhs.height == rhs.height
            && lhs.area == rhs.area
            && lhs.red?.0 == rhs.red?.0 && lhs.red?.1 == rhs.red?.1
            && lhs.blue?.0 == rhs.blue?.0 && lhs.blue?.1 == rhs.blue?.1
    }

    var body: some View {
        ZStack {
            CameraPreview(session: session, view: previewView).frame(height: 280)
            Canvas { context, size in
                drawArea(context: &context, size: size)
                draw(red, color: .red, context: &context, size: size)
                draw(blue, color: .blue, context: &context, size: size)
            }.frame(height: 280)
        }.clipped()
    }

    private func drawArea(context: inout GraphicsContext, size: CGSize) {
        guard let area, width > 0, height > 0 else { return }
        func point(_ x: Double, _ y: Double) -> CGPoint {
            aspectFillPoint(PixelPoint(x: Int((min(max(x, 0), 1) * Double(width)).rounded()),
                                       y: Int((min(max(y, 0), 1) * Double(height)).rounded())),
                            source: (width, height), view: (size.width, size.height))
        }
        let topLeft = point(area.minX, area.minY), bottomRight = point(area.maxX, area.maxY)
        let rect = CGRect(x: topLeft.x, y: topLeft.y, width: bottomRight.x - topLeft.x, height: bottomRight.y - topLeft.y)
        context.stroke(Path(rect), with: .color(.green), lineWidth: 3)
    }

    private func draw(_ endpoints: (PixelPoint, PixelPoint)?, color: Color, context: inout GraphicsContext, size: CGSize) {
        guard let endpoints else { return }
        let path = Path { path in
            path.move(to: aspectFillPoint(endpoints.0, source: (width, height), view: (size.width, size.height)))
            path.addLine(to: aspectFillPoint(endpoints.1, source: (width, height), view: (size.width, size.height)))
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

/// カメラ画像は保持せず、検出フレームの正規化座標だけを図示する。
private struct IgnoreZoneInspectionView: View {
    @ObservedObject var model: CameraViewModel
    @Environment(\.dismiss) private var dismiss
    @State private var session: UUID?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text("カメラを動かした後は、色・番号・位置を確認してください。選んだ1件だけ解除できます。学習中の解除は学習も中止します。")
                    Text("検出フレームを縦向き・左右反転なしで表示。左上が(0,0)、右がX、下がY、各軸0〜100%。送信用の反転・プレイ範囲への拡大は適用しません。")
                        .font(.caption)
                    coordinateDiagram
                        .aspectRatio(1, contentMode: .fit)
                        .accessibilityLabel("登録ゾーンと無視設定で除外中の候補の正規化フレーム座標図")
                    Text("破線の枠＝登録ゾーン、太線と丸＝無視設定で除外中の候補。枠内でも登録形状に一致しなければ除外されません。実刃かどうかは自動判定しません。")
                        .font(.caption)
                    if let snapshot = model.ignoreInspectionSnapshot {
                        Text("最新処理フレーム \(snapshot.sequence)（\(snapshot.width)×\(snapshot.height)）")
                            .font(.caption.monospacedDigit())
                        if snapshot.matches.isEmpty {
                            Text("現在、表示対象の除外候補はありません。")
                        }
                        ForEach(snapshot.matches.indices, id: \.self) { index in
                            let match = snapshot.matches[index]
                            let zones = match.zoneIndices.map { "\(match.color == .red ? "赤" : "青")\($0 + 1)" }.joined(separator: "・")
                            Text("\(match.label)：無視設定で除外中 → \(zones)\n端点(px)：(\(match.endpoints.0.x), \(match.endpoints.0.y)) → (\(match.endpoints.1.x), \(match.endpoints.1.y))")
                                .font(.caption.monospacedDigit())
                        }
                    } else {
                        Text("最新の処理フレームを待っています。送信停止中は候補を表示しません。")
                    }
                    Text("プレイ範囲内のeligible候補だけを表示します。追跡など他の条件による未選択は表示対象外です。複数の登録に一致する場合は、すべての番号を表示します。")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("登録：\(model.ignoreZoneSummary)").font(.headline)
                    ForEach(model.ignoreZoneEntries) { entry in
                        HStack(alignment: .top) {
                            VStack(alignment: .leading) {
                                Text(entry.label).bold().foregroundStyle(tint(entry.id.color))
                                Text(String(format: "X %.1f〜%.1f%% / Y %.1f〜%.1f%%",
                                            entry.zone.minX * 100, entry.zone.maxX * 100,
                                            entry.zone.minY * 100, entry.zone.maxY * 100))
                                    .font(.caption.monospacedDigit())
                            }
                            Spacer()
                            Button("\(entry.label)を解除", role: .destructive) {
                                if let session { model.removeIgnoreZone(entry, session: session) }
                            }
                            .buttonStyle(.bordered)
                        }
                    }
                }
                .padding()
            }
            .navigationTitle("無視する赤・青の物")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { Button("閉じる") { dismiss() } }
        }
        .onAppear { session = model.beginIgnoreInspection() }
        .onDisappear {
            if let session { model.endIgnoreInspection(session) }
            session = nil
        }
    }

    private func tint(_ color: SaberColor) -> Color { color == .red ? .red : .blue }

    private var coordinateDiagram: some View {
        Canvas { context, size in
            let bounds = CGRect(origin: .zero, size: size)
            context.fill(Path(bounds), with: .color(.black.opacity(0.06)))
            context.stroke(Path(bounds), with: .color(.secondary))
            for entry in model.ignoreZoneEntries {
                let z = entry.zone
                let rect = CGRect(x: z.minX * size.width, y: z.minY * size.height,
                                  width: (z.maxX - z.minX) * size.width, height: (z.maxY - z.minY) * size.height)
                context.stroke(Path(rect), with: .color(tint(entry.id.color)), style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                context.draw(Text(entry.label).font(.caption).foregroundColor(tint(entry.id.color)),
                             at: CGPoint(x: max(14, min(size.width - 14, rect.midX)),
                                         y: max(10, min(size.height - 10, rect.minY))), anchor: .center)
            }
            if let snapshot = model.ignoreInspectionSnapshot {
                for match in snapshot.matches {
                    let a = snapshot.diagramPoint(match.endpoints.0)
                    let b = snapshot.diagramPoint(match.endpoints.1)
                    let p = CGPoint(x: a.x * size.width, y: a.y * size.height)
                    let q = CGPoint(x: b.x * size.width, y: b.y * size.height)
                    var line = Path(); line.move(to: p); line.addLine(to: q)
                    context.stroke(line, with: .color(tint(match.color)), lineWidth: 3)
                    for point in [p, q] {
                        context.fill(Path(ellipseIn: CGRect(x: point.x - 3, y: point.y - 3, width: 6, height: 6)),
                                     with: .color(tint(match.color)))
                    }
                    context.draw(Text(match.label).font(.caption2).foregroundColor(tint(match.color)),
                                 at: CGPoint(x: (p.x + q.x) / 2, y: (p.y + q.y) / 2), anchor: .bottom)
                }
            }
        }
    }
}
