# universal-cam — Xcode Context Document

> **How to use this file:** Paste the contents into Claude Code on your Mac as context before starting work on the iOS side. This document gives a complete picture of the project, the iOS architecture, and how to connect to the Windows side.

---

## Project Overview

**universal-cam** is an iPhone-as-Windows-webcam app. The iPhone streams H.264 video and AAC audio to a Windows PC over QUIC (Wi-Fi or USB). The Windows PC exposes them as a virtual webcam and microphone — visible to Teams, Zoom, OBS, Slack, Discord, and any other app that uses DirectShow or Windows Media Foundation.

This is a **monorepo**. The same git repo contains:
- `src/` — Windows desktop app (WinUI 3 / C# / .NET 8)
- `ios/` — iPhone companion app (SwiftUI / Swift 5.9 / iOS 16+)

Both sides implement the same wire protocol (defined below). They are developed in parallel: Windows in VS Code, iOS in Xcode.

---

## Mac / Xcode Setup (First Time)

### Prerequisites
- macOS 13 Ventura or later
- Xcode 15.0 or later (iOS 16 SDK)
- Homebrew

### Steps
```bash
# 1. Install XcodeGen (generates .xcodeproj from project.yml)
brew install xcodegen

# 2. Clone the repo
git clone <repo-url>
cd universal-cam

# 3. Generate the Xcode project
cd ios
xcodegen generate

# 4. Open in Xcode
open UniversalCamPhone.xcodeproj

# 5. In Xcode: select your iPhone as target, Cmd+R to build and run
```

### Signing
1. In Xcode, select `UniversalCamPhone` target → Signing & Capabilities
2. Set your Apple Developer Team
3. Xcode will auto-manage provisioning profiles

### Re-generating the project
Run `xcodegen generate` again any time `project.yml` changes (new files added, settings changed).
The `.xcodeproj` is excluded from git — always regenerate on a fresh clone.

---

## iOS App Architecture

```
UniversalCamPhone/
  App/
    UniversalCamPhoneApp.swift     @main entry; creates StateObjects; injects EnvironmentObjects
    AppDelegate.swift              AVAudioSession setup; background task registration
  Camera/
    CameraSession.swift            AVCaptureSession wrapper (ObservableObject)
    VideoEncoder.swift             VideoToolbox H.264 encoder; emits Annex B NAL units
  Transport/
    ConnectionManager.swift        State machine; owns QuicTransport + BonjourDiscovery + encoder wiring
    QuicTransport.swift            Network.framework NWConnection with QUIC; binary frame protocol
    BonjourDiscovery.swift         Bonjour mDNS discovery + advertisement
  Audio/
    AudioCapture.swift             AVAudioEngine input tap; TODO: AAC-LC encoding (Phase 2)
  UI/
    ContentView.swift              SwiftUI root: preview + status overlay + controls + settings sheet
    CameraPreviewView.swift        UIViewRepresentable for AVCaptureVideoPreviewLayer
  UniversalCamPhone.entitlements   Network client/server + multicast entitlements
```

### State Flow
```
App launch
  └─> ConnectionManager.startDiscovery()
        └─> BonjourDiscovery searches for _universalcam._tcp on LAN
              └─> [Windows PC found] → ConnectionManager.connect(to: host)
                    └─> QuicTransport connects to port 7779
                          └─> [QUIC connected] → send "hello" control message
                                └─> [receive "welcome"] → state = .connected
                                      └─> User taps stream button
                                            └─> ConnectionManager.startStreaming()
                                                  └─> CameraSession frames → VideoEncoder → QuicTransport
```

### Threading Model
- **Main thread:** UI updates, @Published mutations
- **sessionQueue** (`com.universalcam.camera.session`): AVCaptureSession config + frame callbacks
- **encoderQueue** (`com.universalcam.videoencoder`): VTCompressionSession encode calls
- **quicQueue** (`com.universalcam.quic`, `.userInteractive`): NWConnection send/receive

Never call `captureSession.startRunning()` or `VTCompressionSessionEncodeFrame` on the main thread.

---

## Wire Protocol

Both the iOS Swift and Windows C# sides implement this protocol exactly.

**Transport:** QUIC (Network.framework on iOS, MsQuic on Windows)
**Port:** 7779
**Connection initiator:** iPhone (client) connects to Windows PC (server)

---

### Stream 0 — Control Channel
Bidirectional. Newline-delimited JSON messages (`\n` = 0x0A).

**iPhone → Windows:**
```json
{"type":"hello","version":1,"deviceName":"iPhone 15 Pro","capabilities":["h264","hevc","stereo_audio"]}
{"type":"configure","resolution":"1080p","fps":30,"codec":"h264","bitrate":8000000}
{"type":"start_stream"}
{"type":"stop_stream"}
{"type":"ping","ts":1711234567890}
```

**Windows → iPhone:**
```json
{"type":"welcome","version":1}
{"type":"configure_ack","resolution":"1080p","fps":30}
{"type":"pong","ts":1711234567890}
```

**Handshake sequence:**
1. iPhone connects via QUIC
2. iPhone sends `hello`
3. Windows responds `welcome`
4. iPhone optionally sends `configure` (resolution/fps/codec/bitrate)
5. Windows responds `configure_ack`
6. iPhone sends `start_stream`
7. iPhone begins sending video frames on Stream 1 and audio frames on Stream 2

---

### Stream 1 — Video Frames (iPhone → Windows)
Binary. Each frame:
```
Offset  Size  Type      Description
------  ----  --------  -----------
0       4     uint32LE  payload_length  (bytes of NAL data that follow the 13-byte header)
4       8     int64LE   pts_us          (presentation timestamp, microseconds since epoch)
12      1     uint8     flags           bit0=1 → keyframe; bit1=1 → HEVC (else H.264)
13      N     bytes     H.264 Annex B NAL unit(s)
```

**H.264 format:** Annex B (start-code `00 00 00 01` prefix). Baseline profile, no B-frames
(`kVTCompressionPropertyKey_AllowFrameReordering = false`).

**Keyframe interval:** Every 2 seconds (60 frames at 30fps) or when `flags.bit0 = 1`.

---

### Stream 2 — Audio Frames (iPhone → Windows)
Binary. Each frame:
```
Offset  Size  Type      Description
------  ----  --------  -----------
0       4     uint32LE  payload_length
4       8     int64LE   pts_us
12      1     uint8     channels    (1 = mono, 2 = stereo)
13      N     bytes     AAC-LC raw frame (Phase 2 — PCM placeholder in Phase 1)
```

**Audio format:** AAC-LC, 44100 Hz, stereo preferred. Fall back to mono if only one mic.

---

## Phase Roadmap (iOS perspective)

### Phase 1 (Weeks 1–4) — Foundation
**Goal:** App launches, camera preview works, Bonjour discovery runs, QUIC connects.
- [x] Project scaffold (this commit)
- [ ] Camera permission request flow
- [ ] CameraSession starts and preview renders
- [ ] BonjourDiscovery finds Windows app on LAN
- [ ] QUIC connection established (hello/welcome handshake)
- [ ] Basic status UI works (state pill, connect/disconnect)

### Phase 2 (Weeks 5–10) — Transport & Stream
**Goal:** Video reaches Windows, first frame decoded there.
- [ ] VideoEncoder emitting Annex B frames
- [ ] QuicTransport sending video frames (Stream 1)
- [ ] AAC-LC encoding in AudioCapture (replace PCM placeholder)
- [ ] QuicTransport sending audio frames (Stream 2)
- [ ] Resolution picker wired to CameraSession.setResolution
- [ ] Manual exposure, ISO, white balance (AVCaptureDevice manual controls)
- [ ] HEVC codec option (kCMVideoCodecType_HEVC)

### Phase 3 (Weeks 11–16) — AI & Differentiation
- [ ] Portrait mode / background blur (AVDepthData + Metal)
- [ ] CoreML face tracking / Center Stage clone (Vision PoseNet)
- [ ] Multi-iPhone director mode (AVCaptureMultiCamSession, iPhone 11+)
- [ ] AI noise reduction (MetalPerformanceShaders temporal denoiser)
- [ ] LUT / cinematic filter (Metal 3D LUT pass)

### Phase 4 (Weeks 17–22) — Platform
- [ ] Android companion app (separate repo, CameraX)
- [ ] Apple Vision Pro support (visionOS app)

---

## Key APIs Reference

### AVFoundation
```swift
// Camera session
AVCaptureSession            → see CameraSession.swift
AVCaptureDevice             → device selection, manual controls (exposure, ISO, WB, focus)
AVCaptureVideoDataOutput    → raw YUV frame callbacks
AVCaptureMultiCamSession    → multi-camera (Phase 3, requires iPhone 11+, entitlement)

// Background operation
UIBackgroundModes: audio    → keeps session alive when screen locks
UIBackgroundModes: voip     → keeps network connection alive
```

### VideoToolbox
```swift
VTCompressionSession        → H.264/HEVC hardware encoder → see VideoEncoder.swift
VTCompressionSessionCreate  → init with kCMVideoCodecType_H264 or kCMVideoCodecType_HEVC
kVTCompressionPropertyKey_RealTime → must be true for live streaming
kVTCompressionPropertyKey_AllowFrameReordering → false (no B-frames, reduces latency)
```

### Network.framework (QUIC)
```swift
NWConnection(to:using:)     → see QuicTransport.swift
NWProtocolQUIC.Options(alpn:) → ALPN: "universalcam/1"
NWParameters(quic:)         → pass TLS + QUIC options
// Requires iOS 15+. TLS certificate required; use self-signed in dev.
```

### CoreML / Vision (Phase 3)
```swift
VNCoreMLRequest             → run CoreML model on CVPixelBuffer
VNDetectHumanBodyPoseRequest → body pose for Center Stage clone
AVDepthData                 → depth map from dual-camera for portrait bokeh
```

---

## Connecting to the Windows App for Testing

### On the same Wi-Fi network
1. Run the Windows app (VS Code: `Ctrl+Shift+B` then `dotnet run`)
2. Run the iPhone app on device
3. The iPhone automatically discovers the PC via Bonjour
4. Tap the PC name in Settings → the app connects and starts streaming

### USB (Phase 2)
USB connection uses the Apple Devices (libimobiledevice) tunnel. The iPhone shows up as a network interface on Windows (`169.254.x.x` link-local). Connect to that IP directly until the full USB abstraction is built.

### Debugging latency
- The status pill shows one-way latency (RTT/2) from ping/pong messages
- Target: < 100 ms USB, < 150 ms Wi-Fi end-to-end glass-to-glass

---

## Coding Conventions

### Match the Windows side naming where it matters
| iOS (Swift)              | Windows (C#)               |
|---|---|
| `ConnectionManager`      | `ConnectionManager.cs`     |
| `QuicTransport`          | `QuicTransport.cs`         |
| `ControlMessage.hello`   | `ControlMessage.Hello`     |
| `VideoEncoder.EncodedFrame` | `VideoFrame` struct     |
| Port `7779`              | Port `7779`                |

### Swift style
- `final class` for performance-sensitive types (CameraSession, VideoEncoder, QuicTransport)
- `ObservableObject` + `@Published` for UI-driven state
- `DispatchQueue` named with `com.universalcam.*` prefix
- No force unwraps in transport/encoder paths — use `guard let` + early return
- TODOs tagged `// TODO: Phase N — description`

---

## File Checklist for Xcode

After running `xcodegen generate`, Xcode should show:

```
UniversalCamPhone
  ├── App
  │   ├── UniversalCamPhoneApp.swift
  │   └── AppDelegate.swift
  ├── Camera
  │   ├── CameraSession.swift
  │   └── VideoEncoder.swift
  ├── Transport
  │   ├── ConnectionManager.swift
  │   ├── QuicTransport.swift
  │   └── BonjourDiscovery.swift
  ├── Audio
  │   └── AudioCapture.swift
  ├── UI
  │   ├── ContentView.swift
  │   └── CameraPreviewView.swift
  └── UniversalCamPhone.entitlements
```

If files are missing from the Xcode navigator, re-run `xcodegen generate`.

---

## Known Issues / TODOs at Scaffold Stage

1. **QUIC TLS cert:** `QuicTransport.swift` accepts any cert in dev (`verify_block` always returns true). Replace with proper certificate pinning before shipping.
2. **AAC encoding:** `AudioCapture.swift` sends raw PCM as placeholder. Wire up `AVAudioConverter` → AAC-LC in Phase 2.
3. **USB support:** Phase 2. USB NCM/RNDIS interface detection not yet implemented on either side.
4. **Multicast entitlement:** `com.apple.developer.networking.multicast` requires explicit Apple approval for App Store distribution. Fine for TestFlight and development.
5. **`ControlMessage` Codable conformance:** The enum in `ConnectionManager.swift` has a partial `Codable` stub. Full encoding/decoding needs implementation before the handshake works end-to-end.

---

*Last updated: 2026-03-15 — Windows side: VS Code / .NET 8 / WinUI 3 — iOS side: Xcode 15 / Swift 5.9 / iOS 16*
