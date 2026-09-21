import QuickLook
import SwiftUI

struct ContentView: View {
    @StateObject private var model = CameraViewModel()
    @State private var recordingPreviewURL: URL?
#if DEBUG
    @State private var showDebugPerformance = false
#endif

    var body: some View {
        let pathDetail = model.pathInterface.isEmpty ? "" : " (\(model.pathInterface))"
        NavigationStack {
            ScrollView {
                VStack(spacing: 12) {
                    GroupBox("接続") {
                        Button("Macを再検索") { model.retryDiscovery() }
                            .disabled(model.running)
                        Text("Network: \(model.running ? "CONNECTED" : model.networkDiscoveryStatus)")
                            .font(.headline)
                        Text("Mac: \(model.discoveredMacName.isEmpty ? "未発見" : model.discoveredMacName)　IP: \(model.discoveredMacIP.isEmpty ? "-" : model.discoveredMacIP)")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("Mode: \(model.connectionMode)　Red: 5005　Blue: 5006")
                            .font(.footnote).foregroundStyle(.secondary)
                        TextField("手動IP（自動発見できない場合のみ）", text: $model.host)
                            .textFieldStyle(.roundedBorder)
                            .keyboardType(.URL)
                            .autocorrectionDisabled(true)
                            .textInputAutocapitalization(.never)
                            .disabled(model.running)
                        Button(model.running ? "停止" : "通常送信を開始") {
                            model.running ? model.stop() : model.start()
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
                    GroupBox("Debug Recording") {
                        VStack(alignment: .leading, spacing: 8) {
                            Toggle("Debug Recording: \(model.debugRecordingEnabled ? "ON" : "OFF")",
                                   isOn: $model.debugRecordingEnabled)
                                .disabled(model.debugRecordingActive || model.debugRecordingFinalizing)
                            HStack {
                                Button("Start Recording") { model.startDebugRecording() }
                                    .disabled(!model.debugRecordingEnabled || !model.running ||
                                              model.debugRecordingActive || model.debugRecordingFinalizing)
                                Button("Stop Recording") { model.stopDebugRecording() }
                                    .disabled(!model.debugRecordingActive || model.debugRecordingFinalizing)
                            }
                            Text(model.debugRecordingEnabled ? model.debugRecordingStatus : "OFF（録画処理なし）")
                                .font(.caption)
                                .foregroundStyle(model.debugRecordingActive ? .red : .secondary)
                            if let recording = model.lastDebugRecordingResult {
                                Text("\(recording.sessionID)  — raw / overlayは同じセッションです")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
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
                    DisclosureGroup(isExpanded: $showDebugPerformance) {
                        Text("DEBUG PROFILING")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.orange)
                        Text("表示更新は最大5Hz。認識とUDP送信の頻度は下げません。")
                            .font(.caption).foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Camera Configuration").font(.subheadline.weight(.semibold))
                            Picker("Capture rate", selection: Binding(
                                get: { model.debugRequestedFPS },
                                set: { model.selectDebugCameraFPS($0) }
                            )) {
                                Text("30 FPS").tag(30)
                                Text("60 FPS").tag(60)
                            }
                            .pickerStyle(.segmented)
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
                        GroupBox("接続・出力") {
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
                        Button("通常送信を開始") {
                            model.measurementMode = false
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
