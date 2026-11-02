import Network
import Foundation

/// TCP client transport — connects to the Windows app's TCP server on port 7780.
///
/// Used as a reliable TCP fallback when QUIC is unavailable or slow to connect.
/// Works over both Wi-Fi and USB (when Apple's USB network adapter is active).
///
/// Uses the same wire framing as QuicTransport:
///   [1 byte:  stream_type]
///   [4 bytes: payload_length LE]
///   [8 bytes: pts_us LE]
///   [1 byte:  flags]
///   [N bytes: payload]
///
/// All mutable state is accessed exclusively on `queue` for thread safety.
final class USBTransport {

    // MARK: - Constants

    /// TCP port the Windows app listens on for TCP connections from the iPhone.
    static let port: UInt16 = 7780

    /// Stream type identifiers (mirrors QuicTransport)
    private enum StreamType: UInt8 {
        case control = 0x00
        case video   = 0x01
        case audio   = 0x02
    }

    /// Header size for binary media frames: stream_type(1) + length(4) + pts(8) + flags(1)
    private static let frameHeaderSize = 14

    // MARK: - Callbacks

    var onControlMessage: ((Data) -> Void)?
    var onStateChange:    ((USBConnectionState) -> Void)?

    enum USBConnectionState: Equatable {
        case listening   // kept for API compat — means "ready / connecting"
        case connected
        case disconnected
        case failed(String)
    }

    // MARK: - Private (accessed on `queue`)

    private var listener: NWListener?
    private var connection: NWConnection?

    init() { startListening() }
    private let queue = DispatchQueue(label: "com.universalcam.tcp", qos: .userInteractive)

    /// Accumulation buffer for reassembling TCP segments into complete messages.
    private var receiveBuffer = Data()

    /// Flow control: number of sends in flight (not yet completed).
    private var waitingForVideoKeyframe = false
    private var sendsInFlight = 0
    private static let maxSendsInFlight = 30

    // MARK: - Connection

    /// Connect to the Windows app TCP server at the given host on port 7780.
    func connect(to host: String) {
        queue.async { [self] in
            connection?.cancel()
            receiveBuffer = Data()
            sendsInFlight = 0

            let endpoint = NWEndpoint.hostPort(
                host: NWEndpoint.Host(host),
                port: NWEndpoint.Port(rawValue: Self.port)!
            )
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true
            let params = NWParameters(tls: nil, tcp: tcp)
            params.allowLocalEndpointReuse = true

            let conn = NWConnection(to: endpoint, using: params)
            self.connection = conn

            conn.stateUpdateHandler = { [weak self] state in
                guard let self, self.connection === conn else { return }
                switch state {
                case .ready:
                    self.onStateChange?(.connected)
                    print("[USBTransport] Connected to PC at \(host):\(Self.port)")
                case .failed(let error):
                    self.connection = nil
                    self.receiveBuffer = Data()
                    self.onStateChange?(.failed(error.localizedDescription))
                    print("[USBTransport] Connection failed: \(error)")
                case .cancelled:
                    self.connection = nil
                    self.receiveBuffer = Data()
                    self.onStateChange?(.disconnected)
                default:
                    break
                }
            }

            conn.start(queue: queue)
            onStateChange?(.listening)
            receiveLoop(conn)
        }
    }

    /// Legacy API — kept so ConnectionManager doesn't need changes
    func startListening() {
        queue.async { [self] in
            guard listener == nil else { return }
            do {
                let tcp = NWProtocolTCP.Options()
                tcp.noDelay = true
                let server = try NWListener(using: NWParameters(tls: nil, tcp: tcp), on: 7781)
                server.newConnectionHandler = { [weak self] conn in
                    guard let self else { return }
                    self.connection?.cancel()
                    self.connection = conn
                    self.receiveBuffer = Data()
                    self.sendsInFlight = 0
                    self.waitingForVideoKeyframe = true
                    conn.stateUpdateHandler = { [weak self] state in
                        guard let self, self.connection === conn else { return }
                        switch state {
                        case .ready: self.onStateChange?(.connected)
                        case .failed(let error):
                            self.connection = nil
                            self.onStateChange?(.failed(error.localizedDescription))
                        case .cancelled:
                            self.connection = nil
                            self.onStateChange?(.disconnected)
                        default: break
                        }
                    }
                    conn.start(queue: self.queue)
                    self.receiveLoop(conn)
                }
                server.start(queue: queue)
                listener = server
            } catch { onStateChange?(.failed(error.localizedDescription)) }
        }
    }

    func stopListening() {
        disconnect()
    }

    func disconnect() {
        queue.async { [self] in
            connection?.cancel()
            connection = nil
            receiveBuffer = Data()
            sendsInFlight = 0
            onStateChange?(.disconnected)
        }
    }

    var isConnected: Bool { connection != nil }

    // MARK: - Send

    func sendControl(_ message: ControlMessage) {
        guard let data = try? JSONEncoder().encode(message) else { return }
        var payload = Data([StreamType.control.rawValue])
        payload.append(data)
        payload.append(0x0A)
        queue.async { [self] in
            guard let conn = connection else { return }
            sendWithFlowControl(conn, data: payload)
        }
    }

    func sendVideoFrame(_ frame: VideoEncoder.EncodedFrame) {
        let flags: UInt8 = frame.isKeyframe ? 0x01 : 0x00
        let packet = makeFramePacket(streamType: .video, payload: frame.data, ptsUs: frame.ptsUs, flags: flags)
        queue.async { [self] in
            guard let conn = connection else { return }
            if sendsInFlight >= Self.maxSendsInFlight {
                waitingForVideoKeyframe = true
                return
            }
            if waitingForVideoKeyframe {
                guard frame.isKeyframe else { return }
                waitingForVideoKeyframe = false
            }
            sendWithFlowControl(conn, data: packet)
        }
    }

    func sendAudioFrame(_ frame: AudioCapture.AudioFrame) {
        let flags = UInt8(frame.channels)
        let packet = makeFramePacket(streamType: .audio, payload: frame.data, ptsUs: frame.ptsUs, flags: flags)
        queue.async { [self] in
            guard let conn = connection else { return }
            guard sendsInFlight < Self.maxSendsInFlight else { return }
            sendWithFlowControl(conn, data: packet)
        }
    }

    private func sendWithFlowControl(_ conn: NWConnection, data: Data) {
        sendsInFlight += 1
        conn.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            guard self.connection === conn else { return }
            self.sendsInFlight = max(0, self.sendsInFlight - 1)
            if let error {
                print("[USBTransport] Send error: \(error)")
            }
        })
    }

    // MARK: - Receive Loop with Reassembly

    private func receiveLoop(_ conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            if let data, !data.isEmpty {
                self?.receiveBuffer.append(data)
                self?.processReceiveBuffer()
            }
            if isComplete || error != nil {
                return
            }
            self?.receiveLoop(conn)
        }
    }

    private func processReceiveBuffer() {
        while !receiveBuffer.isEmpty {
            guard let firstByte = receiveBuffer.first else { break }

            if firstByte == StreamType.control.rawValue {
                guard let newlineIndex = receiveBuffer.dropFirst(1).firstIndex(of: 0x0A) else {
                    break
                }
                let jsonData = Data(receiveBuffer[receiveBuffer.startIndex + 1 ..< newlineIndex])
                receiveBuffer = Data(receiveBuffer[(newlineIndex + 1)...])
                if !jsonData.isEmpty {
                    onControlMessage?(jsonData)
                }

            } else if firstByte == StreamType.video.rawValue || firstByte == StreamType.audio.rawValue {
                guard receiveBuffer.count >= Self.frameHeaderSize else { break }
                let payloadLength = receiveBuffer.withUnsafeBytes { ptr -> Int in
                    let base = ptr.baseAddress!.advanced(by: 1)
                    return Int(base.loadUnaligned(as: UInt32.self).littleEndian)
                }
                let totalFrameSize = Self.frameHeaderSize + payloadLength
                guard receiveBuffer.count >= totalFrameSize else { break }
                receiveBuffer = Data(receiveBuffer.dropFirst(totalFrameSize))

            } else {
                if let newlineIndex = receiveBuffer.firstIndex(of: 0x0A) {
                    let lineData = Data(receiveBuffer[receiveBuffer.startIndex ..< newlineIndex])
                    receiveBuffer = Data(receiveBuffer[(newlineIndex + 1)...])
                    if !lineData.isEmpty {
                        onControlMessage?(lineData)
                    }
                } else {
                    break
                }
            }
        }
    }

    // MARK: - Helpers

    private func makeFramePacket(streamType: StreamType, payload: Data, ptsUs: Int64, flags: UInt8) -> Data {
        var packet = Data()
        packet.reserveCapacity(Self.frameHeaderSize + payload.count)
        packet.append(streamType.rawValue)
        var length = UInt32(payload.count).littleEndian
        var pts    = ptsUs.littleEndian
        packet.append(contentsOf: withUnsafeBytes(of: &length) { Array($0) })
        packet.append(contentsOf: withUnsafeBytes(of: &pts)    { Array($0) })
        packet.append(flags)
        packet.append(payload)
        return packet
    }
}
