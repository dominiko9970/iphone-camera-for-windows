# universal-cam

Use your iPhone as a camera and microphone source for a Windows app over Wi-Fi (QUIC) or USB tunnel (TCP).

> Status: Active prototype (Phase 1–2).

---

## What Is In The Repo

This repository contains both sides of the system:

- iOS app (SwiftUI): camera/audio capture, H.264 + AAC encoding, Bonjour discovery, QUIC/TCP transport.
- Windows app (WPF): stream session orchestration, transport listeners, protocol parsing, preview UI, virtual camera output.

Core transport/protocol implementation lives in `src/UniversalCam.Core`.
Virtual camera output lives in `src/UniversalCam.VirtualCamera`.

---

## Current Features

- iPhone app advertises/discovers with Bonjour (`_universalcam._tcp`).
- Wi-Fi transport via QUIC on port `7779`.
- USB path via localhost TCP tunnel on port `7780`.
- Unified control protocol (JSON control messages + stream-type multiplexing).
- PC→iOS bidirectional messaging over the iPhone-opened QUIC stream.
- H.264 Annex B decode on Windows via FFmpeg (libavcodec software decoder).
- SAR-corrected YUV→BGRA32 conversion via `sws_scale` (BT.709).
- Virtual camera output via Windows 11 MF `IMFVirtualCamera` (no driver signing required).
- Video and audio frame parsing on Windows.
- Basic Windows preview window with connection state and transport badge.
- Camera/microphone permission flow and settings entry points on iOS.
- Camera controls: switch between 1080p and 4K stream configuration from the Windows UI.
- iOS keeps screen awake while streaming.
- Single-instance enforcement on Windows to avoid duplicate listeners.

### Latest Updates (March 2026)

- QUIC PC→iOS messaging now uses the iPhone-opened bidirectional stream (fixes handshake on iOS where server-initiated streams are unavailable).
- H.264 decode upgraded from placeholder to full FFmpeg software pipeline with keyframe gating and BT.709-correct color conversion.
- Virtual camera project added: `IMFVirtualCamera` (Windows 11 22H2+) with DirectShow fallback for older Windows.
- Audio output pipeline wired up via NAudio WASAPI.
- iOS `Hello` is now sent on `.ready`, completing the QUIC handshake without a server-initiated stream.

---

## Known Prototype Limitations

- MF virtual camera (`IMFVirtualCamera`) requires Windows 11 22H2 or newer and COM registration — currently fails with `E_NOINTERFACE` in some environments.
- Audio playback/rendering pipeline on Windows is not complete yet.
- USB requires a local forwarding setup and Apple mobile device support on Windows.
- Protocol and app UX are still evolving.

---

## Requirements

### Windows

- Windows 10 version 2004 (build `19041`) or newer
- Windows 11 22H2+ for virtual camera output (`IMFVirtualCamera`)
- .NET 8 SDK
- Visual Studio 2022 Build Tools (Desktop .NET workloads)

### iOS

- Xcode 15+
- iOS 15+
- Physical iPhone for camera/mic testing

---

## Build And Run

### Windows

```powershell
# restore
dotnet restore universal-cam.sln

# build
dotnet build universal-cam.sln --configuration Debug

# run the Windows app
dotnet run --project src\UniversalCam\UniversalCam.csproj

# tests
dotnet test universal-cam.sln
```

### iOS

1. Open `ios/UniversalCamPhone.xcodeproj` in Xcode.
2. Select a real iPhone target.
3. Build and run.
4. Grant camera/microphone/local-network permissions.

---

## Project Structure

```text
universal-cam/
  src/
    UniversalCam/              # Windows WPF app UI
    UniversalCam.Core/         # Shared Windows core logic
      Discovery/               # Bonjour advertisement
      Protocol/                # Control messages + frame header model
      Transport/               # QUIC server, TCP USB transport, parser
      Video/                   # H.264 decode pipeline (FFmpeg/libavcodec)
    UniversalCam.VirtualCamera/ # Virtual camera output
      MfVirtualCameraServer    # IMFVirtualCamera (Windows 11 22H2+)
      DsVirtualCameraServer    # DirectShow fallback (older Windows)
      SharedMemoryBridge       # Frame handoff between app and camera server
  ios/
    UniversalCamPhone/         # iOS app (capture, encode, transport, UI)
  tests/
    UniversalCam.Tests/
  docs/
  ROADMAP.md
```

---

## Wire Protocol Summary

- Stream type byte first:
  - `0x00` control (JSON + newline)
  - `0x01` video frame
  - `0x02` audio frame
- Binary media frame header is 14 bytes:
  - stream type (1)
  - payload length (4, little-endian)
  - PTS in microseconds (8, little-endian)
  - flags (1)

See `ios/UniversalCamPhone/Transport/PROTOCOL.md` for full details.

---

## Tech Stack

| Area | Technology |
|---|---|
| Windows UI | WPF (.NET 8, x64) |
| Windows transport | `System.Net.Quic`, `TcpClient` |
| Windows discovery | `Makaretu.Dns.Multicast` |
| Windows video decode | FFmpeg via `Sdcb.FFmpeg` (libavcodec H.264 software) |
| Windows virtual camera | MF `IMFVirtualCamera` / DirectShow |
| Windows audio output | NAudio WASAPI |
| iOS UI | SwiftUI |
| iOS media | AVFoundation, VideoToolbox |
| iOS networking | Network.framework (`NWConnection`, `NWListener`, `NWBrowser`) |
| Serialization | `System.Text.Json` (Windows), `Codable` (iOS) |

---

## Contributing

Contributions are welcome.

- Start with `CONTRIBUTING.md` for setup, workflow, and pull request checklist.
- Please follow `CODE_OF_CONDUCT.md` in all project interactions.
- For major protocol or architecture changes, open an issue first to align on direction.

---

## License

This project is open source under the MIT License.

See `LICENSE` for full text.

---

## Third-Party Notices

For open-source dependency attributions and framework notices, see
`THIRD_PARTY_NOTICES.md`.
