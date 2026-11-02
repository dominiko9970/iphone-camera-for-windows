import SwiftUI
import AVFoundation

struct ContentView: View {

    @EnvironmentObject var connection: ConnectionManager
    @EnvironmentObject var camera:     CameraSession

    @State private var showSettings = false
    @State private var cameraAuthorized = true
    @State private var micAuthorized = true

    var body: some View {
        ZStack {
            if !cameraAuthorized {
                // Permission denied overlay
                PermissionDeniedView(
                    cameraAuthorized: cameraAuthorized,
                    micAuthorized: micAuthorized
                )
            } else {
                // Full-screen camera preview
                CameraPreviewView(session: camera.captureSession)
                    .ignoresSafeArea()
                    .onAppear {
                        camera.start()
                        connection.startDiscovery()
                    }
                    .onDisappear { camera.stop() }
            }

            // Status overlay (always visible when camera is active)
            if cameraAuthorized {
                VStack {
                    HStack {
                        StatusPill(
                            state: connection.state,
                            latency: connection.latencyMs,
                            transport: connection.activeTransport
                        )
                        .padding(.top, 56)
                        .padding(.leading, 16)
                        Spacer()

                        // Settings
                        Button {
                            showSettings = true
                        } label: {
                            Image(systemName: "gearshape.fill")
                                .font(.system(size: 22))
                                .foregroundStyle(.white)
                                .padding(10)
                                .background(.ultraThinMaterial, in: Circle())
                        }
                        .padding(.top, 56)
                        .padding(.trailing, 16)
                    }

                    Spacer()

                    if !micAuthorized {
                        Text("Microphone access denied — audio will not stream.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                            .padding(.horizontal, 16)
                            .padding(.bottom, 4)
                    }

                    // Connection hint when idle
                    if case .idle = connection.state {
                        Text("Waiting for PC connection...")
                            .font(.callout)
                            .foregroundStyle(.white.opacity(0.7))
                            .padding(.bottom, 48)
                    } else if case .discovering = connection.state {
                        VStack(spacing: 4) {
                            Text("Searching for PC on local network...")
                                .font(.callout)
                                .foregroundStyle(.white.opacity(0.7))
                            if connection.usbListening {
                                Text("TCP fallback ready on port \(USBTransport.port)")
                                    .font(.caption)
                                    .foregroundStyle(.white.opacity(0.5))
                            }
                        }
                        .padding(.bottom, 48)
                    }
                }
            }
        }
        .task { await checkPermissions() }
        .sheet(isPresented: $showSettings) {
            SettingsView()
                .environmentObject(connection)
                .environmentObject(camera)
        }
    }

    private func checkPermissions() async {
        // Camera
        let camStatus = AVCaptureDevice.authorizationStatus(for: .video)
        if camStatus == .notDetermined {
            let granted = await AVCaptureDevice.requestAccess(for: .video)
            await MainActor.run { cameraAuthorized = granted }
        } else {
            await MainActor.run { cameraAuthorized = camStatus == .authorized }
        }

        // Microphone
        let micStatus = AVCaptureDevice.authorizationStatus(for: .audio)
        if micStatus == .notDetermined {
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            await MainActor.run { micAuthorized = granted }
        } else {
            await MainActor.run { micAuthorized = micStatus == .authorized }
        }
    }
}

// MARK: - Permission Denied

private struct PermissionDeniedView: View {
    let cameraAuthorized: Bool
    let micAuthorized: Bool

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(spacing: 20) {
                Image(systemName: "camera.fill")
                    .font(.system(size: 48))
                    .foregroundStyle(.secondary)

                Text("Camera Access Required")
                    .font(.title2.bold())
                    .foregroundStyle(.white)

                Text("UniversalCam needs camera access to stream video to your PC. Open Settings to grant permission.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 32)

                Button("Open Settings") {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        UIApplication.shared.open(url)
                    }
                }
                .buttonStyle(.borderedProminent)
            }
        }
    }
}

// MARK: - Sub-views

private struct StatusPill: View {
    let state:     ConnectionManager.State
    let latency:   Int
    let transport: ConnectionManager.TransportType?

    var body: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(stateColor)
                .frame(width: 8, height: 8)
            Text(stateLabel)
                .font(.caption.bold())
                .foregroundStyle(.white)
            if let transport, state == .connected || state == .streaming {
                Text(transport.rawValue)
                    .font(.caption2.bold())
                    .foregroundStyle(.white.opacity(0.8))
                    .padding(.horizontal, 4)
                    .padding(.vertical, 1)
                    .background(
                        transport == .usb ? Color.blue.opacity(0.6) : Color.purple.opacity(0.6),
                        in: Capsule()
                    )
            }
            if state == .streaming, latency > 0 {
                Text("\(latency)ms")
                    .font(.caption2)
                    .foregroundStyle(.white.opacity(0.8))
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.ultraThinMaterial, in: Capsule())
    }

    private var stateColor: Color {
        switch state {
        case .streaming:         return .green
        case .connected:         return .yellow
        case .connecting:        return .orange
        case .discovering:       return .blue
        case .error:             return .red
        case .idle:              return .gray
        }
    }

    private var stateLabel: String {
        switch state {
        case .idle:              return "Not connected"
        case .discovering:       return "Searching..."
        case .connecting(let h): return "Connecting to \(h)"
        case .connected:         return "Connected"
        case .streaming:         return "Streaming"
        case .error(let msg):    return "Error: \(msg)"
        }
    }
}

private struct SettingsView: View {
    @EnvironmentObject var connection: ConnectionManager
    @EnvironmentObject var camera:     CameraSession
    @Environment(\.dismiss) var dismiss

    @State private var manualIP = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Connection") {
                    if let transport = connection.activeTransport {
                        HStack {
                            Image(systemName: transport == .usb ? "cable.connector" : "wifi")
                            Text("Connected via \(transport.rawValue)")
                            Spacer()
                            Image(systemName: "checkmark.circle.fill")
                                .foregroundStyle(.green)
                        }
                    }

                    if connection.discoveredPeers.isEmpty && connection.activeTransport != .wifi {
                        HStack {
                            ProgressView()
                                .padding(.trailing, 8)
                            Text("Searching for Windows PC...")
                                .foregroundStyle(.secondary)
                        }
                    } else {
                        ForEach(connection.discoveredPeers, id: \.self) { peer in
                            let name = ConnectionManager.displayName(for: peer)
                            Button {
                                connection.connect(to: peer)
                                dismiss()
                            } label: {
                                HStack {
                                    Image(systemName: "desktopcomputer")
                                    Text(name)
                                    Spacer()
                                    if connection.peerHost == name {
                                        Image(systemName: "checkmark.circle.fill")
                                            .foregroundStyle(.green)
                                    }
                                }
                            }
                        }
                    }
                }

                Section("Manual Connect") {
                    HStack {
                        TextField("PC IP address (e.g. 192.168.1.5)", text: $manualIP)
                            .keyboardType(.asciiCapable)
                            .autocorrectionDisabled()
                            .textInputAutocapitalization(.never)
                        if !manualIP.isEmpty {
                            Button("Connect") {
                                connection.connect(to: manualIP, port: 7779)
                                dismiss()
                            }
                            .buttonStyle(.borderedProminent)
                        }
                    }
                    Text("Enter the IP shown in the Windows app (e.g. PC: 192.168.1.5:7779)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Section("TCP Fallback") {
                    HStack {
                        Image(systemName: "cable.connector")
                        Text("TCP port \(USBTransport.port)")
                        Spacer()
                        Text("Connects on manual IP")
                            .foregroundStyle(.secondary)
                    }
                }

                Section("Status") {
                    HStack {
                        Text("State")
                        Spacer()
                        Text(stateDescription)
                            .foregroundStyle(.secondary)
                    }
                    if connection.latencyMs > 0 {
                        HStack {
                            Text("Latency")
                            Spacer()
                            Text("\(connection.latencyMs) ms")
                                .foregroundStyle(.secondary)
                        }
                    }
                }

                if connection.state != .idle {
                    Section {
                        Button("Disconnect", role: .destructive) {
                            connection.disconnect()
                            dismiss()
                        }
                    }
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }

    private var stateDescription: String {
        switch connection.state {
        case .idle:              return "Not connected"
        case .discovering:       return "Searching..."
        case .connecting(let h): return "Connecting to \(h)"
        case .connected:         return "Ready"
        case .streaming:         return "Streaming"
        case .error(let msg):    return msg
        }
    }
}
