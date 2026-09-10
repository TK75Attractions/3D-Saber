import SwiftUI

struct ContentView: View {
    @StateObject private var model = CameraViewModel()

    var body: some View {
        let pathDetail = model.pathInterface.isEmpty ? "" : " (\(model.pathInterface))"
        NavigationStack {
            ScrollView {
                VStack(spacing: 12) {
                    ZStack {
                        CameraPreview(session: model.session).frame(height: 280)
                        GeometryReader { proxy in
                            Canvas { context, size in
                                draw(model.redEndpoints, color: .red, context: &context, size: size)
                                draw(model.blueEndpoints, color: .blue, context: &context, size: size)
                            }
                        }.frame(height: 280)
                    }.clipped()
                    Text("状態: \(model.status)  FPS: \(model.fps, specifier: "%.1f")")
                    Text("送信先: \(model.activeDestination)")
                        .font(.footnote).foregroundStyle(.secondary)
                    Text("検出数: 赤 \(model.redDetectionCount) / 青 \(model.blueDetectionCount)")
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
                    GroupBox("接続") {
                        Text("送信先Mac")
                            .font(.headline)
                        TextField("例: 192.168.1.10", text: $model.host)
                            .textFieldStyle(.roundedBorder)
                            .keyboardType(.URL)
                            .autocorrectionDisabled(true)
                            .textInputAutocapitalization(.never)
                            .disabled(model.running)
                        Text("赤: UDP 5005　青: UDP 5006")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Toggle("遅延計測モード", isOn: $model.measurementMode)
                        Text(model.measurementMode ? "座標の前に iPhone の時計で ts=... を付けます。Macの表示時刻と同じ時刻基準で比較してください。" : "通常モードは座標だけを送ります。")
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
                    }
                    Button(model.running ? "停止" : "開始") {
                        model.running ? model.stop() : model.start()
                    }.buttonStyle(.borderedProminent)
                    Text("カメラ映像と検出座標を使用します。192.168.x.x のMacへはiPhoneも同じWi-Fiに接続してください。セルラー経路では通常届きません。Wi-Fi経路ありでも同一LAN・到達可能性は保証されません。")
                        .font(.caption).foregroundStyle(.secondary)
                }.padding()
            }.navigationTitle("Phone Saber Sender")
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
