import Foundation
import Combine
import AVFoundation
import Network
import UIKit

/// Central state machine. Owns QuicTransport, USBTransport, and BonjourDiscovery.
/// Wires CameraSession output → VideoEncoder → active transport.
/// All camera/streaming control is driven by the PC app via control messages.
final class ConnectionManager: ObservableObject {

    // MARK: - State

    enum State: Equatable {
        case idle
        case discovering
        case connecting(host: String)
        case connected
        case streaming
        case error(String)
    }

    enum TransportType: String {
        case wifi = "WiFi"
        case usb  = "USB"
    }

    @Published var state:       State  = .idle
    @Published var peerHost:    String = ""
    @Published var latencyMs:   Int    = 0
    @Published var discoveredPeers: [NWEndpoint] = []
    @Published var activeTransport: TransportType? = nil
    @Published var usbListening: Bool = false

    /// Set by the app to allow PC-initiated camera switching
    weak var cameraSession: CameraSession?

    // MARK: - Private

    private let wifiTransport = QuicTransport()
    private let usbTransport  = USBTransport()
    private let discovery     = BonjourDiscovery()
    private let encoder       = VideoEncoder()
    private let audioCapture  = AudioCapture()
    private var pingTimer:    Timer?
    private var pingSentAt:   Date?
    private var cancellables  = Set<AnyCancellable>()
    private var isEncoderPrepared = false

    // MARK: - Lifecycle

    init() {
        bindDiscovery()
        bindWifiTransport()
        bindUSBTransport()
        bindEncoder()
        checkPermissions()
        setupOrientationObserver()
    }

    deinit {
        NotificationCenter.default.removeObserver(self,
            name: UIDevice.orientationDidChangeNotification, object: nil)
        UIDevice.current.endGeneratingDeviceOrientationNotifications()
    }

    // MARK: - Public API

    func startDiscovery() {
        guard state == .idle || {
            if case .error = state { return true }
            return false
        }() else { return }
        state = .discovering
        discovery.start()
    }

    /// Connect to a discovered Bonjour service endpoint.
    func connect(to endpoint: NWEndpoint) {
        let displayName = Self.displayName(for: endpoint)
        state = .connecting(host: displayName)
        peerHost = displayName
        // Try QUIC (WiFi) and TCP (USB/fallback) simultaneously; first welcome wins
        wifiTransport.connect(to: endpoint)
        if case .service(let name, _, _, _) = endpoint {
            // For Bonjour endpoints we don't have a raw IP yet; TCP will connect after welcome
            _ = name
        }
    }

    /// Connect to a specific host/port (for manual IP entry).
    func connect(to host: String, port: UInt16 = 7779) {
        state = .connecting(host: host)
        peerHost = host
        // Try QUIC on 7779 and TCP on 7780 simultaneously; first welcome wins
        wifiTransport.connect(to: host, port: port)
        usbTransport.connect(to: host)
    }

    func disconnect() {
        stopStreaming()
        UIApplication.shared.isIdleTimerDisabled = false
        wifiTransport.disconnect()
        // Don't stop USB listener — keep it available for reconnection
        discovery.stop()
        activeTransport = nil
        state = .idle
    }

    func disconnectUSB() {
        if activeTransport == .usb {
            stopStreaming()
        }
        usbTransport.disconnect()
        if activeTransport == .usb {
            activeTransport = nil
            state = .idle
        }
    }

    func startStreaming() {
        guard state == .connected else { return }

        // Prepare encoder if not already done
        if !isEncoderPrepared {
            encoder.prepare(width: 1920, height: 1080, fps: 30, bitrate: 8_000_000)
            isEncoderPrepared = true
        }

        // Do NOT echo start_stream back — Windows drives the stream lifecycle.
        // Sending start_stream here would open a new QUIC stream that's immediately
        // closed, causing "stream accept: operation aborted" on Windows.

        // Start audio capture
        audioCapture.start()

        // Keep screen on while streaming
        UIApplication.shared.isIdleTimerDisabled = true

        state = .streaming
        startPingTimer()
        print("[ConnectionManager] Streaming started via \(activeTransport?.rawValue ?? "unknown")")
    }

    func stopStreaming() {
        guard state == .streaming else { return }

        sendControl(.stopStream)

        // Stop audio capture
        audioCapture.stop()

        // Flush encoder
        encoder.flush()

        // Allow screen to sleep again
        UIApplication.shared.isIdleTimerDisabled = false

        state = .connected
        pingTimer?.invalidate()
        pingTimer = nil
        print("[ConnectionManager] Streaming stopped")
    }

    /// Called by CameraSession.onVideoSampleBuffer
    func handleVideoSample(_ sampleBuffer: CMSampleBuffer) {
        guard state == .streaming else { return }
        encoder.encode(sampleBuffer)
    }

    // MARK: - Transport Abstraction

    /// Send a control message via the active transport.
    private func sendControl(_ message: ControlMessage) {
        switch activeTransport {
        case .usb:
            usbTransport.sendControl(message)
        case .wifi:
            wifiTransport.sendControl(message)
        case .none:
            // Try both — one might be mid-handshake
            wifiTransport.sendControl(message)
            usbTransport.sendControl(message)
        }
    }

    /// Send a video frame via the active transport.
    private func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) {
        switch activeTransport {
        case .usb:
            usbTransport.sendVideoFrame(frame)
        case .wifi:
            wifiTransport.sendVideoFrame(frame)
        case .none:
            break
        }
    }

    /// Send an audio frame via the active transport.
    private func sendAudioFrame(_ frame: AudioCapture.AudioFrame) {
        switch activeTransport {
        case .usb:
            usbTransport.sendAudioFrame(frame)
        case .wifi:
            wifiTransport.sendAudioFrame(frame)
        case .none:
            break
        }
    }

    // MARK: - Private Wiring — WiFi

    private func bindDiscovery() {
        discovery.$discovered
            .receive(on: DispatchQueue.main)
            .sink { [weak self] peers in
                guard let self else { return }
                self.discoveredPeers = peers
                // Auto-connect via WiFi if not already connected (USB takes priority)
                if self.state == .discovering, self.activeTransport == nil, let firstPeer = peers.first {
                    self.connect(to: firstPeer)
                }
            }
            .store(in: &cancellables)
    }

    private func bindWifiTransport() {
        wifiTransport.onStateChange = { [weak self] state in
            DispatchQueue.main.async {
                self?.handleWifiTransportStateChange(state)
            }
        }

        wifiTransport.onControlMessage = { [weak self] data in
            self?.parseControlMessage(data, from: .wifi)
        }
    }

    private func handleWifiTransportStateChange(_ transportState: NWConnection.State) {
        switch transportState {
        case .ready:
            // Don't wait for a server-initiated Welcome stream (requires newConnectionHandler,
            // which is unreliable across iOS versions). Send Hello immediately so Windows
            // can drive the handshake (Configure → ConfigureAck → StartStream).
            // USB still uses the Welcome → Hello path and will override if it connects later.
            guard activeTransport == nil else { break } // USB already active — don't override
            activeTransport = .wifi
            let hello = ControlMessage.hello(
                deviceName: UIDevice.current.name,
                capabilities: ["h264", "aac_lc", "stereo_audio"]
            )
            wifiTransport.sendControl(hello)
            sendCurrentOrientation()
            DispatchQueue.main.async { self.state = .connected }
            print("[ConnectionManager] QUIC ready — sent Hello proactively")
            sendAvailableCameras()
        case .failed(let error):
            if activeTransport == .wifi {
                state = .error("WiFi connection failed: \(error.localizedDescription)")
                activeTransport = nil
            }
        case .cancelled:
            if activeTransport == .wifi && state != .idle {
                activeTransport = nil
                state = .idle
            }
        default:
            break
        }
    }

    // MARK: - Private Wiring — USB

    private func bindUSBTransport() {
        usbTransport.onStateChange = { [weak self] usbState in
            DispatchQueue.main.async {
                self?.handleUSBStateChange(usbState)
            }
        }

        usbTransport.onControlMessage = { [weak self] data in
            self?.parseControlMessage(data, from: .usb)
        }
    }

    private func handleUSBStateChange(_ usbState: USBTransport.USBConnectionState) {
        switch usbState {
        case .listening:
            usbListening = true
            print("[ConnectionManager] USB listener ready")

        case .connected:
            // USB connection established — if we were streaming over WiFi, keep WiFi.
            // If idle/discovering, the welcome/hello handshake will set the state.
            print("[ConnectionManager] USB connection established")

        case .disconnected:
            if activeTransport == .usb {
                // Lost USB connection
                if state == .streaming {
                    stopStreaming()
                }
                activeTransport = nil
                state = .idle
                print("[ConnectionManager] USB disconnected, reverting to idle")
            }

        case .failed(let msg):
            usbListening = false
            print("[ConnectionManager] USB failed: \(msg)")
        }
    }

    // MARK: - Private Wiring — Encoder

    private func bindEncoder() {
        encoder.onEncodedFrame = { [weak self] frame in
            self?.sendVideoFrame(frame)
        }

        encoder.onError = { [weak self] error in
            DispatchQueue.main.async {
                self?.state = .error("Video encoding failed: \(error.localizedDescription)")
            }
        }

        audioCapture.onEncodedAudio = { [weak self] audioFrame in
            self?.sendAudioFrame(audioFrame)
        }
    }

    // MARK: - Permissions

    private func checkPermissions() {
        let cameraStatus = AVCaptureDevice.authorizationStatus(for: .video)
        if cameraStatus == .notDetermined {
            AVCaptureDevice.requestAccess(for: .video) { granted in
                print("[ConnectionManager] Camera access: \(granted)")
            }
        }

        let micStatus = AVCaptureDevice.authorizationStatus(for: .audio)
        if micStatus == .notDetermined {
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                print("[ConnectionManager] Microphone access: \(granted)")
            }
        }
    }

    // MARK: - Ping / Latency

    private func startPingTimer() {
        pingTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.pingSentAt = Date()
            let ts = Int64(Date().timeIntervalSince1970 * 1000)
            self.sendControl(.ping(ts: ts))
        }
    }

    func handlePong(ts: Int64) {
        guard let sent = pingSentAt else { return }
        let rtt = Int(Date().timeIntervalSince(sent) * 1000)
        DispatchQueue.main.async { self.latencyMs = rtt / 2 }
    }

    // MARK: - Incoming Control Messages

    /// Parse a control message received from a transport.
    /// The transport layer handles framing/reassembly, so each callback delivers
    /// exactly one complete JSON message (without the stream type prefix or newline).
    private func parseControlMessage(_ data: Data, from transport: TransportType) {
        guard let message = try? JSONDecoder().decode(ControlMessage.self, from: data) else {
            print("[ConnectionManager] Failed to decode control message (\(data.count) bytes)")
            return
        }

        DispatchQueue.main.async {
            self.handleIncomingControl(message, from: transport)
        }
    }

    /// Sends the list of available cameras to the PC.
    private func sendAvailableCameras() {
        guard let session = cameraSession else { return }
        let cameras = session.availableCameras
        let currentID = session.currentCameraID
        let msg = ControlMessage.availableCameras(cameras: cameras, currentCameraID: currentID)
        sendControl(msg)
        print("[ConnectionManager] Sent \(cameras.count) available cameras to PC")
    }

    func handleIncomingControl(_ message: ControlMessage, from transport: TransportType) {
        switch message {
        case .welcome:
            // USB always wins; WiFi only wins if USB hasn't connected yet.
            // This prevents a QUIC welcome (arriving after USB welcome) from
            // flipping activeTransport back to WiFi and desyncing Windows.
            let incomingIsUSB = (transport == .usb)
            guard activeTransport == nil || incomingIsUSB else { break }
            activeTransport = transport
            if transport == .usb { wifiTransport.disconnect() }

            let hello = ControlMessage.hello(
                deviceName: UIDevice.current.name,
                capabilities: ["h264", "aac_lc", "stereo_audio"]
            )
            sendControl(hello)
            sendCurrentOrientation()
            DispatchQueue.main.async { self.state = .connected }
            print("[ConnectionManager] Connected to peer via \(transport.rawValue)")

            sendAvailableCameras()

        case .configure(let resolution, let fps, _, let bitrate):
            let components = resolution.split(separator: "x")
            if components.count == 2,
               let width = Int32(components[0]),
               let height = Int32(components[1]) {
                let preset: AVCaptureSession.Preset
                if width >= 3840 {
                    preset = .hd4K3840x2160
                } else if width <= 1280 {
                    preset = .hd1280x720
                } else {
                    preset = .hd1920x1080
                }
                cameraSession?.setResolutionAndFPS(preset: preset, fps: Int32(fps))
                encoder.prepare(width: width, height: height, fps: Int32(fps), bitrate: bitrate)
                isEncoderPrepared = true
            }
            let ack = ControlMessage.configureAck(resolution: resolution, fps: fps)
            sendControl(ack)

        case .configureAck:
            break

        case .startStream:
            DispatchQueue.main.async {
                self.startStreaming()
            }

        case .stopStream:
            DispatchQueue.main.async {
                self.stopStreaming()
            }

        case .switchCamera(let cameraID):
            guard let session = cameraSession else { return }

            if let cameraID = cameraID {
                session.switchToCamera(withID: cameraID) { info in
                    guard let info else {
                        print("[ConnectionManager] Failed to switch to camera ID: \(cameraID)")
                        return
                    }
                    self.sendControl(.switchCameraAck(cameraID: info.id, cameraName: info.name))
                    print("[ConnectionManager] Camera switched to \(info.name) (\(info.id))")
                }
            } else {
                session.switchCamera { info in
                    let currentID = info?.id ?? session.currentCameraID
                    let name = info?.name ?? session.availableCameras.first { $0.id == currentID }?.name ?? "unknown"
                    self.sendControl(.switchCameraAck(cameraID: currentID, cameraName: name))
                    print("[ConnectionManager] Camera toggled to \(name)")
                }
            }

        case .switchCameraAck:
            break

        case .listCameras:
            sendAvailableCameras()

        case .availableCameras:
            break

        case .orientationChanged:
            break // outbound-only from iOS; ignore if PC ever echoes it

        case .pong(let ts):
            handlePong(ts: ts)

        case .ping(let ts):
            sendControl(.pong(ts: ts))

        default:
            break
        }
    }

    // MARK: - Orientation

    private func setupOrientationObserver() {
        UIDevice.current.beginGeneratingDeviceOrientationNotifications()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(deviceOrientationDidChange),
            name: UIDevice.orientationDidChangeNotification,
            object: nil
        )
    }

    @objc private func deviceOrientationDidChange() {
        let o = UIDevice.current.orientation
        guard o.isPortrait || o.isLandscape else { return } // ignore face-up/down/unknown
        let name = o.isPortrait ? "portrait" : "landscape"
        sendControl(.orientationChanged(orientation: name))
        print("[ConnectionManager] Orientation → \(name)")
    }

    /// Sends the current device orientation immediately (call after hello so Windows
    /// knows the orientation before the first video frame is decoded).
    private func sendCurrentOrientation() {
        let o = UIDevice.current.orientation
        if o.isPortrait || o.isLandscape {
            sendControl(.orientationChanged(orientation: o.isPortrait ? "portrait" : "landscape"))
        }
    }

    // MARK: - Helpers

    /// Extract a human-readable display name from an NWEndpoint.
    static func displayName(for endpoint: NWEndpoint) -> String {
        switch endpoint {
        case .service(let name, _, _, _):
            return name
        case .hostPort(let host, let port):
            return "\(host):\(port)"
        default:
            return "\(endpoint)"
        }
    }
}

// MARK: - Control Message Types

enum ControlMessage: Codable {
    case hello(deviceName: String, capabilities: [String])
    case welcome
    case configure(resolution: String, fps: Int, codec: String, bitrate: Int)
    case configureAck(resolution: String, fps: Int)
    case startStream
    case stopStream
    case switchCamera(cameraID: String?)
    case switchCameraAck(cameraID: String, cameraName: String)
    case listCameras
    case availableCameras(cameras: [CameraInfo], currentCameraID: String)
    case ping(ts: Int64)
    case pong(ts: Int64)
    /// iOS → PC: device orientation changed. orientation = "portrait" | "landscape"
    case orientationChanged(orientation: String)

    private enum CodingKeys: String, CodingKey {
        case type, deviceName, capabilities, resolution, fps, codec, bitrate, ts
        case cameraID, cameraName, cameras, currentCameraID
        case orientation
    }

    var type: String {
        switch self {
        case .hello:               return "hello"
        case .welcome:             return "welcome"
        case .configure:           return "configure"
        case .configureAck:        return "configure_ack"
        case .startStream:         return "start_stream"
        case .stopStream:          return "stop_stream"
        case .switchCamera:        return "switch_camera"
        case .switchCameraAck:     return "switch_camera_ack"
        case .listCameras:         return "list_cameras"
        case .availableCameras:    return "available_cameras"
        case .ping:                return "ping"
        case .pong:                return "pong"
        case .orientationChanged:  return "orientation_changed"
        }
    }

    // MARK: - Codable Implementation

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(type, forKey: .type)

        switch self {
        case .hello(let deviceName, let capabilities):
            try container.encode(deviceName, forKey: .deviceName)
            try container.encode(capabilities, forKey: .capabilities)
        case .welcome:
            break
        case .configure(let resolution, let fps, let codec, let bitrate):
            try container.encode(resolution, forKey: .resolution)
            try container.encode(fps, forKey: .fps)
            try container.encode(codec, forKey: .codec)
            try container.encode(bitrate, forKey: .bitrate)
        case .configureAck(let resolution, let fps):
            try container.encode(resolution, forKey: .resolution)
            try container.encode(fps, forKey: .fps)
        case .startStream, .stopStream, .listCameras:
            break
        case .switchCamera(let cameraID):
            try container.encodeIfPresent(cameraID, forKey: .cameraID)
        case .switchCameraAck(let cameraID, let cameraName):
            try container.encode(cameraID, forKey: .cameraID)
            try container.encode(cameraName, forKey: .cameraName)
        case .availableCameras(let cameras, let currentCameraID):
            try container.encode(cameras, forKey: .cameras)
            try container.encode(currentCameraID, forKey: .currentCameraID)
        case .ping(let ts), .pong(let ts):
            try container.encode(ts, forKey: .ts)
        case .orientationChanged(let orientation):
            try container.encode(orientation, forKey: .orientation)
        }
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let type = try container.decode(String.self, forKey: .type)

        switch type {
        case "hello":
            let deviceName = try container.decode(String.self, forKey: .deviceName)
            let capabilities = try container.decode([String].self, forKey: .capabilities)
            self = .hello(deviceName: deviceName, capabilities: capabilities)
        case "welcome":
            self = .welcome
        case "configure":
            let resolution = try container.decode(String.self, forKey: .resolution)
            let fps = try container.decode(Int.self, forKey: .fps)
            let codec = try container.decode(String.self, forKey: .codec)
            let bitrate = try container.decode(Int.self, forKey: .bitrate)
            self = .configure(resolution: resolution, fps: fps, codec: codec, bitrate: bitrate)
        case "configure_ack":
            let resolution = try container.decode(String.self, forKey: .resolution)
            let fps = try container.decode(Int.self, forKey: .fps)
            self = .configureAck(resolution: resolution, fps: fps)
        case "start_stream":
            self = .startStream
        case "stop_stream":
            self = .stopStream
        case "switch_camera":
            let cameraID = try container.decodeIfPresent(String.self, forKey: .cameraID)
            self = .switchCamera(cameraID: cameraID)
        case "switch_camera_ack":
            let cameraID = try container.decode(String.self, forKey: .cameraID)
            let cameraName = try container.decode(String.self, forKey: .cameraName)
            self = .switchCameraAck(cameraID: cameraID, cameraName: cameraName)
        case "list_cameras":
            self = .listCameras
        case "available_cameras":
            let cameras = try container.decode([CameraInfo].self, forKey: .cameras)
            let currentCameraID = try container.decode(String.self, forKey: .currentCameraID)
            self = .availableCameras(cameras: cameras, currentCameraID: currentCameraID)
        case "ping":
            let ts = try container.decode(Int64.self, forKey: .ts)
            self = .ping(ts: ts)
        case "pong":
            let ts = try container.decode(Int64.self, forKey: .ts)
            self = .pong(ts: ts)
        case "orientation_changed":
            let orientation = try container.decode(String.self, forKey: .orientation)
            self = .orientationChanged(orientation: orientation)
        default:
            throw DecodingError.dataCorruptedError(
                forKey: .type,
                in: container,
                debugDescription: "Unknown message type: \(type)"
            )
        }
    }
}
