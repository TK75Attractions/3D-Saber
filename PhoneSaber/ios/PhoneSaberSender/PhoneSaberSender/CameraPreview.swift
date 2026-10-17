import AVFoundation
import SwiftUI

struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    /// 表示を離れても使い続ける view（LowHeatDisplayController が持つ）。
    /// 作り直すと running 中の session に preview layer を足し引きすることになるため。
    let view: PreviewView

    func makeUIView(context: Context) -> PreviewView {
        if view.videoPreviewLayer.session !== session {
            view.videoPreviewLayer.session = session
        }
        view.videoPreviewLayer.videoGravity = .resizeAspectFill
        return view
    }

    func updateUIView(_ uiView: PreviewView, context: Context) {
        // endpoint の描画更新で、同じ capture session を再接続しない。
        if uiView.videoPreviewLayer.session !== session {
            uiView.videoPreviewLayer.session = session
        }
    }
}

final class PreviewView: UIView {
    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
    var videoPreviewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
}
