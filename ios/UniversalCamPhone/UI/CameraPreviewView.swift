import SwiftUI
import AVFoundation

/// UIViewRepresentable that renders the live camera feed via AVCaptureVideoPreviewLayer.
/// When `blurred` is true, a gaussian blur overlay is applied.
struct CameraPreviewView: UIViewRepresentable {

    let session: AVCaptureSession
    var blurred: Bool = true

    func makeUIView(context: Context) -> PreviewUIView {
        let view = PreviewUIView()
        view.videoPreviewLayer.session      = session
        view.videoPreviewLayer.videoGravity = .resizeAspectFill
        view.setBlurred(blurred)
        return view
    }

    func updateUIView(_ uiView: PreviewUIView, context: Context) {
        uiView.setBlurred(blurred)
    }
}

// MARK: - PreviewUIView

final class PreviewUIView: UIView {

    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }

    var videoPreviewLayer: AVCaptureVideoPreviewLayer {
        layer as! AVCaptureVideoPreviewLayer
    }

    private var blurView: UIVisualEffectView?

    func setBlurred(_ blurred: Bool) {
        if blurred {
            if blurView == nil {
                let blur = UIBlurEffect(style: .regular)
                let effectView = UIVisualEffectView(effect: blur)
                effectView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
                effectView.frame = bounds
                addSubview(effectView)
                blurView = effectView
            }
        } else {
            blurView?.removeFromSuperview()
            blurView = nil
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        videoPreviewLayer.frame = bounds
        blurView?.frame = bounds
    }
}
