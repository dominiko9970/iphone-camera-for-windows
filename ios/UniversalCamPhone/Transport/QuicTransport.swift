import Network
import Foundation

/// Low-level QUIC transport using Network.framework NWConnection.
/// Port 7779. Three logical streams multiplexed over one QUIC connection
/// using a 1-byte stream type prefix on each message:
///   0x00 — Control  (JSON, newline-delimited, bidirectional)
///   0x01 — Video    (binary framed H.264 Annex B, phone → PC)
///   0x02 — Audio    (binary framed AAC-LC, phone → PC)
///
/// Wire frame format for video/audio streams:
///   [1 byte:  stream_type (0x01 or 0x02)]
///   [4 bytes: payload_length uint32 LE]
///   [8 bytes: pts_us int64 LE]
///   [1 byte:  flags]  video: bit0=keyframe, bit1=hevc; audio: channels (1 or 2)
///   [N bytes: payload]
///
/// All mutable state is accessed exclusively on `queue` for thread safety.
final class QuicTransport {

    // MARK: - Constants

    static let port: UInt16 = 7779
    static let serviceType  = "_universalcam._tcp"

    /// Stream type identifiers for in-band multiplexing
    private enum StreamType: UInt8 {
        case control = 0x00
        case video   = 0x01
        case audio   = 0x02
    }

    /// Header size for binary media frames: stream_type(1) + length(4) + pts(8) + flags(1)
    private static let frameHeaderSize = 14

    // MARK: - Callbacks

    var onControlMessage: ((Data) -> Void)?
    var onStateChange:    ((NWConnection.State) -> Void)?

    // MARK: - Private (accessed on `queue`)

    private var connection: NWConnection?
    private let queue = DispatchQueue(label: "com.universalcam.quic", qos: .userInteractive)
    private var retryCount = 0
    private static let maxRetries = 10

    /// Accumulation buffer for reassembling partial messages from the receive loop.
    private var receiveBuffer = Data()

    /// Flow control: number of sends in flight (not yet completed).
    private var sendsInFlight = 0
    private static let maxSendsInFlight = 30

    /// The endpoint we're connecting to, for reconnect logic.
    private var currentEndpoint: NWEndpoint?

    // MARK: - Connection

    /// Connect to a discovered Bonjour service endpoint (preferred for NWBrowser results).
    func connect(to endpoint: NWEndpoint) {
        queue.async { [self] in
            currentEndpoint = endpoint
            let params = makeQUICParameters()
            startConnection(NWConnection(to: endpoint, using: params))
        }
    }

    /// Connect to a host/port pair directly.
    func connect(to host: String, port: UInt16 = QuicTransport.port) {
        queue.async { [self] in
            let endpoint = NWEndpoint.hostPort(
                host: NWEndpoint.Host(host),
                port: NWEndpoint.Port(rawValue: port)!
            )
            currentEndpoint = endpoint
            let params = makeQUICParameters()
            startConnection(NWConnection(to: endpoint, using: params))
        }
    }

    /// Must be called on `queue`.
    private func startConnection(_ conn: NWConnection) {
        // Cancel any existing connection to avoid leaking it
        connection?.cancel()

        self.connection = conn
        self.receiveBuffer = Data()
        self.sendsInFlight = 0
        conn.stateUpdateHandler = { [weak self] state in
            // NWConnection delivers state updates on its queue (which is our queue)
            self?.onStateChange?(state)
            if case .ready = state {
                self?.retryCount = 0
                self?.sendsInFlight = 0
            }
            if case .failed = state { self?.scheduleReconnect() }
        }

        conn.start(queue: queue)
        receiveLoop(conn)
    }

    func disconnect() {
        queue.async { [self] in
            connection?.cancel()
            connection = nil
            currentEndpoint = nil
            retryCount = 0
            receiveBuffer = Data()
            sendsInFlight = 0
        }
    }

    // MARK: - Send

    func sendControl(_ message: ControlMessage) {
        guard let data = try? JSONEncoder().encode(message) else { return }
        var payload = Data([StreamType.control.rawValue])
        payload.append(data)
        payload.append(0x0A) // newline delimiter
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
            // Drop frames if the send queue is backed up
            guard sendsInFlight < Self.maxSendsInFlight else { return }
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

    /// Send data with backpressure tracking. Must be called on `queue`.
    private func sendWithFlowControl(_ conn: NWConnection, data: Data) {
        sendsInFlight += 1
        conn.send(content: data, completion: .contentProcessed { [weak self] error in
            // Completion is delivered on the connection's queue (which is our queue)
            guard let self else { return }
            self.sendsInFlight = max(0, self.sendsInFlight - 1)
            if let error {
                print("[QuicTransport] Send error: \(error)")
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
            // isComplete means the current QUIC stream ended (FIN), but the connection
            // is still alive and the server may open additional streams. Stop only on error.
            if let error {
                print("[QuicTransport] Receive error: \(error)")
                return
            }
            self?.receiveLoop(conn)
        }
    }

    /// Consume complete messages from the receive buffer.
    /// The PC only sends control messages (stream type 0x00, newline-delimited JSON).
    /// We also handle the theoretical case of binary frames from the PC.
    private func processReceiveBuffer() {
        while !receiveBuffer.isEmpty {
            guard let firstByte = receiveBuffer.first else { break }

            if firstByte == StreamType.control.rawValue {
                // Control message: 0x00 prefix + JSON + 0x0A newline
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
                // Unknown stream type — try to treat as raw JSON (legacy/fallback)
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

    private func makeQUICParameters() -> NWParameters {
        let quicOptions = NWProtocolQUIC.Options(alpn: ["universalcam/1"])

        // Allow the PC (server) to open unidirectional streams for control messages.
        // Default is 0, which blocks server-initiated streams entirely.
        quicOptions.initialMaxStreamsUnidirectional = 100

        // Configure TLS on the QUIC options directly
        let secOptions = quicOptions.securityProtocolOptions

        #if DEBUG
        sec_protocol_options_set_verify_block(
            secOptions,
            { _, _, completion in completion(true) },
            queue
        )
        #else
        sec_protocol_options_set_verify_block(
            secOptions,
            { _, sec_trust, completion in
                let trust = sec_trust_copy_ref(sec_trust).takeRetainedValue()
                var error: CFError?
                let isValid = SecTrustEvaluateWithError(trust, &error)
                completion(isValid)
            },
            queue
        )
        #endif

        let params = NWParameters(quic: quicOptions)
        return params
    }

    /// Builds the binary frame: [stream_type][length][pts][flags][payload]
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

    // MARK: - Reconnect

    /// Must be called on `queue`.
    private func scheduleReconnect() {
        guard let endpoint = currentEndpoint else { return }
        guard retryCount < Self.maxRetries else {
            print("[QuicTransport] Max retries reached, giving up")
            return
        }
        let delay = min(pow(2.0, Double(retryCount)), 30.0)
        retryCount += 1
        print("[QuicTransport] Reconnecting in \(delay)s (attempt \(retryCount)/\(Self.maxRetries))")
        queue.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self else { return }
            self.currentEndpoint = endpoint
            let params = self.makeQUICParameters()
            self.startConnection(NWConnection(to: endpoint, using: params))
        }
    }
}
