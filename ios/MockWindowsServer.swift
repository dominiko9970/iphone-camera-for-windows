import Foundation
import Network

/// Mock Windows PC server for testing the iOS app without a real PC
/// Run this on a Mac on the same network to simulate the Windows receiver
class MockWindowsServer {
    
    private var listener: NWListener?
    private var connection: NWConnection?
    private let queue = DispatchQueue(label: "com.universalcam.mockserver")
    
    func start() {
        do {
            // Create QUIC listener
            let tlsOptions = NWProtocolTLS.Options()
            
            // For testing, accept any certificate
            sec_protocol_options_set_verify_block(
                tlsOptions.securityProtocolOptions,
                { _, _, completion in
                    print("[MockServer] Accepting connection")
                    completion(true)
                },
                queue
            )
            
            let quicOptions = NWProtocolQUIC.Options(alpn: ["universalcam/1"])
            let parameters = NWParameters(quic: quicOptions)
            
            let listener = try NWListener(using: parameters, on: 7779)
            self.listener = listener
            
            listener.stateUpdateHandler = { state in
                print("[MockServer] Listener state: \(state)")
            }
            
            listener.newConnectionHandler = { [weak self] connection in
                print("[MockServer] New connection from: \(connection.endpoint)")
                self?.handleConnection(connection)
            }
            
            listener.start(queue: queue)
            print("[MockServer] 🚀 Mock Windows server started on port 7779")
            print("[MockServer] Waiting for iPhone connections...")
            
        } catch {
            print("[MockServer] Failed to start: \(error)")
        }
    }
    
    private func handleConnection(_ connection: NWConnection) {
        self.connection = connection
        
        connection.stateUpdateHandler = { state in
            print("[MockServer] Connection state: \(state)")
            
            if case .ready = state {
                self.sendWelcome()
                self.receiveMessages(connection)
                self.startPongResponder()
            }
        }
        
        connection.start(queue: queue)
    }
    
    private func sendWelcome() {
        let welcome = """
        {"type":"welcome"}
        
        """
        
        if let data = welcome.data(using: .utf8) {
            connection?.send(content: data, completion: .contentProcessed({ error in
                if let error = error {
                    print("[MockServer] Failed to send welcome: \(error)")
                } else {
                    print("[MockServer] ✅ Sent welcome message")
                }
            }))
        }
    }
    
    private func receiveMessages(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            if let data = data, !data.isEmpty {
                self?.handleReceivedData(data)
            }
            
            if let error = error {
                print("[MockServer] Receive error: \(error)")
            }
            
            if !isComplete && error == nil {
                self?.receiveMessages(connection)
            }
        }
    }
    
    private func handleReceivedData(_ data: Data) {
        // Split by newlines for control messages
        let lines = data.split(separator: 0x0A)
        
        for line in lines {
            if line.count < 13 {
                // Likely binary frame data
                handleBinaryFrame(Data(line))
            } else {
                // Likely JSON control message
                handleControlMessage(Data(line))
            }
        }
    }
    
    private func handleControlMessage(_ data: Data) {
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let type = json["type"] as? String {
            
            print("[MockServer] 📩 Received: \(type)")
            
            switch type {
            case "hello":
                if let deviceName = json["deviceName"] as? String {
                    print("[MockServer]   Device: \(deviceName)")
                }
                if let capabilities = json["capabilities"] as? [String] {
                    print("[MockServer]   Capabilities: \(capabilities.joined(separator: ", "))")
                }
                
            case "start_stream":
                print("[MockServer] 🎥 Streaming started!")
                
            case "stop_stream":
                print("[MockServer] ⏹️ Streaming stopped")
                
            case "ping":
                if let ts = json["ts"] as? Int64 {
                    sendPong(ts: ts)
                }
                
            default:
                break
            }
        }
    }
    
    private func handleBinaryFrame(_ data: Data) {
        guard data.count >= 13 else { return }
        
        // Parse frame header
        let lengthData = data.subdata(in: 0..<4)
        let ptsData = data.subdata(in: 4..<12)
        let flags = data[12]
        
        let length = lengthData.withUnsafeBytes { $0.load(as: UInt32.self).littleEndian }
        let pts = ptsData.withUnsafeBytes { $0.load(as: Int64.self).littleEndian }
        
        let isKeyframe = (flags & 0x01) != 0
        let frameType = isKeyframe ? "KEYFRAME" : "frame"
        
        // Check if it's video or audio by looking at payload
        let isVideo = data.count > 16 && data[13] == 0x00 && data[14] == 0x00
        let mediaType = isVideo ? "📹 Video" : "🎵 Audio"
        
        print("[MockServer] \(mediaType) \(frameType): \(length) bytes, pts=\(pts)μs")
    }
    
    private var pongTimer: Timer?
    
    private func startPongResponder() {
        // Respond to pings every second
        pongTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
            // Pong will be sent when ping is received
        }
    }
    
    private func sendPong(ts: Int64) {
        let pong = """
        {"type":"pong","ts":\(ts)}
        
        """
        
        if let data = pong.data(using: .utf8) {
            connection?.send(content: data, completion: .idempotent)
            print("[MockServer] 🏓 Pong sent (ts=\(ts))")
        }
    }
    
    func stop() {
        listener?.cancel()
        connection?.cancel()
        pongTimer?.invalidate()
        print("[MockServer] Server stopped")
    }
}

// MARK: - Command Line Tool

#if os(macOS)
print("╔════════════════════════════════════════╗")
print("║  UniversalCam Mock Windows Server     ║")
print("║  For testing iOS app                  ║")
print("╚════════════════════════════════════════╝")
print()

let server = MockWindowsServer()
server.start()

print()
print("Press Ctrl+C to stop the server")
print()

// Keep running
RunLoop.main.run()
#endif
