import AVFoundation
import Combine

/// Describes a camera available on this device.
struct CameraInfo: Codable, Identifiable, Equatable {
    let id: String            // AVCaptureDevice.uniqueID
    let name: String          // Localised device name
    let position: String      // "front", "back", or "unspecified"
    let type: String          // e.g. "wide_angle"
    let zoomFactor: Double    // Optical zoom relative to 1× wide angle (0.5, 1.0, 3.0, …)

    enum CodingKeys: String, CodingKey {
        case id, name, position, type
        case zoomFactor = "zoom_factor"
    }
}

/// Wraps AVCaptureSession and exposes camera state as @Published properties.
/// Owned by the app-level @StateObject; injected into views via @EnvironmentObject.
final class CameraSession: NSObject, ObservableObject {

    // MARK: - Public State

    @Published var isRunning: Bool = false
    @Published var currentCameraID: String = ""
    @Published var availableCameras: [CameraInfo] = []

    /// The underlying capture session — used by CameraPreviewView to attach the preview layer.
    let captureSession = AVCaptureSession()

    // MARK: - Private

    private let sessionQueue = DispatchQueue(label: "com.universalcam.camera.session")
    private var videoOutput  = AVCaptureVideoDataOutput()

    /// Called with each encoded video sample. Set by ConnectionManager.
    var onVideoSampleBuffer: ((CMSampleBuffer) -> Void)?

    // MARK: - Camera Discovery

    /// Discovers all available video cameras on this device.
    func discoverCameras() {
        // Only enumerate individual physical lenses — exclude virtual compound devices
        // (builtInDualCamera, builtInDualWideCamera, builtInTripleCamera) which would
        // produce duplicate "Wide 1×" entries alongside the real single-lens cameras.
        let deviceTypes: [AVCaptureDevice.DeviceType] = [
            .builtInWideAngleCamera,
            .builtInUltraWideCamera,
            .builtInTelephotoCamera,
            .builtInTrueDepthCamera
        ]

        let discovery = AVCaptureDevice.DiscoverySession(
            deviceTypes: deviceTypes,
            mediaType: .video,
            position: .unspecified
        )

        let allCameras = discovery.devices.map { device in
            CameraInfo(
                id: device.uniqueID,
                name: device.localizedName,
                position: positionString(device.position),
                type: deviceTypeString(device.deviceType),
                zoomFactor: opticalZoomFactor(for: device)
            )
        }

        // On devices with a TrueDepth camera, the front wide-angle camera is the same
        // physical sensor exposed under two device types — deduplicate by preferring true_depth.
        let hasFrontTrueDepth = allCameras.contains { $0.position == "front" && $0.type == "true_depth" }
        let cameras = allCameras.filter { cam in
            !(hasFrontTrueDepth && cam.position == "front" && cam.type == "wide_angle")
        }

        DispatchQueue.main.async {
            self.availableCameras = cameras
            print("[CameraSession] Discovered \(cameras.count) cameras:")
            for cam in cameras {
                print("  - \(cam.name) (\(cam.type), \(cam.position)) id=\(cam.id)")
            }
        }
    }

    // MARK: - Lifecycle

    func start() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.discoverCameras()
            self.configureCaptureSession()
            self.captureSession.startRunning()
            DispatchQueue.main.async { self.isRunning = true }
        }
    }

    func stop() {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.captureSession.stopRunning()
            DispatchQueue.main.async { self.isRunning = false }
        }
    }

    /// Switch to a specific camera by its unique ID.
    /// Calls the completion handler on the main queue with the CameraInfo on success, or nil on failure.
    func switchToCamera(withID uniqueID: String, completion: ((CameraInfo?) -> Void)? = nil) {
        sessionQueue.async { [weak self] in
            guard let self else {
                DispatchQueue.main.async { completion?(nil) }
                return
            }
            guard let device = AVCaptureDevice(uniqueID: uniqueID) else {
                print("[CameraSession] No device found with ID: \(uniqueID)")
                DispatchQueue.main.async { completion?(nil) }
                return
            }
            self.reconfigureInput(with: device)
            let info = CameraInfo(
                id: device.uniqueID,
                name: device.localizedName,
                position: self.positionString(device.position),
                type: self.deviceTypeString(device.deviceType),
                zoomFactor: self.opticalZoomFactor(for: device)
            )
            DispatchQueue.main.async {
                self.currentCameraID = device.uniqueID
                completion?(info)
            }
        }
    }

    /// Legacy toggle between front and back (for backward compatibility).
    func switchCamera(completion: ((CameraInfo?) -> Void)? = nil) {
        sessionQueue.async { [weak self] in
            guard let self else {
                DispatchQueue.main.async { completion?(nil) }
                return
            }
            let currentDevice = self.currentVideoDevice()
            let currentPos = currentDevice?.position ?? .front
            let newPosition: AVCaptureDevice.Position = currentPos == .front ? .back : .front

            guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: newPosition) else {
                DispatchQueue.main.async { completion?(nil) }
                return
            }
            self.reconfigureInput(with: device)
            let info = CameraInfo(
                id: device.uniqueID,
                name: device.localizedName,
                position: self.positionString(device.position),
                type: self.deviceTypeString(device.deviceType),
                zoomFactor: self.opticalZoomFactor(for: device)
            )
            DispatchQueue.main.async {
                self.currentCameraID = device.uniqueID
                completion?(info)
            }
        }
    }

    func setResolution(_ preset: AVCaptureSession.Preset) {
        sessionQueue.async { [weak self] in
            guard let self else { return }
            self.captureSession.beginConfiguration()
            if self.captureSession.canSetSessionPreset(preset) {
                self.captureSession.sessionPreset = preset
            }
            self.captureSession.commitConfiguration()
            self.applyLandscapeRotation()   // re-lock after preset change
        }
    }

    func setResolutionAndFPS(preset: AVCaptureSession.Preset, fps: Int32) {
        sessionQueue.async { [weak self] in
            guard let self else { return }

            // Apply session preset for the target resolution
            self.captureSession.beginConfiguration()
            if self.captureSession.canSetSessionPreset(preset) {
                self.captureSession.sessionPreset = preset
            }
            self.captureSession.commitConfiguration()
            self.applyLandscapeRotation()

            guard let device = self.currentVideoDevice() else { return }
            let targetFPS = Double(fps)

            // If the current active format doesn't support the requested fps (common for
            // 4K 60fps where the preset defaults to a 30fps format), find one that does.
            let currentSupports = device.activeFormat.videoSupportedFrameRateRanges
                .contains { $0.maxFrameRate >= targetFPS }

            if !currentSupports {
                let currentDims = CMVideoFormatDescriptionGetDimensions(
                    device.activeFormat.formatDescription)
                if let format = device.formats.first(where: { fmt in
                    let d = CMVideoFormatDescriptionGetDimensions(fmt.formatDescription)
                    return d.width >= currentDims.width
                        && d.height >= currentDims.height
                        && fmt.videoSupportedFrameRateRanges.contains { $0.maxFrameRate >= targetFPS }
                }) {
                    do {
                        try device.lockForConfiguration()
                        device.activeFormat = format
                        device.unlockForConfiguration()
                    } catch {
                        print("[CameraSession] Failed to set high-fps format: \(error)")
                    }
                } else {
                    print("[CameraSession] No format found for \(fps) fps at current resolution — using max available")
                }
            }

            // Set the frame duration (clamped silently by the driver if out of range)
            let duration = CMTimeMake(value: 1, timescale: fps)
            do {
                try device.lockForConfiguration()
                device.activeVideoMinFrameDuration = duration
                device.activeVideoMaxFrameDuration = duration
                device.unlockForConfiguration()
            } catch {
                print("[CameraSession] Failed to set fps \(fps): \(error)")
            }
        }
    }

    // MARK: - Private Configuration

    private func configureCaptureSession() {
        captureSession.beginConfiguration()
        captureSession.sessionPreset = .hd1920x1080

        // Start with the default front wide-angle camera
        if let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front) {
            addVideoInput(device: device)
            DispatchQueue.main.async { self.currentCameraID = device.uniqueID }
        } else if let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back) {
            addVideoInput(device: device)
            DispatchQueue.main.async { self.currentCameraID = device.uniqueID }
        }

        addVideoOutput()

        captureSession.commitConfiguration()
        applyLandscapeRotation()   // must be after commit — commit resets connection rotation
    }

    private func addVideoInput(device: AVCaptureDevice) {
        guard let input = try? AVCaptureDeviceInput(device: device),
              captureSession.canAddInput(input) else { return }
        captureSession.addInput(input)
    }

    private func addVideoOutput() {
        videoOutput.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        videoOutput.setSampleBufferDelegate(self, queue: sessionQueue)
        videoOutput.alwaysDiscardsLateVideoFrames = true
        guard captureSession.canAddOutput(videoOutput) else { return }
        captureSession.addOutput(videoOutput)
        // NOTE: do NOT call applyLandscapeRotation() here — commitConfiguration() resets
        // the connection's rotation angle, so rotation must be applied after commit (see below).
    }

    private func reconfigureInput(with device: AVCaptureDevice) {
        captureSession.beginConfiguration()
        // Remove existing video inputs only
        captureSession.inputs
            .compactMap { $0 as? AVCaptureDeviceInput }
            .filter { $0.device.hasMediaType(.video) }
            .forEach { captureSession.removeInput($0) }
        addVideoInput(device: device)
        captureSession.commitConfiguration()
        applyLandscapeRotation()   // re-lock after input switch
    }

    /// Locks the video output connection to landscape-right (sensor native = 0°).
    /// Call after any session configuration that may reset connection properties.
    private func applyLandscapeRotation() {
        guard let connection = videoOutput.connection(with: .video) else { return }
        if #available(iOS 17.0, *) {
            if connection.isVideoRotationAngleSupported(0) {
                connection.videoRotationAngle = 0
            }
        } else {
            connection.videoOrientation = .landscapeRight
        }
    }

    private func currentVideoDevice() -> AVCaptureDevice? {
        return captureSession.inputs
            .compactMap { $0 as? AVCaptureDeviceInput }
            .first { $0.device.hasMediaType(.video) }?
            .device
    }

    // MARK: - Helpers

    /// Returns the optical zoom factor relative to 1× wide angle for a given device.
    /// Uses virtualDeviceSwitchOverVideoZoomFactors on the enclosing virtual device.
    /// Formula: lastSwitchFactor × minAvailableVideoZoomFactor = real optical zoom.
    private func opticalZoomFactor(for device: AVCaptureDevice) -> Double {
        switch device.deviceType {
        case .builtInUltraWideCamera: return 0.5
        case .builtInWideAngleCamera: return 1.0
        case .builtInTrueDepthCamera: return 1.0
        case .builtInTelephotoCamera:
            for virtualType: AVCaptureDevice.DeviceType in [.builtInTripleCamera, .builtInDualCamera] {
                guard let vd = AVCaptureDevice.default(virtualType, for: .video, position: device.position),
                      vd.constituentDevices.contains(device),
                      let lastFactor = vd.virtualDeviceSwitchOverVideoZoomFactors.last
                else { continue }
                return Double(truncating: lastFactor) * Double(vd.minAvailableVideoZoomFactor)
            }
            return 2.0  // reasonable fallback
        default: return 1.0
        }
    }

    private func positionString(_ position: AVCaptureDevice.Position) -> String {
        switch position {
        case .front: return "front"
        case .back:  return "back"
        default:     return "unspecified"
        }
    }

    private func deviceTypeString(_ deviceType: AVCaptureDevice.DeviceType) -> String {
        if deviceType == .builtInWideAngleCamera     { return "wide_angle" }
        if deviceType == .builtInUltraWideCamera     { return "ultra_wide" }
        if deviceType == .builtInTelephotoCamera      { return "telephoto" }
        if deviceType == .builtInDualCamera           { return "dual" }
        if deviceType == .builtInDualWideCamera       { return "dual_wide" }
        if deviceType == .builtInTripleCamera         { return "triple" }
        if deviceType == .builtInTrueDepthCamera      { return "true_depth" }
        return "unknown"
    }
}

// MARK: - AVCaptureVideoDataOutputSampleBufferDelegate

extension CameraSession: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        onVideoSampleBuffer?(sampleBuffer)
    }

    func captureOutput(
        _ output: AVCaptureOutput,
        didDrop sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        // Dropped frame — metrics can be added later
    }
}
