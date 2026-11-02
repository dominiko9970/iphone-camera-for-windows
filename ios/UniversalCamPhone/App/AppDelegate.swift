import UIKit
import AVFoundation
import BackgroundTasks

class AppDelegate: NSObject, UIApplicationDelegate {

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        configureAudioSession()
        registerBackgroundTasks()
        return true
    }

    // MARK: - Audio Session

    private func configureAudioSession() {
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(
                .playAndRecord,
                mode: .videoRecording,
                options: [.allowBluetoothA2DP, .mixWithOthers]
            )
            try session.setActive(true)
        } catch {
            print("[AppDelegate] AVAudioSession setup failed: \(error)")
        }
    }

    // MARK: - Background Tasks

    private func registerBackgroundTasks() {
        BGTaskScheduler.shared.register(
            forTaskWithIdentifier: "com.universalcam.phone.stream-keepalive",
            using: nil
        ) { task in
            task.setTaskCompleted(success: true)
        }
    }

    func applicationDidEnterBackground(_ application: UIApplication) {
        // AVCaptureSession continues running in background via UIBackgroundModes: audio + voip
    }
}
