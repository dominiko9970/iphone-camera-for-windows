import SwiftUI

@main
struct UniversalCamPhoneApp: App {

    @UIApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    @StateObject private var connectionManager = ConnectionManager()
    @StateObject private var cameraSession     = CameraSession()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(connectionManager)
                .environmentObject(cameraSession)
                .onAppear {
                    // Wire video frames: Camera → ConnectionManager → Encoder → Transport
                    cameraSession.onVideoSampleBuffer = { [weak connectionManager] sample in
                        connectionManager?.handleVideoSample(sample)
                    }
                    // Give ConnectionManager access to camera for PC-initiated switching
                    connectionManager.cameraSession = cameraSession
                }
        }
    }
}
